#if NET48
// =====================================================================================
//  net48 的 hosts 覆盖请求改写
//
//  net8 靠 SocketsHttpHandler.ConnectCallback 在「TCP 连接」这一层做重定向，
//  URI 和 Host 头完全不变。net48 没有这个钩子，只能在「HTTP 请求」这一层模拟：
//
//    1. 把 request.RequestUri 的 host 换成覆盖表里的 IP
//    2. 显式设置 Host 头为原始域名（否则 HTTP/1.1 会把 Host 发成 IP，虚拟主机拒绝）
//    3. 给 handler 挂证书校验回调，按原始域名校验证书（否则 TLS 会因 IP≠域名失败）
//
//  限制：TLS 的 SNI 会是 IP。绝大多数 CDN（含 GitHub）在拿到 Host 头后
//  仍能正确路由，只有极少数启用「SNI 必须匹配」的站点会失败 ——
//  那种情况本就有镜像线路兜底。
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Net.Http;

namespace YunX.Core.Net
{
    /// <summary>把 hosts 覆盖应用到 <see cref="HttpRequestMessage"/> 上。</summary>
    internal static class HostsRewrite48
    {
        /// <summary>当前请求被改写过的原始 host（供证书校验回调使用）。</summary>
        [ThreadStatic]
        private static string? _currentOriginalHost;

        /// <summary>
        /// 若目标 host 在覆盖表里，改写 request 的 URI 并补 Host 头。
        /// </summary>
        /// <returns>true 表示发生了改写。</returns>
        public static bool Apply(HttpRequestMessage request)
        {
            if (request?.RequestUri == null) return false;

            var rewritten = HostsOverride48.TryRewrite(request.RequestUri);
            if (rewritten == null) return false;

            var (uri, hostHeader) = rewritten.Value;
            _currentOriginalHost = request.RequestUri.Host;
            request.RequestUri = uri;

            // Host 头必须显式设置：HTTP/1.1 默认用 URI 的 host（此时是 IP），
            // 而 GitHub/CDN 都按 Host 分流，发 IP 会拿到 404 或直接断开。
            request.Headers.Host = hostHeader;
            return true;
        }

        /// <summary>证书校验回调里取「这次请求原本的域名」。</summary>
        public static string? CurrentOriginalHost => _currentOriginalHost;

        /// <summary>请求结束后清理（避免线程复用时串味）。</summary>
        public static void Reset() => _currentOriginalHost = null;
    }
}
#endif
