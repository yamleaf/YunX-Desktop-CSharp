using System.Text.Json;

namespace YunX.GitHub;

/// <summary>仓库文件树条目。</summary>
public sealed class RepoTreeEntry
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public string Sha { get; init; } = "";
    public string LastCommitDate { get; init; } = "";
    public string SizeText => IsDirectory ? "" : Core.Util.Humanize.Size(Size);
}

/// <summary>仓库基本信息。</summary>
public sealed class RepoSummary
{
    public required string FullName { get; init; }
    public string Description { get; init; } = "";
    public string DefaultBranch { get; init; } = "HEAD";
    public int Stars { get; init; }
    public int Forks { get; init; }
    public string Language { get; init; } = "";
    public long SizeKb { get; init; }
    /// <summary>fork 来源（非 fork 时为 null）——原项目会显示这个，方便判断是不是原创。</summary>
    public string? ForkedFrom { get; init; }
    public string UpdatedAt { get; init; } = "";
    public bool IsPrivate { get; init; }
}

/// <summary>账号（用户或组织）下的仓库条目。</summary>
public sealed class RepoListItem
{
    public required string FullName { get; init; }
    public string Description { get; init; } = "";
    public int Stars { get; init; }
    public string Language { get; init; } = "";
    public string UpdatedAt { get; init; } = "";
    public bool IsFork { get; init; }
}

/// <summary>
/// GitHub 浏览：文件树、Releases、账号仓库列表。
///
/// <para><b>API 消耗控制</b>：未认证每小时只有 60 次配额，浏览很容易打满。
/// 所以这里做了两件事：</para>
/// <list type="bullet">
///   <item>所有请求都走 <see cref="GitHubResponseCache"/>（成功 10 分钟、失败 1 分钟，in-flight 去重）；</item>
///   <item>目录的「最后提交时间」用<b>一次</b> commits 请求批量取，不是逐文件请求
///         （逐文件请求会瞬间打满配额 —— 这是设计这类浏览器的关键决策）。</item>
/// </list>
/// </summary>
public sealed class GitHubBrowser
{
    private readonly GitHubApiClient _api;

    public Action<string>? Log { get; set; }

    public GitHubBrowser(GitHubApiClient api) => _api = api;

    /// <summary>取仓库概要（含 fork 来源、默认分支）。</summary>
    public async Task<RepoSummary?> GetRepoAsync(string owner, string repo,
        CancellationToken ct = default)
    {
        using var doc = await _api.GetJsonAsync($"/repos/{owner}/{repo}", ct)
            .ConfigureAwait(false);
        if (doc == null) return null;

        var r = doc.RootElement;
        string? forkedFrom = null;
        if (r.TryGetProperty("fork", out var isFork) && isFork.ValueKind == JsonValueKind.True &&
            r.TryGetProperty("parent", out var parent) &&
            parent.TryGetProperty("full_name", out var pfn))
        {
            forkedFrom = pfn.GetString();
        }

        return new RepoSummary
        {
            FullName = Str(r, "full_name"),
            Description = Str(r, "description"),
            DefaultBranch = Str(r, "default_branch") is { Length: > 0 } b ? b : "HEAD",
            Stars = r.TryGetProperty("stargazers_count", out var s) ? s.GetInt32() : 0,
            Forks = r.TryGetProperty("forks_count", out var f) ? f.GetInt32() : 0,
            Language = Str(r, "language"),
            SizeKb = r.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0,
            ForkedFrom = forkedFrom,
            UpdatedAt = Str(r, "updated_at"),
            IsPrivate = r.TryGetProperty("private", out var p) && p.ValueKind == JsonValueKind.True,
        };
    }

