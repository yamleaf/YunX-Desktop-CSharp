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

    /// <summary>
    /// 低于这个速度视为"能连但基本不可用"（字节/秒）。
    ///
    /// <para><b>为什么需要这条线</b>：探测只发 <c>Range: bytes=0-0</c>（1 个字节），
    /// 拿到 206 就判"可用"。但实测存在这种线路：1 字节能秒回，
    /// 真正下载 512KB 要 32 秒（16 KB/s）—— 它"可用"却会把整个任务拖死。
    /// 8 线程全挤在它上面，用户看到的就是"几百 K 在爬"。</para>
    /// </summary>
    public const double UnusableBps = 48 * 1024;

    /// <summary>
    /// 排序分数（越大越好）。失败线路得 -1。
    ///
    /// <para><b>不含"太慢"惩罚</b>：要不要压低一条能连但极慢的线路，
    /// 必须结合**全局**判断 —— 如果所有线路都这么慢，压低它们毫无意义
    /// （排序还是原样），反倒会让分数变得不可读。所以那一步放在
    /// <c>MirrorPool.RankAsync</c> 里做（见 <see cref="UnusableBps"/>）。</para>
    /// </summary>
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

    /// <summary>
    /// 「够用就算通」的阈值。
    ///
    /// <para><b>为什么需要提前结束</b>：原来一定要收满 768KB 才算测完。对 GitHub 的
    /// Release 链接这类小响应（几十 KB 的 API JSON / HTML 页面），响应体读完后循环
    /// 仍要等下一次 <c>ReadAsync</c> 返回 0 —— 镜像若不主动关连接，这一等就顶到
    /// 8 秒超时，而测出来的还是「超时 = 不通」，白白把可用线路判死。
    /// 现在收到 256KB 且已有稳态样本就直接结束，结果与收满一致。</para>
    /// </summary>
    private const int EnoughBytes = 256 * 1024;

    private const int FirstChunk = 32 * 1024;

    private readonly HttpClient _client;
    private readonly TimeSpan _timeout;
    private readonly int _budget;

    /// <param name="timeout">
    /// 单条线路的探测预算，默认 8 秒。**HTML / API 这类小响应应传更小的值**
    /// （见 <c>MirrorPool.RankAsync</c>）：这类请求要至少 4 条线路并行探测，
    /// 每条都等 8 秒的话，最坏 8 秒（并行）还只是第一步，用户感知就是「卡住」。
    /// </param>
    /// <param name="budgetBytes">
    /// 采样字节数，默认 768KB。
    /// <b>解析阶段（只想判断通不通 + 比延迟）应传很小的值</b>：解析是为了给用户
    /// 一个「将走哪条线路」的提示，不需要精确吞吐。实测对 745MB 的资产收满
    /// 768KB 要 8~13 秒，把解析拖成了「卡死」。
    /// </param>
    public MirrorProbe(HttpClient client, TimeSpan? timeout = null, int? budgetBytes = null)
    {
        _client = client;
        _timeout = timeout ?? TimeSpan.FromSeconds(8);
        _budget = Math48.Clamp(budgetBytes ?? BudgetBytes, 16 * 1024, BudgetBytes);
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
            req.Headers.TryAddWithoutValidation("Range", $"bytes=0-{_budget - 1}");

            using var resp = await _client.SendAsync(req,
                HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
            status = (int)resp.StatusCode;

            // ★ 延迟 = 首字节时间（TTFB），在这里就取，而不是等首块数据到齐。
            //
            //   早期在「读到第一块数据」时记 latency，快测模式下（只要 64KB）
            //   镜像往往一次就把 64KB 全推完，量到的其实是整次传输耗时；
            //   而慢线路可能只先给几 KB —— 数值完全不可比，还出现过 0 ms
            //   这种明显失真的结果（命中了进程内的连接复用）。
            //   改成以响应头到达为准，只反映「这条线路多久开始给我数据」。
            latency = sw.Elapsed.TotalSeconds;

            if (status >= 400)
            {
                return new ProbeResult
                {
                    MirrorId = mirror.Id, MirrorName = mirror.Name, Url = built,
                    StatusCode = status, Error = $"HTTP {status}",
                };
            }

#if NET48
            using var stream = await resp.Content.ReadAsStreamAsync(linked.Token)
#else
            await using var stream = await resp.Content.ReadAsStreamAsync(linked.Token)
#endif
                .ConfigureAwait(false);

            var buffer = new byte[FirstChunk];
            while (got < _budget)
            {
                int read = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                if (read <= 0) break;

                if (!sawFirst)
                {
                    sawFirst = true;
                    firstChunkLen = read;
                }
                got += read;

                // 够用就停：避免在小响应上死等对端关连接（详见 EnoughBytes 的说明）
                if (got >= EnoughBytes && got - firstChunkLen >= 16 * 1024) break;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // ★ 用户主动取消（暂停/删除）不能算成"线路坏了"。
            //
            //   早期这个过滤器写的是 `when (!ct.IsCancellationRequested)` —— 只管超时。
            //   于是主动取消不匹配它、落到下面的兜底 catch，被记成
            //   `Ok = false`，导致：
            //     · 每条线路都记一次失败（连续 2 次就进 5 分钟冷却）
            //     · 用户点一下暂停，全部线路瞬间被拉黑，日志刷「5 条线路不可用」
            //     · 再点继续时无线路可用 —— 这正是「继续后过一会就自动结束」的来源
            //   必须原样上抛，让上层按「取消」处理。
            throw;
        }
        catch (OperationCanceledException)
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
        if (got < _budget * 0.9) speed *= 0.85;

        return new ProbeResult
        {
            MirrorId = mirror.Id, MirrorName = mirror.Name, Url = built,
            Ok = true, Speed = speed, Latency = latency, StatusCode = status, Tiny = tiny,
        };
    }

    public string DescribeSpeed(double bytesPerSec) => Humanize.Speed(bytesPerSec);
}
