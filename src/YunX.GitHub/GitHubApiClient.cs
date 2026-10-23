// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Linq;
// Copyright (C) 2026 Lyaco
//
// YunX-Desktop-CSharp —— 基于 YunX-Desktop（AGPL-3.0）的 C# / WPF 重构版。
// 本程序是自由软件：你可以依据 GNU AGPL-3.0 的条款重新分发或修改它。
// 完整协议见仓库根目录 LICENSE。

using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using YunX.Core.Net;
using YunX.Core.Util;

namespace YunX.GitHub;

/// <summary>
/// GitHub API 响应缓存（进程内，原始 JSON 文本）。
///
/// <para>设计参考 YunX-Desktop，但补充了两点：</para>
/// <list type="bullet">
///   <item><b>失败也缓存</b>（1 分钟短 TTL）：避免对限流/404 的路径反复打 API，
///         否则一次限流会让界面疯狂重试直到更严重；</item>
///   <item><b>in-flight 去重</b>：同一 key 并发只发一次请求，其余等同一份结果。</item>
/// </list>
/// </summary>
public sealed class GitHubResponseCache
{
    private static readonly TimeSpan SuccessTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan FailTtl = TimeSpan.FromMinutes(1);
    private const int MaxEntries = 256;

    private sealed record Entry(string? Value, DateTimeOffset At, bool Success);

    private readonly ConcurrentDictionary<string, Entry> _cache = new();
    private readonly ConcurrentDictionary<string, Task<string?>> _inFlight = new();

    public async Task<string?> GetOrFetchAsync(string key, Func<Task<string?>> fetch,
        CancellationToken ct = default)
    {
        if (_cache.TryGetValue(key, out var hit))
        {
            var ttl = hit.Success ? SuccessTtl : FailTtl;
            if (DateTimeOffset.UtcNow - hit.At <= ttl) return hit.Value;
            _cache.TryRemove(key, out _);
        }

        if (_inFlight.TryGetValue(key, out var running))
            return await running.WaitAsync(ct).ConfigureAwait(false);

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_inFlight.TryAdd(key, tcs.Task))
        {
            if (_inFlight.TryGetValue(key, out var existing))
                return await existing.WaitAsync(ct).ConfigureAwait(false);
        }

        try
        {
            var result = await fetch().ConfigureAwait(false);
            _cache[key] = new Entry(result, DateTimeOffset.UtcNow, result != null);
            EvictIfNeeded();
            tcs.SetResult(result);
            return result;
        }
        catch (Exception e)
        {
            _cache[key] = new Entry(null, DateTimeOffset.UtcNow, false);
            tcs.SetException(e);
            throw;
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    private void EvictIfNeeded()
    {
        if (_cache.Count <= MaxEntries) return;
        foreach (var kv in _cache.OrderBy(k => k.Value.At).Take(_cache.Count - MaxEntries))
            _cache.TryRemove(kv.Key, out _);
    }

    /// <summary>清空缓存。401（Token 失效）时必须调用，否则已缓存的失败条目会让「修好之后」仍然失败。</summary>
    public void Clear() => _cache.Clear();
}

/// <summary>
/// GitHub API 客户端。
///
/// <para><b>两个关键行为（都是原项目踩过 / 遗漏的坑）</b>：</para>
/// <list type="number">
///   <item><b>401 自愈</b>：GitHub 对带「无效」Authorization 头的一切请求都返回 401，
///         连公开仓库也会全量失败 —— 不只是限额问题。所以要回调上层清掉 Token 才能恢复匿名访问。
///         否则用户填错一个 Token，整个应用就"什么都打不开"且看不出原因。</item>
///   <item><b>私有仓库不走镜像</b>：实测镜像<b>不转发</b> <c>Authorization</c> 头
///         （直连返回 401 时镜像却返回 200 的匿名内容），因此私有仓库走镜像必然失败，
///         且报错会莫名其妙。有 Token 时只走直连。</item>
/// </list>
/// </summary>
public sealed class GitHubApiClient
{
    private const string ApiBase = "https://api.github.com";

