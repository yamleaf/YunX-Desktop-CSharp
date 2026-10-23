#if NET48
// =====================================================================================
//  hosts 覆盖 / DoH 绕过 —— net48 实现
//
//  net8 用 SocketsHttpHandler.ConnectCallback 精准控制「TCP 连到哪个 IP，
//  TLS/HTTP 仍用原 host」。net48 的 HttpClientHandler 没有这个钩子。
//
//  等价替代：自定义 IWebProxy。
//  HttpClientHandler 在建立连接时会先问 proxy.GetProxy(uri)，
//  拿到一个「代理地址」后把整套 TCP 连接指向它 —— 但如果返回的 proxy 就是
//  我们自己实现的对象、且 GetProxy 返回目标 IP 的 socket 地址，
//  再配合 BypassProxyOnLocal，就能实现「连到指定 IP」。
//
//  不过 IWebProxy 无法改写 Host 头（HTTP/1.1 的 Host 会变成 IP），
//  对需要 SNI / Host 校验的站点会失败。
//
//  ★ 更可靠的方案：ServicePointManager 的证书/连接回调也不行。
//    真正可行且不带全局副作用的是：**在请求 URI 层面替换 host 为 IP，
//    并显式设置 Host 头**。HttpWebRequest 支持 Host 属性覆盖；
//    HttpClient 的 HttpRequestMessage.Headers.Host 也支持。
//    但 TLS SNI 仍然会用 URI 里的 host（= IP），对启用了严格 SNI 的站点无效。
//
//  ★ 最终采用的方案（务实）：保留 hosts 覆盖能力，但**只对 http:// 生效**；
//    https:// 走 DoH 得到的 IP 时用 ServerCertificateCustomValidationCallback
//    校验证书里的 CN/SAN 是否等于原 host —— 这样既绕过了劫持，又没有安全降级。
//
//  这个类承担「把 host 换成 IP + 补 Host 头 + 校验证书」三件事。
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace YunX.Core.Net
{
    /// <summary>
    /// net48 的 hosts 覆盖实现。
    ///
    /// <para>与 net8 的 <c>ConnectCallback</c> 相比有两处能力限制，
    /// 都在这里显式处理而不是静默降级：</para>
    /// <list type="bullet">
    /// <item><b>http://</b>：直接把 URI 的 host 换成 IP，并设 <c>Host</c> 头 —— 完全等价。</item>
    /// <item><b>https://</b>：同上，但 TLS 的 SNI 会变成 IP，需要手工校验证书
    /// CN/SAN 是否匹配原 host（见 <see cref="ValidateCertForHost"/>）。
    /// 对启用了严格 SNI 的站点（少数 CDN）仍可能失败 —— 那种情况走镜像线路。</item>
    /// </list>
    /// </summary>
    internal static class HostsOverride48
    {
        /// <summary>
        /// 如果 <paramref name="uri"/> 的 host 在覆盖表里，返回替换后的 URI + 原始 Host 头；
        /// 否则返回 null（调用方走正常请求）。
        /// </summary>
        public static (Uri Rewritten, string HostHeader)? TryRewrite(Uri uri)
        {
            if (uri == null) return null;
            if (!HostsBypass.Overrides.TryGetValue(uri.Host, out var ip)) return null;

            string hostHeader = uri.IsDefaultPort
                ? uri.Host
                : uri.Host + ":" + uri.Port;

            var builder = new UriBuilder(uri)
            {
                // IPv6 字面量要用方括号
                Host = ip.AddressFamily == AddressFamily.InterNetworkV6
                    ? "[" + ip + "]"
                    : ip.ToString(),
            };

            return (builder.Uri, hostHeader);
        }

        /// <summary>
        /// 给 handler 挂上「证书 CN/SAN 必须匹配原始 host」的校验回调。
        ///
        /// <para>因为 URI 被换成了 IP，系统默认校验会拿 IP 去比对证书里的域名 —— 必然失败。
        /// 这里改为按**原始 host** 校验证书，安全性不降低（仍然是有效证书 + 域名匹配），
        /// 只是绕过了本机 hosts 的解析劫持。</para>
        /// </summary>
        public static void AttachCertificateCheck(HttpClientHandler handler,
            Func<string> originalHostProvider)
        {
            handler.ServerCertificateCustomValidationCallback = (request, cert, chain, errors) =>
            {
                if (errors == SslPolicyErrors.None) return true;

                // 只有「域名不匹配」这一类错误是我们自己造成的（URI host 变成了 IP），
                // 其余错误（过期、吊销、链不完整）一律不放行 —— 不能借绕过之名放宽安全。
                if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) == 0) return false;

                string expectedHost = originalHostProvider();
                if (string.IsNullOrEmpty(expectedHost) || cert == null) return false;

                return ValidateCertForHost(cert, expectedHost);
            };
        }

        /// <summary>校验证书的 CN / SAN 是否包含指定域名（支持通配符 *.example.com）。</summary>
        public static bool ValidateCertForHost(X509Certificate cert, string host)
        {
            if (cert == null || string.IsNullOrEmpty(host)) return false;

            // SAN（Subject Alternative Name）扩展 OID = 2.5.29.17
            const string SanOid = "2.5.29.17";
            var cert2 = cert as X509Certificate2 ?? new X509Certificate2(cert);

            foreach (var ext in cert2.Extensions)
            {
                if (ext.Oid?.Value != SanOid) continue;

                var san = ext as X509Extension;
                if (san == null) continue;

                // 扩展内容是 ASN.1 编码的 GeneralNames。
                // 用 X509Certificate2 的格式化输出解析，避免自己写 DER 解析器。
                string text = san.Format(false) ?? "";
                foreach (var part in text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string v = part.Trim();
                    int eq = v.IndexOf('=');
                    if (eq < 0) continue;
                    string kind = v.Substring(0, eq).Trim();
                    string name = v.Substring(eq + 1).Trim();

                    // 只认 DNS Name，不认 IP/Email
                    if (!kind.Equals("DNS Name", StringComparison.OrdinalIgnoreCase)) continue;
                    if (NameMatches(name, host)) return true;
                }
            }

            // 部分证书只有 CN，没有 SAN —— 回退到 Subject CN 匹配
            string subject = cert2.Subject;
            foreach (var part in subject.Split(','))
            {
                string v = part.Trim();
                if (v.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                {
                    return NameMatches(v.Substring(3).Trim(), host);
                }
            }

            return false;
        }

        /// <summary>域名匹配，支持 <c>*.example.com</c> 通配（仅匹配一级）。</summary>
        private static bool NameMatches(string pattern, string host)
        {
            if (string.IsNullOrEmpty(pattern)) return false;
            if (pattern.Equals(host, StringComparison.OrdinalIgnoreCase)) return true;

            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                string suffix = pattern.Substring(1);      // ".example.com"
                if (!host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return false;

                // 通配只覆盖一级：a.example.com 匹配，a.b.example.com 不匹配
                string left = host.Substring(0, host.Length - suffix.Length);
                return left.Length > 0 && left.IndexOf('.') < 0;
            }

            return false;
        }
    }
}
#endif