    /// <summary>
    /// 列目录。路径为空表示根目录。
    ///
    /// <para>「最后提交时间」用一次 <c>commits?path=</c> 拿不到批量结果，
    /// 所以这里对每个条目只取 <b>sha</b>，由上层按需再查（延迟加载）——
    /// 避免一进目录就发 N 个请求打满配额。</para>
    /// </summary>
    public async Task<List<RepoTreeEntry>> ListDirectoryAsync(string owner, string repo,
        string? path = null, string? gitRef = null, CancellationToken ct = default)
    {
        string p = path?.Trim('/') ?? "";
        string sub = p.Length > 0 ? "/" + p : "";
        string r = gitRef ?? await GetDefaultBranchAsync(owner, repo, ct).ConfigureAwait(false);

        using var doc = await _api.GetJsonAsync(
            $"/repos/{owner}/{repo}/contents{sub}?ref={Uri.EscapeDataString(r)}", ct)
            .ConfigureAwait(false);
        if (doc == null) return new List<RepoTreeEntry>();

        var list = new List<RepoTreeEntry>();
        // contents 接口返回数组（目录）或对象（单文件）
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

        foreach (var e in doc.RootElement.EnumerateArray())
        {
            string type = Str(e, "type");
            list.Add(new RepoTreeEntry
            {
                Name = Str(e, "name"),
                Path = Str(e, "path"),
                IsDirectory = type == "dir",
                Size = e.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0,
                Sha = Str(e, "sha"),
            });
        }

        // 目录在前、同类按名称排序（与 GitHub 网页一致）
        return list
            .OrderByDescending(x => x.IsDirectory)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 批量补充目录条目的「最后提交时间」。
    /// 由 UI 在用户展开/悬停时按需调用，一次最多补 <paramref name="limit"/> 个。
    /// </summary>
    public async Task<Dictionary<string, string>> GetLastCommitDatesAsync(string owner,
        string repo, IEnumerable<string> paths, string? gitRef = null, int limit = 20,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, string>();
        string r = gitRef ?? "HEAD";

        foreach (var path in paths.Take(limit))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var doc = await _api.GetJsonAsync(
                    $"/repos/{owner}/{repo}/commits?path={Uri.EscapeDataString(path)}" +
                    $"&sha={Uri.EscapeDataString(r)}&per_page=1", ct).ConfigureAwait(false);
                if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Array) continue;
                if (doc.RootElement.GetArrayLength() == 0) continue;

                var first = doc.RootElement[0];
                if (first.TryGetProperty("commit", out var commit) &&
                    commit.TryGetProperty("committer", out var committer) &&
                    committer.TryGetProperty("date", out var date))
                {
                    string d = date.GetString() ?? "";
                    if (DateTimeOffset.TryParse(d, out var dt))
                        result[path] = dt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
                    else if (d.Length > 0)
                        result[path] = d;
                }
            }
            catch
            {
                // 单个失败不影响其它
            }
        }
        return result;
    }

    /// <summary>取 README 原文（优先 README.md，其次 README / readme.rst）。</summary>
    public async Task<string?> GetReadmeAsync(string owner, string repo, string? gitRef = null,
        CancellationToken ct = default)
    {
        // 用 raw 而不是 API：raw 不消耗 API 配额
        string r = gitRef ?? await GetDefaultBranchAsync(owner, repo, ct).ConfigureAwait(false);

        foreach (var name in new[] { "README.md", "readme.md", "README.MD", "README", "README.rst" })
        {
            string url = GitHubUrlParser.RawUrl(owner, repo, r, name);
            var text = await TryFetchRawAsync(url, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(text)) return text;
        }
        return null;
    }

    private async Task<string?> TryFetchRawAsync(string url, CancellationToken ct)
    {
        // 复用 API 客户端的 HTTP 栈（含镜像回退与 DoH）
        try
        {
            return await _api.GetRawTextAsync(url, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 账号（用户或组织）的仓库列表，支持分页。
    /// 自动区分 User / Organization —— 两者接口路径不同。
    /// </summary>
    public async Task<List<RepoListItem>> ListUserReposAsync(string owner, int page = 1,
        int perPage = 30, CancellationToken ct = default)
    {
        // 先判断类型
        string kind = "users";
        using (var userDoc = await _api.GetJsonAsync($"/users/{owner}", ct).ConfigureAwait(false))
        {
            if (userDoc?.RootElement.TryGetProperty("type", out var t) == true &&
                t.GetString()?.Equals("Organization", StringComparison.OrdinalIgnoreCase) == true)
                kind = "orgs";
        }

        using var doc = await _api.GetJsonAsync(
            $"/{kind}/{owner}/repos?page={page}&per_page={perPage}&sort=updated", ct)
            .ConfigureAwait(false);
        if (doc == null || doc.RootElement.ValueKind != JsonValueKind.Array)
            return new List<RepoListItem>();

        var list = new List<RepoListItem>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            list.Add(new RepoListItem
            {
                FullName = Str(e, "full_name"),
                Description = Str(e, "description"),
                Stars = e.TryGetProperty("stargazers_count", out var s) ? s.GetInt32() : 0,
                Language = Str(e, "language"),
                UpdatedAt = Str(e, "updated_at"),
                IsFork = e.TryGetProperty("fork", out var f) && f.ValueKind == JsonValueKind.True,
            });
        }
        return list;
    }

    private async Task<string> GetDefaultBranchAsync(string owner, string repo,
        CancellationToken ct)
        => await _api.GetDefaultBranchAsync(owner, repo, ct).ConfigureAwait(false);

    private static string Str(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.ToString(),
            _ => "",
        };
    }
}