    /// <summary>
    /// 能转发 API 的镜像（顺序 = 优先级）。
    ///
    /// <para><b>为什么不能把所有镜像都试一遍</b>：实测（2026-10，enso-org/enso@2025.3.4）
    /// <c>ghproxy.net</c> 与 <c>ghfast.top</c> 对 <c>api.github.com</c> 一律返回 403
    /// “Invalid input.”（它们只认 releases/raw/archive，不转发 API）。
    /// 而 <c>gh-proxy.com</c> / <c>cdn.gh-proxy.org</c> 能正常转发（实测 1.6~2.2s 返回 41KB JSON）。
    /// 把不认 API 的镜像排在前面，每次都要白等一轮 403 —— 这正是「解析 Release 卡」的来源之一。</para>
    /// </summary>
    private static readonly string[] ApiMirrors =
    {
        "https://gh-proxy.com/",
        "https://cdn.gh-proxy.org/",
    };

    private readonly HttpClientProvider _provider;
    private readonly GitHubResponseCache _cache = new();
    private readonly Func<string?> _tokenProvider;

    /// <summary>Token 被 GitHub 拒绝（401）时回调，**只回调一次**。上层应清空该 Token。</summary>
    public Action? OnUnauthorized { get; set; }

    public Action<string>? Log { get; set; }

    private int _unauthorizedNotified;
    private int _anonymousRetryLogged;

    /// <summary>最近一次响应的状态码（供 UI 提示限流）。</summary>
    public int LastStatusCode { get; private set; }

    /// <summary>
    /// 最近一次响应里 GitHub 返回的配额信息（<c>X-RateLimit-*</c>）。
    ///
    /// <para>知道「还剩几次」比知道「被限流了」有用得多：用户在设置页看到
    /// 剩 2 次时，会主动去配 Token，而不是等下一次解析莫名失败。
    /// 值取自响应头，不需要额外的 <c>/rate_limit</c> 请求（那本身也占配额）。</para>
    /// </summary>
    public (long Limit, long Remaining, DateTimeOffset? ResetAt)? RateLimit { get; private set; }

    /// <summary>配额变化时通知（UI 可据此刷新显示）。</summary>
    public Action? RateLimitChanged { get; set; }

    /// <summary>从响应头读取配额信息。</summary>
    private void ReadRateLimit(HttpResponseMessage resp)
    {
        try
        {
            if (!resp.Headers.TryGetValues("X-RateLimit-Limit", out var ls) ||
                !resp.Headers.TryGetValues("X-RateLimit-Remaining", out var rs))
                return;
            if (!long.TryParse(ls.FirstOrDefault(), out long limit) ||
                !long.TryParse(rs.FirstOrDefault(), out long remain))
                return;

            DateTimeOffset? reset = null;
            if (resp.Headers.TryGetValues("X-RateLimit-Reset", out var es) &&
                long.TryParse(es.FirstOrDefault(), out long epoch))
                reset = DateTimeOffset.FromUnixTimeSeconds(epoch).ToLocalTime();

            RateLimit = (limit, remain, reset);
            try { RateLimitChanged?.Invoke(); } catch { /* 通知失败不影响请求 */ }
        }
        catch { /* 头解析失败就当作不知道 */ }
    }

    public GitHubApiClient(HttpClientProvider provider, Func<string?> tokenProvider)
    {
        _provider = provider;
        _tokenProvider = tokenProvider;
    }

    private string? Token => _tokenProvider() is { Length: > 0 } t ? t : null;

    /// <summary>当前是否配置了 Token（供上层决定「值不值得调 API」）。</summary>
    public bool HasToken => Token != null;

    /// <summary>
    /// 仓库是否为私有（决定了这次请求能不能走镜像）。
    ///
    /// <para>null = 还不知道。首次遇到某个仓库时是 null，探明后固定下来，
    /// 后续同一仓库的请求直接用结论 —— 否则每次都要为「公开仓库」多付一次往返。</para>
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _repoPrivate = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 记下某仓库的可见性（由 <c>ContentResolver</c> 从 repo JSON 的 <c>private</c> 字段喂进来）。
    /// </summary>
    public void SetRepoVisibility(string owner, string repo, bool isPrivate)
        => _repoPrivate[$"{owner}/{repo}"] = isPrivate;

