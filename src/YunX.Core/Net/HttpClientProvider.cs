using System.Net;
using System.Net.Sockets;
using YunX.Core.Util;

namespace YunX.Core.Net;

/// <summary>联网模式。</summary>
public enum NetworkMode
{
    /// <summary>启动时探测「直连」与「系统代理」哪条通，自动选。</summary>
    Auto = 0,
    /// <summary>忽略系统代理，强制直连（配合 DoH 绕 hosts 时通常最优）。</summary>
    Direct = 1,
    /// <summary>尊重 HTTP_PROXY / 系统代理。</summary>
    Proxy = 2,
}

/// <summary>
/// HTTP 客户端工厂 + 联网模式自动探测。
///
/// 为什么需要这一层：本项目在实测中发现，**「直连」和「走系统代理」的表现可能完全相反** ——
/// 如果 hosts 把 GitHub 域名指到 127.0.0.1，系统代理自己在解析域名时也会撞上这些假条目，
/// 于是走代理反而 SSL 失败，而忽略代理 + DoH 直连却完全正常。
/// 各模块各自 <c>new HttpClient()</c> 会导致行为不一致，故统一从这里取。
/// </summary>
public sealed class HttpClientProvider : IDisposable
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/124.0 Safari/537.36";

    /// <summary>用于探测连通性的稳定小文件（走镜像，避开可能被墙/被污染的 raw 直连）。</summary>
    private static readonly string[] ProbeUrls =
    {
        "https://ghproxy.net/https://raw.githubusercontent.com/octocat/Hello-World/master/README",
        "https://ghfast.top/https://raw.githubusercontent.com/octocat/Hello-World/master/README",
        "https://gh-proxy.com/https://raw.githubusercontent.com/octocat/Hello-World/master/README",
    };

    private readonly object _gate = new();
    private NetworkMode _mode;
    private NetworkMode _effective;

    /// <summary>
    /// 共享的 <see cref="SocketsHttpHandler"/> 缓存，key = (联网模式, 是否下载)。
    ///
    /// <para><b>为什么必须共享</b>：每个 <see cref="SocketsHttpHandler"/> 自带独立连接池，
    /// <b>跨 handler 的连接不能复用</b>。早期实现每次 <c>CreateClient()</c> 都 new 一个 handler，
    /// 于是 <c>MaxConnectionsPerServer = 64</c> 的池化完全失效 ——
    /// 单次下载至少建 2 个（探测 + 分片），单流降级时每条候选线路再各建 1 个，
    /// 每个都要重新 TCP + TLS 握手，8 线程下这笔开销能吃掉相当一部分带宽。</para>
    ///
    /// <para>key 里带 <c>forDownload</c>：下载必须 <c>AutomaticDecompression = None</c>
    /// （Range 按压缩前字节算偏移，自动解压会让写盘位置全错），
    /// 而普通 API 请求需要自动解压，两者不能共用同一个 handler。</para>
    /// </summary>
    private readonly Dictionary<(NetworkMode Mode, bool ForDownload, bool AllowRedirect, bool Http2),
        SocketsHttpHandler> _handlers = new();

    private bool _disposed;

    /// <summary>
    /// 是否启用 HTTP/2。
    ///
    /// <para><b>为什么放在 provider 上而不是每次调用传参</b>：HTTP/2 是
    /// <see cref="SocketsHttpHandler"/> 级别的开关，不能按请求改。设置页改了这个值
    /// 只需重新赋值，已缓存的 handler 会因 key 变化而重建。</para>
    ///
    /// <para><b>为什么默认关闭</b>：部分镜像/CDN 的 HTTP/2 实现有问题
    /// （流控窗口小、RST 频繁），表现为「握手成功但吞吐远低于 HTTP/1.1」。
    /// 开了之后 key 里多一维，两组 handler 各自独立缓存，不会互相污染。</para>
    /// </summary>
    public bool EnableHttp2 { get; set; }

    public HttpClientProvider(NetworkMode mode = NetworkMode.Auto)
    {
        _mode = mode;
        _effective = mode == NetworkMode.Proxy ? NetworkMode.Proxy : NetworkMode.Direct;
    }

    public NetworkMode Mode => _mode;
    public NetworkMode EffectiveMode => _effective;

    public string Describe() => _effective switch
    {
        NetworkMode.Proxy => "系统代理",
        _ => "直连（忽略系统代理）",
    };

    /// <summary>切换模式；Auto 会重新探测。</summary>
    public async Task SetModeAsync(NetworkMode mode, Action<string>? log = null,
        CancellationToken ct = default)
    {
        _mode = mode;
        if (mode != NetworkMode.Auto)
        {
            _effective = mode;
            log?.Invoke($"联网模式：{Describe()}（手动指定）");
            return;
        }
        await AutoDetectAsync(log, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 并行探测直连与系统代理，选能通的那条。
    /// 两者都通时**优先直连**（配合 DoH 时直连更稳，也不会被代理限速）。
    /// </summary>
    public async Task<NetworkMode> AutoDetectAsync(Action<string>? log = null,
        CancellationToken ct = default)
    {
        if (_mode != NetworkMode.Auto)
        {
            log?.Invoke($"联网模式：{Describe()}（手动指定）");
            return _effective;
        }

        var directTask = CanConnectAsync(NetworkMode.Direct, ct);
        var proxyTask = CanConnectAsync(NetworkMode.Proxy, ct);
        await Task.WhenAll(directTask, proxyTask).ConfigureAwait(false);

        bool directOk = directTask.Result;
        bool proxyOk = proxyTask.Result;

        lock (_gate)
        {
            _effective = directOk || !proxyOk ? NetworkMode.Direct : NetworkMode.Proxy;
        }

        log?.Invoke($"联网模式自动选择：{Describe()}" +
                    $"（直连{(directOk ? "通" : "不通")} / 代理{(proxyOk ? "通" : "不通")}）");
        return _effective;
    }

    private static async Task<bool> CanConnectAsync(NetworkMode mode, CancellationToken ct)
    {
        try
        {
            using var client = BuildEphemeralClient(mode, TimeSpan.FromSeconds(7));
            foreach (var url in ProbeUrls)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("Range", "bytes=0-127");
                    using var resp = await client.SendAsync(req,
                        HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    if (resp.StatusCode is HttpStatusCode.OK or HttpStatusCode.PartialContent)
                        return true;
                }
                catch
                {
                    // 试下一个探测地址
                }
            }
        }
        catch
        {
            // 探测整体失败 = 这条路不通
        }
        return false;
    }

    /// <summary>
    /// 创建一个按当前生效模式配置的客户端。
    /// </summary>
    /// <param name="forDownload">
    /// 下载专用：禁用自动解压（<c>Accept-Encoding: identity</c>），
    /// 否则压缩后的字节流长度与 Range 偏移不一致，分片写入会错位。
    /// </param>
    public HttpClient CreateClient(bool forDownload = false, TimeSpan? timeout = null)
        => BuildClient(_effective, timeout ?? TimeSpan.FromSeconds(100), forDownload);

    /// <summary>
    /// 禁用自动重定向的客户端。
    ///
    /// <para><b>为什么需要单独一个</b>：蓝奏（LanzouDrive）要把重定向的每一跳都校验
    /// 「还在不在蓝奏域名内」，否则一个恶意分享链接就能把服务端请求引到任意主机（SSRF）。
    /// 这个开关在 handler 上，不能按请求改。</para>
    ///
    /// <para>但它仍然复用共享 handler 的其它配置 —— 尤其是 <c>ConnectCallback</c>
    /// （hosts 覆盖 / DoH 绕过），这正是原来「蓝奏自建 HttpClientHandler」丢掉的东西。</para>
    /// </summary>
    public HttpClient CreateClientNoRedirect(TimeSpan? timeout = null)
        => BuildClient(_effective, timeout ?? TimeSpan.FromSeconds(45), forDownload: false,
            allowRedirect: false);

    /// <summary>
    /// 用共享 handler 造客户端。<see cref="HttpClient.Dispose"/> 不会释放传入的 handler，
    /// 所以调用方可以放心 <c>using</c> —— 连接池留在 handler 上继续复用。
    /// </summary>
    private HttpClient BuildClient(NetworkMode mode, TimeSpan timeout, bool forDownload = false,
        bool allowRedirect = true)
    {
        var handler = GetOrCreateHandler(mode, forDownload, allowRedirect);

        var client = new HttpClient(handler, disposeHandler: false) { Timeout = timeout };

        // ★ HTTP 版本策略必须在这里设置，这是 <see cref="EnableHttp2"/> 真正生效的地方。
        //
        //   早期这个开关只被保存和重置、从未被任何 HttpClient 读取 ——
        //   用户改了设置，抓包却始终是 HTTP/1.1。
        //
        //   DefaultRequestVersion + DefaultVersionPolicy：
        //     · 开 → 请求 2.0，允许降级（RequestVersionOrLower）。镜像/老 CDN 不支持时自动回 1.1。
        //     · 关 → 明确用 1.1，不尝试升级。
        //   注意：版本策略是**客户端级**的，所以 handler 的缓存 key 也要带上这个维度，
        //   否则改了开关却命中旧 handler，行为不会变。
        client.DefaultRequestVersion = EnableHttp2
            ? HttpVersion.Version20
            : HttpVersion.Version11;
        client.DefaultVersionPolicy = EnableHttp2
            ? HttpVersionPolicy.RequestVersionOrLower
            : HttpVersionPolicy.RequestVersionExact;

        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        if (forDownload)
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Encoding", "identity");
        return client;
    }

    /// <summary>取（或首次创建）某个 (模式, 下载标志) 对应的共享 handler。</summary>
    private SocketsHttpHandler GetOrCreateHandler(NetworkMode mode, bool forDownload,
        bool allowRedirect)
    {
        // HTTP/2 是 handler 级开关，必须进 key：否则开了开关却命中旧的 HTTP/1.1 handler，
        // 表现为「设置改了但完全没生效」——这也正是它之前是死设置的原因之一。
        var key = (mode, forDownload, allowRedirect, EnableHttp2);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_handlers.TryGetValue(key, out var existing)) return existing;

            var created = new SocketsHttpHandler
            {
                UseProxy = mode == NetworkMode.Proxy,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 64,
                // 关键：不解压。Range 是按**压缩前**的字节算偏移的，
                // 一旦让 HttpClient 自动解压，写入位置就全错了。
                AutomaticDecompression = forDownload
                    ? DecompressionMethods.None
                    : DecompressionMethods.All,
                // 关键：三层重定向都跟随（镜像常 302 到 CDN）。
                // 蓝奏那一路要逐跳校验域名，因此单独用 allowRedirect=false 的 handler。
                AllowAutoRedirect = allowRedirect,
                MaxAutomaticRedirections = 10,
                // 每次建连都重新查 Overrides，因此 hosts 覆盖在运行中更新也能生效
                ConnectCallback = BuildConnectCallback(),
            };

            // HTTP 版本协商由 HttpClient 的 DefaultVersionPolicy 决定，而 handler 这边
            // 能控制的是"是否允许 H2 的明文升级/多路复用"。这里通过 ConnectCallback
            // 之外的方式不行，所以把开关记在 handler 的关联表里，由 BuildClient 设置
            // 请求版本（见 BuildClient）—— handler 本身保持默认。
            _handlers[key] = created;
            return created;
        }
    }

    /// <summary>
    /// 一次性 handler（仅用于启动探测）。
    ///
    /// <para>探测要试「直连」和「代理」两条路，其中必有一条是错的、且可能卡到超时。
    /// 把它塞进共享缓存会让一条注定失败的 handler 永久占位，
    /// 所以探测走独立 handler 并显式释放。</para>
    /// </summary>
    private static HttpClient BuildEphemeralClient(NetworkMode mode, TimeSpan timeout)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = mode == NetworkMode.Proxy,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            ConnectCallback = BuildConnectCallback(),
        };

        var client = new HttpClient(handler) { Timeout = timeout };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
        return client;
    }

    /// <summary>
    /// 构造 ConnectCallback：命中 hosts 覆盖表时直接连真实 IP，否则走系统 DNS。
    /// 这是绕过 hosts 劫持的落点，且**只影响本客户端**，无进程级全局副作用。
    /// </summary>
    private static Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>
        BuildConnectCallback()
    {
        return async (ctx, ct) =>
        {
            string host = ctx.DnsEndPoint.Host;
            int port = ctx.DnsEndPoint.Port;

            if (HostsBypass.Overrides.TryGetValue(host, out var ip))
            {
                var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                {
                    NoDelay = true,
                };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(ip, port), ct).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch
                {
                    socket.Dispose();
                    throw;
                }
            }

            // 常规路径：交给系统解析
            var plain = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await plain.ConnectAsync(ctx.DnsEndPoint, ct).ConfigureAwait(false);
                return new NetworkStream(plain, ownsSocket: true);
            }
            catch
            {
                plain.Dispose();
                throw;
            }
        };
    }

    public void Dispose()
    {
        List<SocketsHttpHandler> toDispose;

        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            toDispose = _handlers.Values.ToList();
            _handlers.Clear();
        }

        // 释放共享 handler 才会真正关闭底层连接池与 socket。
        // 放在锁外：Dispose 可能等待连接完成，不该阻塞其他线程取客户端。
        foreach (var h in toDispose)
        {
            try { h.Dispose(); } catch { /* 退出路径，忽略 */ }
        }
    }
}
