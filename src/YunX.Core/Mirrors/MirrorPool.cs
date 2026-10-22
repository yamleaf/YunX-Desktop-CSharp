using YunX.Core.Net;
using YunX.Core.Util;

namespace YunX.Core.Mirrors;

/// <summary>
/// 线路池：把一条原始 URL 展开成多条候选线路，按实测速度排序，并维护线路健康度。
///
/// <para><b>这是本项目相对原项目（YunX-Desktop）最核心的增强之一。</b>
/// 原项目只有一个硬编码镜像前缀 + 失败回退直连，没有测速、没有多线路，
/// 一旦该镜像变慢或失效，下载速度直接受制于它。本类改为：</para>
/// <list type="number">
///   <item>并发探测所有线路，按**稳态吞吐**排序择优；</item>
///   <item>失败线路进入 5 分钟冷却，避免反复在必死线路上浪费时间；</item>
///   <item>排序结果按 URL 类别缓存 3 分钟，同一批次任务不重复测速。</item>
/// </list>
/// </summary>
public sealed class MirrorPool
{
    private const int CooldownSeconds = 300;
    private const int MaxFailBeforeCooldown = 2;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(3);

    private readonly List<Mirror> _mirrors;
    private readonly HttpClient _client;
    private readonly HttpClientProvider? _provider;
    private readonly object _gate = new();

    // 缓存里额外记下每条线路的延迟：命中缓存时若不回填，调用方拿到的延迟是 0，
    // 界面上就会出现「延迟 0 ms」这种明显失真的结果。
    private readonly Dictionary<string, (DateTimeOffset At, List<string> Order,
        HashSet<string> OkIds, Dictionary<string, double> Latency)> _cache
        = new(StringComparer.Ordinal);

    /// <summary>用户指定的单条线路 Id；null = 自动择优。</summary>
    public string? OnlyMirrorId { get; set; }

    public MirrorPool(IEnumerable<Mirror> mirrors, HttpClient client,
        HttpClientProvider? provider = null)
    {
        _mirrors = mirrors.ToList();
        _client = client;
        _provider = provider;
    }

    public IReadOnlyList<Mirror> AllMirrors => _mirrors;

    public MirrorPool Clone()
    {
        var list = _mirrors.Select(m => new Mirror
        {
            Id = m.Id, Name = m.Name, Kind = m.Kind, Arg = m.Arg,
            Scopes = m.Scopes, Enabled = m.Enabled,
            FailCount = m.FailCount, OfflineUntil = m.OfflineUntil,
        }).ToList();
        return new MirrorPool(list, _client, _provider) { OnlyMirrorId = OnlyMirrorId };
    }

    /// <summary>当前可参与择优的线路（跳过禁用与冷却中的）。</summary>
    private IEnumerable<Mirror> Candidates(string url)
    {
        foreach (var m in _mirrors)
        {
            if (OnlyMirrorId != null && m.Id != OnlyMirrorId) continue;
            if (m.IsCoolingDown) continue;
            if (!m.Supports(url)) continue;
            if (m.Build(url) == null) continue;
            yield return m;
        }
    }