    /// <summary>
    /// 本次请求是否只能直连。
    ///
    /// <para><b>早期实现是「有 Token 就全部直连」</b>，结果是：配了 Token 的用户，
    /// 连下载公开仓库也放弃了镜像 —— 而公开仓库的资产走镜像往往比直连快得多，
    /// Token 在这里没有任何收益（公开仓库不需要鉴权）。</para>
    ///
    /// <para>现在只有两种情况强制直连：</para>
    /// <list type="bullet">
    ///   <item>没有 Token → 谈不上鉴权，随便走（此时 <see cref="UseDirectOnly"/> 为 false，
    ///         但也不带 Authorization）；</item>
    ///   <item>仓库<b>确认是私有</b> → 镜像不转发 <c>Authorization</c> 头，走镜像必然 404/401。</item>
    /// </list>
    /// </summary>
    private bool MustGoDirect(string path, out bool knownPrivate)
    {
        knownPrivate = false;
        if (Token == null) return false;

        // path 形如 /repos/{owner}/{repo}/...；认不出来就保守直连（不知道就别冒险）
        if (!TryOwnerRepo(path, out string owner, out string repo)) return true;

        if (_repoPrivate.TryGetValue($"{owner}/{repo}", out bool priv))
        {
            knownPrivate = priv;
            return priv;
        }
        return false;   // 可见性未知 → 允许先走镜像试，反正公开仓库占绝大多数
    }

    private static bool TryOwnerRepo(string path, out string owner, out string repo)
    {
        owner = repo = "";
        // /repos/{owner}/{repo}[...]
        const string prefix = "/repos/";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var seg = path[prefix.Length..].Split(new[] { '/' }, 3);
        if (seg.Length < 2 || seg[0].Length == 0 || seg[1].Length == 0) return false;
        owner = seg[0];
        repo = seg[1];
        return true;
    }

    public async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken ct = default)
    {
        var text = await GetTextAsync(path, ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(text)) return null;
        try
        {
            return JsonDocument.Parse(text);
        }
        catch
        {
            return null;
        }
    }

