using System.Net;
using System.Net.Sockets;
using YunX.Core.Util;

namespace YunX.Core.Net;

/// <summary>
/// hosts 劫持绕过（DNS over HTTPS）。
///
/// 背景：部分机器把 <c>github.com</c> / <c>raw.githubusercontent.com</c> 等写进
/// <c>C:\Windows\System32\drivers\etc\hosts</c> 指向 <c>127.0.0.1</c>，导致所有请求连本地而失败。
///
/// 本实现比 Python 版**更干净**：不改 <c>socket.getaddrinfo</c>（那是全局副作用），
/// 而是通过 <see cref="SocketsHttpHandler.ConnectCallback"/> 精确控制 TCP 建连目标。
/// TLS 的 SNI / 证书校验仍使用**原域名**（HttpClient 用 URI 的 Host 做握手），
/// 因此绕过 hosts 不等于降低安全性，也不会污染进程内其他网络调用。
/// </summary>
public static class HostsBypass
{
    /// <summary>会被 hosts 劫持、且下载链路需要直连的域名。</summary>
    public static readonly string[] WatchedHosts =
    {
        "github.com",
        "api.github.com",
        "raw.githubusercontent.com",
        "codeload.github.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "gist.github.com",
        "gist.githubusercontent.com",
    };

    private static readonly HashSet<string> Loopback = new(StringComparer.OrdinalIgnoreCase)
    {
        "127.0.0.1", "127.0.0.2", "0.0.0.0", "::1",
    };

    /// <summary>DoH 解析结果：host → 真实 IP。空表示无需绕过或解析失败。</summary>
    public static IReadOnlyDictionary<string, IPAddress> Overrides { get; private set; }
        = new Dictionary<string, IPAddress>();

    /// <summary>
    /// 检测哪些被监控域名当前被解析到本机回环地址（即被 hosts 劫持）。
    /// </summary>
    public static IReadOnlyList<(string Host, string Ip)> DetectHijack()
    {
        var list = new List<(string, string)>();
        foreach (var host in WatchedHosts)
        {
            try
            {
                var addrs = Dns.GetHostAddresses(host);
                if (addrs.Length == 0) continue;
                var ip = addrs[0].ToString();
                if (Loopback.Contains(ip)) list.Add((host, ip));
            }
            catch (SocketException)
            {
                // 解析不出来的域名不算劫持（可能只是当前网络不通）
            }
        }
        return list;
    }

    /// <summary>
    /// 用 DoH 解析真实 IP 并装载覆盖表。
    /// </summary>
    /// <returns>生效的 host→IP 映射；空表示未检测到劫持或全部解析失败。</returns>
    public static async Task<IReadOnlyDictionary<string, IPAddress>> EnableAsync(
        Action<string>? log = null, CancellationToken ct = default)
    {
        var hijacked = DetectHijack();
        if (hijacked.Count == 0)
        {
            Overrides = new Dictionary<string, IPAddress>();
            return Overrides;
        }

        log?.Invoke("⚠ 检测到 hosts 把这些域名指向本机：" +
                    string.Join("、", hijacked.Select(h => h.Host)));

        var map = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        using var doh = new HttpClient(new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(5),
            // DoH 必须直连：走系统代理会再次撞上被污染的 hosts
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.All,
        })
        { Timeout = TimeSpan.FromSeconds(6) };

        foreach (var (host, _) in hijacked)
        {
            var ip = await ResolveViaDohAsync(doh, host, ct).ConfigureAwait(false);
            if (ip != null)
            {
                map[host] = ip;
                log?.Invoke($"DoH {host} → {ip}");
            }
        }

        Overrides = map;
        if (map.Count == 0)
        {
            log?.Invoke("DoH 解析也失败：建议清掉 hosts 里的 GitHub 条目，或改用镜像线路");
        }
        else
        {
            log?.Invoke("已启用真实 IP 直连：" +
                        string.Join("、", map.Select(kv => $"{kv.Key}→{kv.Value}")));
        }
        return map;
    }

    /// <summary>清空覆盖表（切换为直连/关掉该功能时调用）。</summary>
    public static void Disable() => Overrides = new Dictionary<string, IPAddress>();

    private static readonly (string Name, string Url, string Accept)[] DohEndpoints =
    {
        ("aliyun", "https://223.5.5.5/resolve?name={0}&type=A", "application/dns-json"),
        ("dnspod", "https://doh.pub/dns-query?name={0}&type=A", "application/dns-json"),
    };

    private static async Task<IPAddress?> ResolveViaDohAsync(
        HttpClient client, string host, CancellationToken ct)
    {
        foreach (var (name, tpl, accept) in DohEndpoints)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get,
                    string.Format(tpl, Uri.EscapeDataString(host)));
                req.Headers.TryAddWithoutValidation("Accept", accept);
                using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;

                using var doc = await System.Text.Json.JsonDocument
                    .ParseAsync(await resp.Content.ReadAsStreamAsync(ct),
                        cancellationToken: ct).ConfigureAwait(false);

                if (!doc.RootElement.TryGetProperty("Answer", out var answers)) continue;
                foreach (var ans in answers.EnumerateArray())
                {
                    // type 1 = A 记录；跳过 AAAA（type 28），避免拿到 IPv6 而当前网络不通
                    if (!ans.TryGetProperty("type", out var t) || t.GetInt32() != 1) continue;
                    if (!ans.TryGetProperty("data", out var d)) continue;
                    var s = d.GetString();
                    if (IPAddress.TryParse(s, out var ip)) return ip;
                }
            }
            catch
            {
                // 换下一个 DoH 端点
            }
        }
        return null;
    }
}
