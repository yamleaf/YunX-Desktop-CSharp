using System.Net;
using System.Text.RegularExpressions;
using YunX.Core.Mirrors;
using YunX.Core.Net;
using YunX.Core.Util;

namespace YunX.GitHub;

/// <summary>
/// 把用户输入解析成「一组可下载的 (URL, 文件名)」。
/// </summary>
public sealed partial class ContentResolver
{
    private readonly GitHubApiClient _api;
    private readonly HttpClientProvider _provider;

    public Action<string>? Log { get; set; }

    public ContentResolver(GitHubApiClient api, HttpClientProvider provider)
    {
        _api = api;
        _provider = provider;
    }

    public async Task<ResolvedContent> ResolveAsync(string input, MirrorPool pool,
        CancellationToken ct = default)
    {
        var target = GitHubUrlParser.Parse(input);
        Log?.Invoke($"识别为：{target.KindLabel} · {target.FullName}");

        switch (target.Kind)
        {
            case TargetKind.File:
            {
                // raw 链接若没带具体分支，用 API 查默认分支（否则 HEAD 在部分镜像上不认）
                string refName = target.Ref ?? "";
                if (refName.Length == 0 || refName.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    refName = await _api.GetDefaultBranchAsync(target.Owner, target.Repo, ct)
                        .ConfigureAwait(false);
                    Log?.Invoke($"默认分支：{refName}");
                }
                string url = GitHubUrlParser.RawUrl(target.Owner, target.Repo, refName,
                    target.Path ?? "");
                string name = FileNamePolicy.SanitizeFileName(
                    (target.Path ?? "file.bin").Split('/')[^1]);
                return new ResolvedContent
                {
                    Kind = TargetKind.File,
                    Owner = target.Owner,
                    Repo = target.Repo,
                    GitRef = target.Ref ?? "",
                    Title = $"{target.FullName} · {name}",
                    Note = $"分支 {refName}",
                    Items = { (url, name, 0L) },
                };
            }

            case TargetKind.Repo:
            case TargetKind.Tree:
            {
                string refName = target.Ref ?? "";
                if (refName.Length == 0 || refName.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
                {
                    refName = await _api.GetDefaultBranchAsync(target.Owner, target.Repo, ct)
                        .ConfigureAwait(false);
                    Log?.Invoke($"默认分支：{refName}");
                }
                string url = GitHubUrlParser.ArchiveUrl(target.Owner, target.Repo, refName);
                string name = $"{target.Repo}-{refName}.zip";
                string note = "整仓源码 zip";
                if (target.Kind == TargetKind.Tree && !string.IsNullOrEmpty(target.Path))
                    note = $"目录 {target.Path} → 打包整仓 zip（GitHub 不提供单目录包）";

                return new ResolvedContent
                {
                    Kind = TargetKind.Repo,
                    Owner = target.Owner,
                    Repo = target.Repo,
                    GitRef = target.Ref ?? "",
                    Title = $"{target.FullName} @ {refName}",
                    Note = note,
                    Items = { (url, name, 0L) },
                };
            }

            case TargetKind.Archive:
            {
                string name = target.SuggestedName ?? "archive.zip";
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                    name += ".zip";
                return new ResolvedContent
                {
                    Kind = TargetKind.Archive,
                    Owner = target.Owner,
                    Repo = target.Repo,
                    GitRef = target.Ref ?? "",
                    Title = $"{target.FullName} @ {target.Ref ?? "默认"}",
                    Note = "归档直链",
                    Items = { (target.Url ?? "", name, 0L) },
                };
            }

            case TargetKind.Asset:
            {
                string name = FileNamePolicy.SanitizeFileName(
                    target.SuggestedName ?? target.AssetName?.Split('/')[^1] ?? "download.bin");
                return new ResolvedContent
                {
                    Kind = TargetKind.Asset,
                    Owner = target.Owner,
                    Repo = target.Repo,
                    GitRef = target.Ref ?? "",
                    Title = $"{target.FullName} · {name}",
                    Note = string.IsNullOrEmpty(target.Tag) ? "Release 资产" : $"Release {target.Tag}",
                    Items = { (target.Url ?? "", name, 0L) },
                };
            }

            case TargetKind.Releases:
            {
                var assets = await LoadReleaseAssetsAsync(target, pool, ct).ConfigureAwait(false);
                if (assets.Count == 0)
                {
                    throw new ParseException(
                        "没有取到该 Release 的资产列表。可改为下载「仓库源码包」，" +
                        "或在设置里填入 GitHub Token 以提高 API 成功率。");
                }
                Log?.Invoke($"取到 {assets.Count} 个资产");
                var content = new ResolvedContent
                {
                    Kind = TargetKind.Releases,
                    Owner = target.Owner,
                    Repo = target.Repo,
                    GitRef = target.Ref ?? "",
                    Title = $"{target.FullName} · Release",
                    Note = string.IsNullOrEmpty(target.Tag) ? "最新 Release" : $"Release {target.Tag}",
                };
                foreach (var a in assets)
                    content.Items.Add((a.Url, FileNamePolicy.SanitizeFileName(a.Name), a.Size));
                return content;
            }

            case TargetKind.Gist:
            {
                using var doc = await _api.GetGistAsync(target.Repo, ct).ConfigureAwait(false);
                if (doc?.RootElement.TryGetProperty("zipball_url", out var zb) == true &&
                    zb.GetString() is { Length: > 0 } zip)
                {
                    int fileCount = 0;
                    if (doc.RootElement.TryGetProperty("files", out var files))
                        fileCount = files.EnumerateObject().Count();

                    return new ResolvedContent
                    {
                        Kind = TargetKind.Gist,
                        Owner = target.Owner,
                        Repo = target.Repo,
                        GitRef = target.Ref ?? "",
                        Title = $"Gist {target.Repo}",
                        Note = $"{fileCount} 个文件",
                        Items = { (zip, $"gist-{target.Repo}.zip", 0L) },
                    };
                }
                throw new ParseException("无法获取 Gist 下载地址（私有 Gist 需要在设置里填 Token）");
            }

            default:
                throw new ParseException("暂不支持的链接类型");
        }
    }

    /// <summary>
    /// 取 Release 资产：先 API，失败再用 HTML 兜底。
    ///
    /// <para>兜底存在的意义：未认证的 GitHub API 每小时只有 60 次配额，
    /// 用户浏览几个仓库就可能耗尽。此时网页本身仍可访问，
    /// 从 <c>releases/expanded_assets</c> 页面抓 href 就能拿到资产列表与大小。</para>
    /// </summary>
    private async Task<List<ReleaseAsset>> LoadReleaseAssetsAsync(GitHubTarget target,
        MirrorPool pool, CancellationToken ct)
    {
        string? tag = target.Tag;
        if (tag == "latest") tag = null;

        try
        {
            using var doc = tag != null
                ? await _api.GetReleaseByTagAsync(target.Owner, target.Repo, tag, ct).ConfigureAwait(false)
                : await _api.GetLatestReleaseAsync(target.Owner, target.Repo, ct).ConfigureAwait(false);

            if (doc != null)
            {
                var list = GitHubApiClient.ParseAssets(doc);
                if (list.Count > 0) return list;
            }
        }
        catch (Exception e)
        {
            Log?.Invoke($"Release API 失败（{e.GetType().Name}），改用页面抓取");
        }

        return await LoadAssetsFromHtmlAsync(target.Owner, target.Repo, tag ?? "latest", pool, ct)
            .ConfigureAwait(false);
    }

    [GeneratedRegex(@"href=""(/[^""]+/releases/download/[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex AssetHref();

    [GeneratedRegex(@"(\d+(?:\.\d+)?)\s*(Bytes|KB|MB|GB|TB)\b")]
    private static partial Regex SizePattern();

    private async Task<List<ReleaseAsset>> LoadAssetsFromHtmlAsync(string owner, string repo,
        string tag, MirrorPool pool, CancellationToken ct)
    {
        string page = $"https://github.com/{owner}/{repo}/releases/expanded_assets/{tag}";

        // 页面也可经镜像；直连失败时换镜像
        var candidates = new List<string> { page };
        foreach (var (_, mirrorUrl) in await pool.OrderedUrlsAsync(page, false, null, ct)
                     .ConfigureAwait(false))
        {
            if (!candidates.Contains(mirrorUrl)) candidates.Add(mirrorUrl);
        }

        foreach (var url in candidates)
        {
            try
            {
                using var client = _provider.CreateClient();
                using var resp = await client.GetAsync(url, ct).ConfigureAwait(false);
                if (resp.StatusCode != HttpStatusCode.OK) continue;

                string html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!html.Contains("/releases/download/", StringComparison.Ordinal)) continue;

                var list = new List<ReleaseAsset>();
                var seen = new HashSet<string>();

                foreach (Match m in AssetHref().Matches(html))
                {
                    string href = WebUtility.HtmlDecode(m.Groups[1].Value);
                    string name = href.Split('/')[^1];
                    if (!seen.Add(name)) continue;

                    // 大小在链接后面的兄弟节点里，取该匹配位置之后的一小段文本找
                    string tail = html.Substring(m.Index, Math.Min(600, html.Length - m.Index));
                    long size = ParseSize(tail);

                    list.Add(new ReleaseAsset
                    {
                        Name = name, Size = size, Tag = tag,
                        Url = "https://github.com" + href,
                    });
                }

                if (list.Count > 0)
                {
                    Log?.Invoke($"已从页面抓取 {list.Count} 个资产（API 不可用）");
                    return list;
                }
            }
            catch
            {
                // 试下一个候选
            }
        }
        return new List<ReleaseAsset>();
    }

    private static long ParseSize(string text)
    {
        var m = SizePattern().Match(text);
        if (!m.Success) return 0;
        if (!double.TryParse(m.Groups[1].Value, out double v)) return 0;
        long mult = m.Groups[2].Value switch
        {
            "Bytes" => 1,
            "KB" => 1024,
            "MB" => 1024 * 1024,
            "GB" => 1024L * 1024 * 1024,
            "TB" => 1024L * 1024 * 1024 * 1024,
            _ => 1,
        };
        return (long)(v * mult);
    }
}