    public async Task<string?> GetTextAsync(string path, CancellationToken ct = default)
    {
        string token = Token ?? "anon";
        string key = $"{path}|{token.GetHashCode()}";

        return await _cache.GetOrFetchAsync(key, async () =>
        {
            string apiUrl = ApiBase + path;

            // 候选线路：
            //   · 仓库确认私有 → 只走直连（镜像不转发 Authorization，走镜像必失败）
            //   · 否则直连 + 能转发 API 的镜像（公开仓库走镜像通常更快，且不吃 API 配额）
            //
            // ★ 只放**实测能转发 API** 的镜像。早期这里复用 MirrorRegistry 的
            //   gh-proxy.com + ghfast.top，而 ghfast.top 对 api.github.com 返回
            //   403 "Invalid input." —— 于是「直连被限流 → 白等一轮 403 → 才轮到能通的线路」。
            bool directOnly = MustGoDirect(path, out bool _);
            var candidates = directOnly
                ? new List<string> { apiUrl }
                : ApiMirrors.Select(m => m + apiUrl).Prepend(apiUrl).ToList();

            // Token 被拒（401）后改匿名重试。
            //
            // ★ 状态必须**按候选 URL 独立**：早期用一个共享标志，结果第一条线路
            //   401 后置位，后面每条线路都会跟着不带 Token —— 私有仓库会全部失败。
            //   另外「重试」不应消耗外层线路循环的次数，所以用 while 内层小循环。
            var pending = new Queue<string>(candidates);
            bool authDisabled = false;      // 401 之后全局匿名（Token 已确认无效）

            while (pending.Count > 0)
            {
                string url = pending.Dequeue();

                // 同一个 URL 最多试两次：先按当前身份，被 401 就改匿名再来一次。
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    bool anonymous = attempt > 0 || authDisabled;
                try
                {
                    using var client = _provider.CreateClient();
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                    req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

                    // 公开仓库经镜像时**不**带 Token：镜像不转发该头，带了也没用，
                    // 反而把一个长期凭证交给了第三方。只有直连（含私有仓库）才带。
                    bool viaMirror = !url.StartsWith(ApiBase, StringComparison.Ordinal);
                    if (Token != null && !viaMirror && !anonymous)
                        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");

                    using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
                    LastStatusCode = (int)resp.StatusCode;
                    NoteStatusCode((int)resp.StatusCode);

                    if (resp.StatusCode == HttpStatusCode.OK)
                        return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

                    if (resp.StatusCode is HttpStatusCode.Unauthorized)
                    {
                        // 401：Token 无效。**公开仓库不该因此失败** ——
                        // 早期这里直接 return null，于是 Token 一失效，连公开仓库的 API
                        // 都用不了，被迫走慢得多的 HTML 兜底（实测多花 5 秒）。
                        // 这里对同一个 URL 改匿名再试一次：公开仓库匿名照样能读。
                        LastStatusCode = 401;
                        if (!anonymous)
                        {
                            // 「重试」提示同样只该出现一次：它是 Token 状态变化，
                            // 而不是每条线路各发生一次。
                            if (Interlocked.Exchange(ref _anonymousRetryLogged, 1) == 0)
                                Log?.Invoke("Token 无效，改以匿名身份重试");
                            authDisabled = true;    // 本次请求后续线路也不再带 Token
                            continue;               // 重来一次（attempt+1 → 匿名）
                        }
                        // 匿名也 401 —— 这个仓库确实需要权限，换线路也没用
                        return null;
                    }

#if NET48
                    // net48 的 HttpStatusCode 枚举没有 TooManyRequests 具名成员，用数值 429
                    if (resp.StatusCode is HttpStatusCode.Forbidden
                        || (int)resp.StatusCode == 429)
#else
                    if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
#endif
                    {
                        ReadRateLimit(resp);
                        var body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        // 说清"为什么失败、怎么恢复"，而不是只丢一句 rate limit exceeded
                        if (RateLimit is { Remaining: 0 } rl && rl.ResetAt is { } reset)
                            Log?.Invoke($"GitHub API 配额已用完（{rl.Limit}/小时，" +
                                        $"{(reset - DateTimeOffset.Now).TotalMinutes:0} 分钟后恢复）。" +
                                        "配一个 Token 可提到 5000 次/小时；" +
                                        "下载本身不消耗配额，不受影响。");
                        else
                            Log?.Invoke("GitHub API 限流或拒绝：" + Shorten(body));
                        return null;
                    }

                    if (resp.StatusCode == HttpStatusCode.NotFound)
                        return null;
                }
                catch (Exception e)
                {
                    Log?.Invoke($"API 请求失败（{UrlKindName(url)}）：{e.GetType().Name}");
                    break;      // 连接层面失败，换下一个候选，不必再匿名重试
                }
                }
            }
            return null;
        }, ct).ConfigureAwait(false);
    }

    private void NoteStatusCode(int code)
    {
        if (code != 401) return;
        if (Interlocked.Exchange(ref _unauthorizedNotified, 1) != 0) return;

        // 缓存里可能存了基于失效 Token 的失败条目，必须清掉
        _cache.Clear();
        Log?.Invoke("Token 无效或已过期（401），已自动降级为匿名访问。请到设置里更新 Token。");
        OnUnauthorized?.Invoke();
    }

    /// <summary>
    /// 取任意 URL 的文本（raw 文件、镜像前缀）。
    /// <b>不计入 API 配额</b>：raw.githubusercontent.com 走的是 CDN 而非 API。
    /// </summary>
    public async Task<string?> GetRawTextAsync(string url, CancellationToken ct = default)
    {
        // 与 API 路径同一套判定：只有**确认私有**的仓库才强制直连。
        // 早期这里是「有 Token 就直连」，导致公开仓库的 raw 也放弃了镜像。
        bool directOnly = PrivateOfRawUrl(url);
        var candidates = directOnly
            ? new List<string> { url }
            : new List<string> { url }.Concat(ApiMirrors.Select(m => m + url)).ToList();

        foreach (var candidate in candidates)
        {
            try
            {
                using var client = _provider.CreateClient();
                using var req = new HttpRequestMessage(HttpMethod.Get, candidate);

                // 经镜像时不带 Token（镜像不转发，且不该把凭证交给第三方）
                bool viaMirror = !candidate.Equals(url, StringComparison.Ordinal);
                if (Token != null && !viaMirror)
                    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");

                using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.OK)
                    return await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            }
            catch
            {
                // 试下一个候选
            }
        }
        return null;
    }

    /// <summary>
    /// raw URL 对应的仓库是否确认私有。
    /// 形如 <c>https://raw.githubusercontent.com/{owner}/{repo}/{ref}/{path}</c>。
    /// </summary>
    private bool PrivateOfRawUrl(string url)
    {
        if (Token == null) return false;
        const string prefix = "https://raw.githubusercontent.com/";
        if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;

        var seg = url[prefix.Length..].Split(new[] { '/' }, 3);
        if (seg.Length < 2) return false;
        return _repoPrivate.TryGetValue($"{seg[0]}/{seg[1]}", out bool priv) && priv;
    }

    /// <summary>重置 401 提醒，让用户换 Token 后能再次收到提示。</summary>
    public void ResetUnauthorizedFlag()
    {
        Interlocked.Exchange(ref _unauthorizedNotified, 0);
        Interlocked.Exchange(ref _anonymousRetryLogged, 0);
    }

    private static string UrlKindName(string url) =>
        url.Contains("api.github.com") && url.StartsWith("https://api") ? "直连" : "镜像";

    private static string Shorten(string s) =>
        s.Length <= 160 ? s.Replace('\n', ' ') : s[..160].Replace('\n', ' ') + "…";

    // ===================== 常用接口 =====================

    public async Task<JsonDocument?> GetRepoAsync(string owner, string repo,
        CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"/repos/{owner}/{repo}", ct).ConfigureAwait(false);

        // ★ 顺手把「这是不是私有仓库」记下来：后续所有 API / raw / 下载的线路选择
        //   都依赖它。判断公开/私有只能靠这次响应里的 private 字段。
        if (doc?.RootElement.TryGetProperty("private", out var p) == true)
            SetRepoVisibility(owner, repo, p.ValueKind == JsonValueKind.True);

        return doc;
    }

    /// <summary>取默认分支；失败返回 "HEAD"（codeload 支持 HEAD）。</summary>
    public async Task<string> GetDefaultBranchAsync(string owner, string repo,
        CancellationToken ct = default)
    {
        using var doc = await GetRepoAsync(owner, repo, ct).ConfigureAwait(false);
        if (doc?.RootElement.TryGetProperty("default_branch", out var b) == true)
        {
            var s = b.GetString();
            if (!string.IsNullOrEmpty(s)) return s;
        }
        return "HEAD";
    }

    public async Task<JsonDocument?> GetReleaseByTagAsync(string owner, string repo, string tag,
        CancellationToken ct = default)
        => await GetJsonAsync($"/repos/{owner}/{repo}/releases/tags/{Uri.EscapeDataString(tag)}", ct)
            .ConfigureAwait(false);

    public async Task<JsonDocument?> GetLatestReleaseAsync(string owner, string repo,
        CancellationToken ct = default)
        => await GetJsonAsync($"/repos/{owner}/{repo}/releases/latest", ct).ConfigureAwait(false);

    public async Task<JsonDocument?> GetGistAsync(string gistId, CancellationToken ct = default)
        => await GetJsonAsync($"/gists/{gistId}", ct).ConfigureAwait(false);

    /// <summary>把 release JSON 解析为资产列表；无资产时用源码 zip 兜底。</summary>
    public static List<ReleaseAsset> ParseAssets(JsonDocument doc)
    {
        var list = new List<ReleaseAsset>();
        if (!doc.RootElement.TryGetProperty("assets", out var assets)) return list;

        string tag = doc.RootElement.TryGetProperty("tag_name", out var t)
            ? t.GetString() ?? "" : "";

        foreach (var a in assets.EnumerateArray())
        {
            string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            long size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
            string url = a.TryGetProperty("browser_download_url", out var u)
                ? u.GetString() ?? "" : "";
            if (string.IsNullOrEmpty(url)) continue;
            list.Add(new ReleaseAsset { Name = name, Size = size, Url = url, Tag = tag });
        }

        if (list.Count == 0 &&
            doc.RootElement.TryGetProperty("zipball_url", out var zb) &&
            zb.GetString() is { Length: > 0 } zip)
        {
            list.Add(new ReleaseAsset
            {
                Name = $"源码包 source ({tag}).zip", Size = 0, Url = zip, Tag = tag,
                Note = "仓库快照",
            });
        }
        return list;
    }
}
