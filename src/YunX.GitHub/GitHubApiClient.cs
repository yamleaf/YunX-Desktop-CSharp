// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 yamleaf
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

    private readonly HttpClientProvider _provider;
    private readonly GitHubResponseCache _cache = new();
    private readonly Func<string?> _tokenProvider;

    /// <summary>Token 被 GitHub 拒绝（401）时回调，**只回调一次**。上层应清空该 Token。</summary>
    public Action? OnUnauthorized { get; set; }

    public Action<string>? Log { get; set; }

    private int _unauthorizedNotified;

    /// <summary>最近一次响应的状态码（供 UI 提示限流）。</summary>
    public int LastStatusCode { get; private set; }

    public GitHubApiClient(HttpClientProvider provider, Func<string?> tokenProvider)
    {
        _provider = provider;
        _tokenProvider = tokenProvider;
    }

    private string? Token => _tokenProvider() is { Length: > 0 } t ? t : null;

    /// <summary>
    /// 是否应使用直连（不走镜像）。
    /// 有 Token 时必须直连：镜像不转发鉴权头，私有仓库走镜像一定失败。
    /// </summary>
    private bool UseDirectOnly => Token != null;

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

            // 无 Token 时允许走镜像（提升可用性）；有 Token 时只走直连
            var candidates = UseDirectOnly
                ? new List<string> { apiUrl }
                : new List<string>
                {
                    apiUrl,
                    "https://gh-proxy.com/" + apiUrl,
                    "https://ghfast.top/" + apiUrl,
                };

            foreach (var url in candidates)
            {
                try
                {
                    using var client = _provider.CreateClient();
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
                    req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
                    if (Token != null)
                        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");

                    using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
                    LastStatusCode = (int)resp.StatusCode;
                    NoteStatusCode((int)resp.StatusCode);

                    if (resp.StatusCode == HttpStatusCode.OK)
                        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                    if (resp.StatusCode is HttpStatusCode.Unauthorized)
                    {
                        // 401：Token 无效。不要再试其它镜像 —— 结果一样，且会掩盖问题
                        LastStatusCode = 401;
                        return null;
                    }

                    if (resp.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                    {
                        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                        Log?.Invoke("GitHub API 限流或拒绝：" + Shorten(body));
                        return null;
                    }

                    if (resp.StatusCode == HttpStatusCode.NotFound)
                        return null;
                }
                catch (Exception e)
                {
                    Log?.Invoke($"API 请求失败（{UrlKindName(url)}）：{e.GetType().Name}");
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
        // 有 Token 时只走直连（隐私内容不应经第三方镜像）
        var candidates = UseDirectOnly
            ? new List<string> { url }
            : new List<string>
            {
                url,
                "https://gh-proxy.com/" + url,
                "https://ghfast.top/" + url,
            };

        foreach (var candidate in candidates)
        {
            try
            {
                using var client = _provider.CreateClient();
                using var req = new HttpRequestMessage(HttpMethod.Get, candidate);
                if (Token != null)
                    req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {Token}");

                using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.OK)
                    return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            }
            catch
            {
                // 试下一个候选
            }
        }
        return null;
    }

    /// <summary>重置 401 提醒，让用户换 Token 后能再次收到提示。</summary>
    public void ResetUnauthorizedFlag() => Interlocked.Exchange(ref _unauthorizedNotified, 0);

    private static string UrlKindName(string url) =>
        url.Contains("api.github.com") && url.StartsWith("https://api") ? "直连" : "镜像";

    private static string Shorten(string s) =>
        s.Length <= 160 ? s.Replace('\n', ' ') : s[..160].Replace('\n', ' ') + "…";

    // ===================== 常用接口 =====================

    public async Task<JsonDocument?> GetRepoAsync(string owner, string repo,
        CancellationToken ct = default)
    {
        var doc = await GetJsonAsync($"/repos/{owner}/{repo}", ct).ConfigureAwait(false);
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