    /// <summary>
    /// 对 URL 测速并返回排序结果（最快在前）。
    /// </summary>
    public async Task<IReadOnlyList<ProbeResult>> RankAsync(string url, bool force = false,
        Action<string>? log = null, CancellationToken ct = default,
        bool quick = false)
    {
        string key = $"{UrlClassifier.Classify(url)}|{OnlyMirrorId}|{(quick ? "q" : "f")}";

        if (!force)
        {
            lock (_gate)
            {
                if (_cache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.At < CacheTtl)
                {
                    var cands = Candidates(url).ToList();
                    var cached = cands
                        .Select(m => new ProbeResult
                        {
                            MirrorId = m.Id, MirrorName = m.Name, Url = m.Build(url) ?? "",
                            Ok = hit.OkIds.Contains(m.Id),
                            StatusCode = hit.OkIds.Contains(m.Id) ? 200 : 0,
                            Latency = hit.Latency.TryGetValue(m.Id, out double lt) ? lt : 0,
                        })
                        .OrderBy(r => hit.Order.IndexOf(r.MirrorId) is var i && i >= 0 ? i : 999)
                        .ToList();
                    return cached;
                }
            }
        }

        var list = Candidates(url).ToList();
        if (list.Count <= 1)
        {
            var only = list.Select(m => new ProbeResult
            {
                MirrorId = m.Id, MirrorName = m.Name, Url = m.Build(url) ?? "", Ok = true,
            }).ToList();
            Cache(key, list.Select(m => m.Id).ToList(),
                list.Select(m => m.Id).ToHashSet(), new Dictionary<string, double>());
            return only;
        }

        var probe = quick
            ? new MirrorProbe(_client, QuickProbeTimeout, QuickProbeBytes)
            : new MirrorProbe(_client, ProbeTimeoutFor(url));
        var tasks = list.Select(m => probe.ProbeAsync(m, url, ct)).ToList();
        var results = (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();

        // ★ 取消要在这里就拦住，绝不能继续往下走到「更新健康度」。
        //
        //   MirrorProbe 现在会把主动取消原样上抛，Task.WhenAll 会把异常抛到这里 ——
        //   但万一将来某个 Probe 实现又把它吞成 Ok=false，这里的检查是最后一道闸：
        //   一旦把取消算成失败，全线路都会进 5 分钟冷却，
        //   用户「暂停再继续」就会因为无线路可用而立刻失败。
        ct.ThrowIfCancellationRequested();

        // ★ 把"能连但极慢"的线路压到正常线路之后。
        //
        //   探测只发 Range: bytes=0-0（1 字节），拿到 206 就判可用 ——
        //   但实测存在 1 字节能秒回、下 512KB 要 32 秒的线路（16 KB/s）。
        //   它比"失败"更糟：失败会立刻换线，慢线路却会一直占着 worker 不放，
        //   8 线程全挤上去，用户看到的就是"几百 K 在爬"。
        //
        //   ★ 只在**存在正常线路**时才压低。全都慢的话压了也没用，
        //     反而让分数失去参考意义。
        bool hasHealthy = results.Any(r => r.Ok && !r.Tiny && r.Speed >= ProbeResult.UnusableBps);
        if (hasHealthy)
        {
            foreach (var r in results.Where(x => x.Ok && !x.Tiny &&
                                                 x.Speed < ProbeResult.UnusableBps))
                log?.Invoke($"线路 {r.MirrorName} 太慢（{Humanize.Speed(r.Speed)}），不参与择优");
        }

        results.Sort((a, b) =>
        {
            if (hasHealthy)
            {
                bool au = a.Ok && !a.Tiny && a.Speed < ProbeResult.UnusableBps;
                bool bu = b.Ok && !b.Tiny && b.Speed < ProbeResult.UnusableBps;
                if (au != bu) return au ? 1 : -1;   // 太慢的排后面
            }
            return b.Score.CompareTo(a.Score);
        });

        // 更新线路健康度
        var byId = _mirrors.ToDictionary(m => m.Id, m => m);
        foreach (var r in results)
        {
            if (!byId.TryGetValue(r.MirrorId, out var m)) continue;
            if (r.Ok)
            {
                m.FailCount = 0;
                m.OfflineUntil = DateTimeOffset.MinValue;
            }
            else
            {
                m.FailCount++;
                // 直连不冷却：它常常是被 hosts 污染，值得每次都试
                if (m.FailCount >= MaxFailBeforeCooldown && m.Kind != MirrorKind.Direct)
                    m.OfflineUntil = DateTimeOffset.UtcNow.AddSeconds(CooldownSeconds);
            }
        }

        if (log != null)
        {
            // 快测模式拿到的字节太少，算出来的「速度」没有参考价值，只报延迟
            foreach (var r in results.Where(x => x.Ok).Take(3))
                log(quick
                    ? $"线路 {r.MirrorName}：延迟 {r.Latency * 1000:0} ms"
                    : $"线路 {r.MirrorName}：{Humanize.Speed(r.Speed)}");
            int bad = results.Count(x => !x.Ok);
            if (bad > 0) log($"{bad} 条线路不可用（已冷却 5 分钟）");
        }

        Cache(key, results.Select(r => r.MirrorId).ToList(),
            results.Where(r => r.Ok).Select(r => r.MirrorId).ToHashSet(),
            results.Where(r => r.Ok).ToDictionary(r => r.MirrorId, r => r.Latency));

        return results;
    }

    /// <summary>
    /// 按 URL 类别给探测预算。
    ///
    /// <para><b>为什么 API / 网页要更短</b>：测速是为了给**下载**选线路，样本越大越准。
    /// 但解析阶段探测的是几十 KB 的 JSON / HTML —— 目标本来就不是测吞吐，只是想知道
    /// 「这条线路通不通」。这时还按 8 秒预算等，一旦某条线路半死（能连上、不回数据），
    /// 整个解析就卡在那儿。实测 enso-org/enso 的 Release 页面在 ghproxy.net 上正是这种表现。</para>
    /// </summary>
    private static TimeSpan ProbeTimeoutFor(string url) => UrlClassifier.Classify(url) switch
    {
        UrlKind.Api => TimeSpan.FromSeconds(4),
        UrlKind.Page => TimeSpan.FromSeconds(4),
        _ => TimeSpan.FromSeconds(8),
    };

    /// <summary>
    /// 解析阶段「快测」用的参数。
    ///
    /// <para><b>为什么解析时不该收满 768KB</b>：实测对一个 745MB 的资产，
    /// 三条线路并行收满样本要 8 秒以上，最慢那条还要等满超时 —— 整次解析被拖到
    /// 13.6 秒，用户看到的就是「点了没反应」。而解析只需要知道
    /// <b>哪条线路通、谁先响应</b>，精确吞吐交给下载阶段的完整测速。</para>
    ///
    /// <para>64KB 样本 + 2 秒超时：足够让正常线路全部给出结论。
    /// 实测慢线（如 ghproxy.net）往往要拖到超时才判死，所以超时值直接决定
    /// 用户感知的等待 —— 2 秒是「足够区分好坏」与「不让人觉得卡」的折中。</para>
    /// </summary>
    private static readonly TimeSpan QuickProbeTimeout = TimeSpan.FromSeconds(2);
    private const int QuickProbeBytes = 64 * 1024;

    /// <summary>
    /// 解析阶段定线路：只判断通不通 + 比延迟，几秒内出结果。
    ///
    /// <para>用 <c>quick: true</c> 走独立缓存键，不会污染下载阶段的完整测速结果。</para>
    /// </summary>
    public async Task<ProbeResult?> PickAsync(string url, Action<string>? log = null,
        CancellationToken ct = default)
    {
        var ranked = await RankAsync(url, force: false, log, ct, quick: true).ConfigureAwait(false);
        return ranked.FirstOrDefault(r => r.Ok);
    }

    private void Cache(string key, List<string> order, HashSet<string> okIds,
        Dictionary<string, double> latency)
    {
        lock (_gate)
        {
            _cache[key] = (DateTimeOffset.UtcNow, order, okIds, latency);
            if (_cache.Count > 64)
            {
                var oldest = _cache.OrderBy(kv => kv.Value.At).First().Key;
                _cache.Remove(oldest);
            }
        }
    }

    /// <summary>
    /// 按速度排序返回可用线路 URL（最快的在前）。
    ///
    /// <para><b>重要：只返回测速成功的线路。</b>早期实现把失败线路也一并返回，导致下载线程
    /// 每领一个分片都要在必死线路上空等一轮 —— 实测让"续传"比"首次下载"还慢 3 倍。
    /// </para>
    /// </summary>
    public async Task<List<(string Name, string Url)>> OrderedUrlsAsync(string url,
        bool force = false, Action<string>? log = null, CancellationToken ct = default)
    {
        var ranked = await RankAsync(url, force, log, ct).ConfigureAwait(false);

        var good = new List<(string, string)>();
        var bad = new List<(string, string)>();
        var byId = _mirrors.ToDictionary(m => m.Id, m => m);

        foreach (var r in ranked)
        {
            if (!byId.TryGetValue(r.MirrorId, out var m)) continue;
            var built = m.Build(url);
            if (built == null) continue;
            (r.Ok ? good : bad).Add((m.Name, built));
        }

        if (good.Count > 0) return good;

        // 一条都没测通：退回「直连 + 其余候选」，让下载阶段自己再试一次
        var fallback = new List<(string, string)> { ("直连", url) };
        fallback.AddRange(bad.Where(x => x.Item1 != "直连 GitHub"));
        return fallback;
    }

    /// <summary>标记某条线路下载失败（累计 2 次进入冷却）。</summary>
    public void MarkBad(string mirrorName, bool hard = false)
    {
        var m = _mirrors.FirstOrDefault(x => x.Name == mirrorName);
        if (m == null) return;
        m.FailCount++;
        if (hard || m.FailCount >= MaxFailBeforeCooldown)
            m.OfflineUntil = DateTimeOffset.UtcNow.AddSeconds(CooldownSeconds);
    }

    /// <summary>清除所有冷却与失败计数（设置页「重置线路」）。</summary>
    public void ResetHealth()
    {
        foreach (var m in _mirrors)
        {
            m.FailCount = 0;
            m.OfflineUntil = DateTimeOffset.MinValue;
        }
        lock (_gate) _cache.Clear();
    }
}
