using System.Net;
using YunX.Core.Mirrors;
using YunX.Core.Net;
using YunX.Core.Util;

namespace YunX.Core.Download;

/// <summary>远端资源信息（探测结果）。</summary>
public sealed class RemoteInfo
{
    /// <summary>探测/下载实际使用的地址（可能已带镜像前缀）。</summary>
    public string Url { get; init; } = "";

    /// <summary>
    /// **原始**资源地址（未加镜像前缀）。
    /// 线路展开、断点元数据的资源身份都必须基于它：拿 <see cref="Url"/> 再展开
    /// 会得到 "镜像/镜像/原URL" 的双重前缀，必然失败。
    /// </summary>
    public string ResourceUrl { get; init; } = "";

    public string MirrorName { get; init; } = "";
    public long Total { get; init; }
    public bool RangeSupported { get; init; }
    public string FileName { get; init; } = "";
    public string? ETag { get; init; }
    public string? LastModified { get; init; }
    public string ContentType { get; init; } = "";

    public string SizeText => Total > 0 ? Humanize.Size(Total) : "未知";
}

/// <summary>
/// 远端探测：确定线路、大小、是否支持 Range，并采集 ETag。
///
/// <para><b>策略：优先选"支持 Range + 已知大小"的线路</b>，因为只有这样才能多线程分块。
/// 原 Python 版取"第一个可用"线路，常常拿到一个只能单流的镜像，多线程能力白费。</para>
/// </summary>
public sealed class RemoteProbe
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(40);

    /// <summary>最多探测几条线路，避免在慢线路上耗太久。</summary>
    private const int MaxProbe = 4;

    private readonly HttpClient _client;

    public RemoteProbe(HttpClient client) => _client = client;

    public sealed class ProbeFailedException : Exception
    {
        public ProbeFailedException(string message) : base(message) { }
    }

    public async Task<RemoteInfo> ProbeAsync(MirrorPool pool, string url,
        string fallbackName = "download.bin", Action<string>? log = null,
        CancellationToken ct = default,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        var candidates = await pool.OrderedUrlsAsync(url, force: false, log, ct)
            .ConfigureAwait(false);

        string lastError = "无可用线路";
        int saw404 = 0, tried = 0;
        RemoteInfo? fallback = null;

        // 429/503 是限流而非资源不可用：整轮探测退避后重来，而不是直接判死。
        // 早期实现遇到非 200/206/404/403 一律 continue，于是一个刚被限流的线路
        // 会让整个探测报「所有线路均不可用（HTTP 429）」—— 用户看到的是资源不存在，
        // 实际上等一秒重试就好了。这里最多重来 ProbeRateLimitRounds 轮。
        for (int round = 0; round <= ProbeRateLimitRounds; round++)
        {
            bool rateLimited = false;
            TimeSpan wait = TimeSpan.Zero;

            foreach (var (name, target) in candidates.Take(MaxProbe))
            {
                if (round > 0) tried++;
                else tried = Math.Max(tried, 1);

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, target);
                    req.Headers.TryAddWithoutValidation("Range", "bytes=0-0");
                    // ★ 探测必须带上和下载相同的鉴权头。
                    //   百度/迅雷/夸克等直链对 Referer / Cookie 是硬校验：缺了就 403。
                    //   早期只在分片阶段带 headers，于是「需要鉴权的直链」在探测阶段就
                    //   全部被判死，报「所有线路均不可用」，而实际上分片请求本可以成功。
                    ApplyHeaders(req, headers);

                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    cts.CancelAfter(ConnectTimeout + ReadTimeout);
                    using var resp = await _client.SendAsync(req,
                        HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);

                    long total = 0;
                    bool rangeOk = false;

                    if (resp.StatusCode == HttpStatusCode.PartialContent)
                    {
                        // Content-Range: bytes 0-0/12345
                        var cr = resp.Content.Headers.ContentRange;
                        if (cr?.Length is long len && len > 0)
                        {
                            total = len;
                            rangeOk = true;
                        }
                    }
                    else if (resp.StatusCode == HttpStatusCode.OK)
                    {
                        total = resp.Content.Headers.ContentLength ?? 0;
                        rangeOk = false;
                    }
                    else if (resp.StatusCode is HttpStatusCode.TooManyRequests
                             or HttpStatusCode.ServiceUnavailable)
                    {
                        // 记下最长的 Retry-After，本轮结束后统一睡
                        rateLimited = true;
                        lastError = $"HTTP {(int)resp.StatusCode}（限流）";
                        var d = RetryDelay(resp, round);
                        if (d > wait) wait = d;
                        log?.Invoke($"线路 [{name}] 被限流（HTTP {(int)resp.StatusCode}）");
                        continue;
                    }
                    else if (resp.StatusCode is HttpStatusCode.NotFound
                             or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    {
                        lastError = $"HTTP {(int)resp.StatusCode}";
                        if (resp.StatusCode == HttpStatusCode.NotFound) saw404++;
                        continue;
                    }
                    else
                    {
                        lastError = $"HTTP {(int)resp.StatusCode}";
                        continue;
                    }

                    var info = new RemoteInfo
                    {
                        Url = target,
                        ResourceUrl = url,
                        MirrorName = name,
                        Total = total,
                        RangeSupported = rangeOk,
                        FileName = GuessFileName(resp, target, fallbackName),
                        ETag = resp.Headers.ETag?.Tag?.Trim('"'),
                        LastModified = resp.Content.Headers.LastModified?.ToString("R"),
                        ContentType = resp.Content.Headers.ContentType?.MediaType ?? "",
                    };

                    log?.Invoke($"线路 [{name}] 可用 · 大小 {info.SizeText} · " +
                            (rangeOk ? "支持断点续传" : "单流模式"));

                    // 支持 Range 的直接返回；不支持的先记下，继续找更好的
                    if (rangeOk) return info;
                    fallback ??= info;
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    lastError = "连接超时";
                }
                catch (HttpRequestException e)
                {
                    lastError = e.InnerException is System.Security.Authentication.AuthenticationException
                        ? "TLS 握手失败" : "连接失败";
                }
                catch (Exception e)
                {
                    lastError = e.GetType().Name;
                }
            }

            // 本轮全部被限流：退避后整体重试；没有任何线路报限流就结束
            if (!rateLimited) break;
            if (round == ProbeRateLimitRounds)
            {
                log?.Invoke($"探测：连续 {round + 1} 轮被限流，放弃");
                break;
            }

            if (wait <= TimeSpan.Zero) wait = TimeSpan.FromMilliseconds(500 * (round + 1));
            if (wait > MaxRetryWait) wait = MaxRetryWait;
            log?.Invoke($"探测：所有线路都在限流，{wait.TotalMilliseconds:0}ms 后重试" +
                        $"（{round + 1}/{ProbeRateLimitRounds}）");
            await Task.Delay(wait, ct).ConfigureAwait(false);
            // 重试前清掉上一轮「只看到 404」的统计，避免误判成资源不存在
            saw404 = 0;
            tried = 0;
        }

        if (fallback != null)
        {
            log?.Invoke("没有线路支持 Range，改用单流下载（无法多线程加速）");
            return fallback;
        }

        if (saw404 > 0 && saw404 >= Math.Max(1, tried / 2))
        {
            throw new ProbeFailedException(
                "资源不存在（404）：请检查仓库名 / 分支 / 文件名。" +
                "私有仓库需在设置里填 Token，且不能走镜像（镜像不转发鉴权头）。");
        }

        throw new ProbeFailedException($"所有线路均不可用（{lastError}）");
    }

    /// <summary>探测阶段允许的限流重试轮数（首轮之外再重试几次）。</summary>
    private const int ProbeRateLimitRounds = 3;

    /// <summary>单次退避上限，避免启动阶段被一条慢线路拖住。</summary>
    private static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(5);

    /// <summary>解析 <c>Retry-After</c>（秒数或 HTTP 日期），否则按轮次指数退避。</summary>
    private static TimeSpan RetryDelay(HttpResponseMessage resp, int round)
    {
        var ra = resp.Headers.RetryAfter;
        if (ra?.Delta is TimeSpan d && d > TimeSpan.Zero) return d;
        if (ra?.Date is DateTimeOffset at)
        {
            var w = at - DateTimeOffset.UtcNow;
            if (w > TimeSpan.Zero) return w;
        }
        return TimeSpan.FromMilliseconds(400 * Math.Pow(2, Math.Min(round, 4)));
    }

    /// <summary>
    /// 附加自定义请求头（与 <see cref="DownloadEngine"/> 的分片请求保持同一套语义）：
    /// Range / If-Range 由引擎自己控制，不允许被用户头覆盖，否则区间会错乱。
    /// </summary>
    private static void ApplyHeaders(HttpRequestMessage req,
        IReadOnlyDictionary<string, string>? headers)
    {
        if (headers == null) return;
        foreach (var (k, v) in headers)
        {
            if (string.IsNullOrEmpty(v)) continue;
            if (k.Equals("Range", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("If-Range", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("Host", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                continue;
            req.Headers.TryAddWithoutValidation(k, v);
        }
    }

    private static string GuessFileName(HttpResponseMessage resp, string url, string fallback)
    {
        var cd = resp.Content.Headers.ContentDisposition;
        if (cd != null)
        {
            var name = cd.FileNameStar ?? cd.FileName;
            if (!string.IsNullOrWhiteSpace(name))
                return FileNamePolicy.SanitizeFileName(name.Trim('"'), fallback);
        }

        try
        {
            var u = new Uri(url);
            var last = u.AbsolutePath.TrimEnd('/').Split('/').LastOrDefault();
            if (!string.IsNullOrEmpty(last) && last.Contains('.') && last.Length < 200)
                return FileNamePolicy.SanitizeFileName(Uri.UnescapeDataString(last), fallback);
        }
        catch
        {
            // 用 fallback
        }
        return FileNamePolicy.SanitizeFileName(fallback, "download.bin");
    }
}
