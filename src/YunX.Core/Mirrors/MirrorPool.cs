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

    private readonly Dictionary<string, (DateTimeOffset At, List<string> Order,
        HashSet<string> OkIds)> _cache = new(StringComparer.Ordinal);

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
        Action<string>? log = null, CancellationToken ct = default)
    {
        string key = $"{UrlClassifier.Classify(url)}|{OnlyMirrorId}";

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
                list.Select(m => m.Id).ToHashSet());
            return only;
        }

        var probe = new MirrorProbe(_client);
        var tasks = list.Select(m => probe.ProbeAsync(m, url, ct)).ToList();
        var results = (await Task.WhenAll(tasks).ConfigureAwait(false)).ToList();

        results.Sort((a, b) => b.Score.CompareTo(a.Score));

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
            foreach (var r in results.Where(x => x.Ok).Take(3))
                log($"线路 {r.MirrorName}：{Humanize.Speed(r.Speed)}");
            int bad = results.Count(x => !x.Ok);
            if (bad > 0) log($"{bad} 条线路不可用（已冷却 5 分钟）");
        }

        Cache(key, results.Select(r => r.MirrorId).ToList(),
            results.Where(r => r.Ok).Select(r => r.MirrorId).ToHashSet());

        return results;
    }

    private void Cache(string key, List<string> order, HashSet<string> okIds)
    {
        lock (_gate)
        {
            _cache[key] = (DateTimeOffset.UtcNow, order, okIds);
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
