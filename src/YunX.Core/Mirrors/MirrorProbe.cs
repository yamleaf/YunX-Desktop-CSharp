using System.Diagnostics;
using YunX.Core.Net;
using YunX.Core.Util;

namespace YunX.Core.Mirrors;

/// <summary>单条线路的测速结果。</summary>
public sealed class ProbeResult
{
    public required string MirrorId { get; init; }
    public required string MirrorName { get; init; }
    public string Url { get; init; } = "";
    public bool Ok { get; init; }
    /// <summary>稳态吞吐（字节/秒）。已剔除建连与首块开销。</summary>
    public double Speed { get; init; }
    /// <summary>首字节延迟（秒）。</summary>
    public double Latency { get; init; }
    public string Error { get; init; } = "";
    public int StatusCode { get; init; }
    /// <summary>样本本身太小（&lt;64KB），速度无意义，只能按延迟排序。</summary>
    public bool Tiny { get; init; }

    /// <summary>排序分数（越大越好）。失败线路得 -1。</summary>
    public double Score => !Ok ? -1
        : Tiny ? 1_000_000 - Latency * 100_000
        : Speed - Latency * 2_000;
}

/// <summary>
/// 线路测速器。
///
/// <para><b>为什么不能用小样本测速</b>：本项目曾用 64KB 样本测速，结果排序与真实吞吐
/// 完全对不上 —— 小样本测到的其实是**握手延迟**而非带宽，导致最快的线路排到垫底。
/// 改为约 768KB 样本、并且**从首块之后开始计时**（剔除 TCP/TLS 握手），排序才与
/// 4MB 实下吞吐一致。</para>
/// </summary>
public sealed class MirrorProbe
{
    /// <summary>测速样本大小：约 768KB，兼顾准确度与耗时。</summary>
    private const int BudgetBytes = 768 * 1024;

    /// <summary>低于此大小视为"文件本身太小"，改按延迟排序。</summary>
    private const int TinyThreshold = 64 * 1024;

    private const int FirstChunk = 32 * 1024;

    private readonly HttpClient _client;
    private readonly TimeSpan _timeout;

    public MirrorProbe(HttpClient client, TimeSpan? timeout = null)
    {
        _client = client;
        _timeout = timeout ?? TimeSpan.FromSeconds(8);
    }

    public async Task<ProbeResult> ProbeAsync(Mirror mirror, string url,
        CancellationToken ct = default)
    {
        var built = mirror.Build(url);
        if (built == null)
        {
            return new ProbeResult
            {
                MirrorId = mirror.Id, MirrorName = mirror.Name, Error = "不适用",
            };
        }

        var sw = Stopwatch.StartNew();
        long got = 0;
        int firstChunkLen = 0;
        double latency = 0;
        bool sawFirst = false;
        int status = 0;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(_timeout);

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, built);
            req.Headers.TryAddWithoutValidation("Range", $"bytes=0-{BudgetBytes - 1}");

            using var resp = await _client.SendAsync(req,
                HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            status = (int)resp.StatusCode;

            if (status >= 400)
            {
                return new ProbeResult
                {
                    MirrorId = mirror.Id, MirrorName = mirror.Name, Url = built,
                    StatusCode = status, Error = $"HTTP {status}",
                };
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(linked.Token)
                .ConfigureAwait(false);

            var buffer = new byte[FirstChunk];
            while (got < BudgetBytes)
            {
                int read = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                if (read <= 0) break;

                if (!sawFirst)
                {
                    sawFirst = true;
                    latency = sw.Elapsed.TotalSeconds;
                    firstChunkLen = read;
                }
                got += read;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new ProbeResult
            {
                MirrorId = mirror.Id, MirrorName = mirror.Name, Url = built,
                StatusCode = status, Error = "超时",
            };
        }
        catch (HttpRequestException e)
        {
            return new ProbeResult
            {
                MirrorId = mirror.Id, MirrorName = mirror.Name, Url = built,
                StatusCode = status,
                Error = e.InnerException is System.Security.Authentication.AuthenticationException
                    ? "TLS 失败" : "连接失败",
            };
        }
        catch (Exception e)
        {
            return new ProbeResult
            {
                MirrorId = mirror.Id, MirrorName = mirror.Name, Url = built,
                StatusCode = status, Error = e.GetType().Name,
            };
        }

        sw.Stop();
        if (got <= 0 || !sawFirst)
        {
            return new ProbeResult
            {
                MirrorId = mirror.Id, MirrorName = mirror.Name, Url = built,
                StatusCode = status, Error = "无数据",
            };
        }

        double totalSpan = Math.Max(sw.Elapsed.TotalSeconds, 0.005);
        long steadyBytes = got - firstChunkLen;
        double steadySpan = Math.Max(sw.Elapsed.TotalSeconds - latency, 0.005);

        bool tiny = got < TinyThreshold;
        double speed;

        if (tiny)
        {
            speed = got / totalSpan;
        }
        else if (steadyBytes >= 16 * 1024 && steadySpan > 0.02)
        {
            // 稳态吞吐：只算首块之后的字节与时间
            speed = steadyBytes / steadySpan;
        }
        else
        {
            speed = got / totalSpan;
        }

        speed = Math.Min(speed, 300.0 * 1024 * 1024);

        // 没测满样本 = 大概率被限速或链路差，按已测得速度打折，避免它靠偶然高值排前面
        if (got < BudgetBytes * 0.9) speed *= 0.85;

        return new ProbeResult
        {
            MirrorId = mirror.Id, MirrorName = mirror.Name, Url = built,
            Ok = true, Speed = speed, Latency = latency, StatusCode = status, Tiny = tiny,
        };
    }

    public string DescribeSpeed(double bytesPerSec) => Humanize.Speed(bytesPerSec);
}
