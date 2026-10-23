using System.Text.RegularExpressions;

namespace YunX.GitHub;

/// <summary>
/// GitHub 链接解析：把各种形态的输入归一化为 <see cref="GitHubTarget"/>。
///
/// <para>覆盖面比原 Python 版更完整，并修掉了几处边界问题：</para>
/// <list type="bullet">
///   <item>支持裸域名（<c>github.com/a/b</c>）、SSH（<c>git@github.com:a/b.git</c>）、<c>owner/repo</c> 简写；</item>
///   <item>正确剥离 clone 链接末尾的 <c>.git</c>（只对 owner/repo 形态生效，不误伤文件名）；</item>
///   <item>区分 <c>blob</c>（单文件）与 <c>tree</c>（目录），并给出「目录会打包整仓」的明确提示；</item>
///   <item>识别 <c>releases/download/…</c>、<c>expanded_assets</c>、<c>archive/refs/heads/…</c> 等深链。</item>
/// </list>
/// </summary>
public static partial class GitHubUrlParser
{
    
    #if NET48
    // 源生成器在 net48 上不可用，改用惰性初始化的运行时 Regex
    private static Regex? _shortFormCache;
    private static Regex ShortForm() =>
        _shortFormCache ??= new Regex(@"^([A-Za-z0-9](?:[A-Za-z0-9\-]{0,38}))/([A-Za-z0-9._\-]{1,100})$", RegexOptions.Compiled);
    #else
    [GeneratedRegex(@"^([A-Za-z0-9](?:[A-Za-z0-9\-]{0,38}))/([A-Za-z0-9._\-]{1,100})$")]
        private static partial Regex ShortForm();
    #endif

    
    #if NET48
    // 源生成器在 net48 上不可用，改用惰性初始化的运行时 Regex
    private static Regex? _dotGitSuffixCache;
    private static Regex DotGitSuffix() =>
        _dotGitSuffixCache ??= new Regex(@"^(https://github\.com/[^/]{1,40}/[^/]{1,100})\.git(/?)$", RegexOptions.Compiled);
    #else
    [GeneratedRegex(@"^(https://github\.com/[^/]{1,40}/[^/]{1,100})\.git(/?)$")]
        private static partial Regex DotGitSuffix();
    #endif

    private static readonly string[] GitHubHosts =
    {
        "github.com", "www.github.com", "gist.github.com",
    };

    public static GitHubTarget Parse(string? input)
    {
        string raw = (input ?? string.Empty).Trim().Trim('"', '\'', '<', '>');
        if (raw.Length == 0) throw new ParseException("请输入 GitHub 链接");

        string url = CleanUrl(raw);

        // 1) owner/repo 简写
        if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            var m = ShortForm().Match(url);
            if (m.Success)
            {
                return new GitHubTarget
                {
                    Kind = TargetKind.Repo,
                    Owner = m.Groups[1].Value,
                    Repo = m.Groups[2].Value,
                    RawInput = raw,
                    SuggestedName = m.Groups[2].Value + ".zip",
                };
            }
            throw new ParseException("看不懂这个输入，请粘贴 GitHub 链接（或写 owner/repo）");
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            throw new ParseException("链接格式不正确");

        string host = uri.Host.ToLowerInvariant();
        string rest = uri.AbsolutePath.Trim('/');
        var segs = rest.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

        // 2) raw.githubusercontent.com/{owner}/{repo}/{ref}/{path}
        if (host is "raw.githubusercontent.com" or "raw.gitmirror.com" or "raw.gitmirror.xyz")
        {
            if (segs.Length >= 4)
            {
                string o = segs[0], r = segs[1], rf = segs[2];
                string path = string.Join("/", segs.Skip(3));
                return new GitHubTarget
                {
                    Kind = TargetKind.File, Owner = o, Repo = r, Ref = rf, Path = path,
                    Url = $"https://raw.githubusercontent.com/{o}/{r}/{rf}/{path}",
                    SuggestedName = segs[^1], RawInput = raw,
                };
            }
            throw new ParseException("raw 链接不完整");
        }

        // 3) codeload.github.com/{owner}/{repo}/{zip|tar.gz}/{ref…}
        if (host == "codeload.github.com")
        {
            if (segs.Length >= 5 && segs[2] is "zip" or "tar.gz")
            {
                string rf = StripRefs(string.Join("/", segs.Skip(4)));
                string ext = segs[2];
                return new GitHubTarget
                {
                    Kind = TargetKind.Archive, Owner = segs[0], Repo = segs[1], Ref = rf,
                    Url = url, SuggestedName = $"{segs[1]}-{rf}.{ext}", RawInput = raw,
                };
            }
            throw new ParseException("codeload 链接不完整");
        }

        // 4) Release 资产 CDN 直链
        if (host.StartsWith("objects.") || host.StartsWith("release-assets.") || host.StartsWith("media."))
        {
            string name = segs.Length > 0 ? segs[^1] : "download.bin";
            return new GitHubTarget
            {
                Kind = TargetKind.Asset, Url = url, SuggestedName = name, RawInput = raw,
                Note = "CDN 直链，镜像不一定支持，优先直连",
            };
        }

        // 5) gist
        if (host == "gist.github.com")
        {
            if (segs.Length >= 2)
            {
                return new GitHubTarget
                {
                    Kind = TargetKind.Gist, Owner = segs[0], Repo = segs[1],
                    RawInput = raw, SuggestedName = $"{segs[1]}.zip",
                };
            }
            throw new ParseException("gist 链接不完整");
        }

        if (!GitHubHosts.Contains(host) && !host.EndsWith(".github.com"))
        {
            if (host.Contains("github"))
            {
                return new GitHubTarget
                {
                    Kind = TargetKind.Asset, Url = url,
                    SuggestedName = segs.Length > 0 ? segs[^1] : "download.bin",
                    RawInput = raw,
                };
            }
            throw new ParseException("这不是 GitHub 链接");
        }

        if (segs.Length < 2) throw new ParseException("链接里缺少 owner/repo");

        string owner = segs[0], repo = segs[1];
        var tail = segs.Skip(2).ToList();

        if (tail.Count == 0)
        {
            return new GitHubTarget
            {
                Kind = TargetKind.Repo, Owner = owner, Repo = repo,
                RawInput = raw, SuggestedName = $"{repo}.zip",
            };
        }

        string head = tail[0].ToLowerInvariant();

        // ---- releases ----
        if (head == "releases")
        {
            var sub = tail.Skip(1).ToList();
            if (sub.Count > 0 && sub[0].Equals("tag", StringComparison.OrdinalIgnoreCase) && sub.Count >= 2)
            {
                return new GitHubTarget
                {
                    Kind = TargetKind.Releases, Owner = owner, Repo = repo,
                    Tag = StripRefs(string.Join("/", sub.Skip(1))), RawInput = raw,
                };
            }
            if (sub.Count >= 3 && sub[0].Equals("download", StringComparison.OrdinalIgnoreCase))
            {
                string tag = sub[1];
                string asset = string.Join("/", sub.Skip(2));
                return new GitHubTarget
                {
                    Kind = TargetKind.Asset, Owner = owner, Repo = repo, Tag = tag,
                    AssetName = asset,
                    Url = $"https://github.com/{owner}/{repo}/releases/download/{tag}/{asset}",
                    SuggestedName = asset.Split('/')[^1], RawInput = raw,
                };
            }
            if (sub.Count >= 2 && sub[0].Equals("expanded_assets", StringComparison.OrdinalIgnoreCase))
            {
                return new GitHubTarget
                {
                    Kind = TargetKind.Releases, Owner = owner, Repo = repo,
                    Tag = sub[1], RawInput = raw,
                };
            }
            bool latest = sub.Any(s => s.Equals("latest", StringComparison.OrdinalIgnoreCase));
            return new GitHubTarget
            {
                Kind = TargetKind.Releases, Owner = owner, Repo = repo,
                Tag = latest ? "latest" : null, RawInput = raw,
            };
        }

        // ---- archive/refs/heads|tags/<ref>.zip ----
        if (head == "archive" && tail.Count >= 2)
        {
            string all = string.Join("/", tail);
            string fmt = all.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ? "tar.gz" : "zip";
            string refPart = Regex.Replace(all, @"\.(zip|tar\.gz)$", "",
                RegexOptions.IgnoreCase);
            refPart = string.Join("/", refPart.Split('/').Skip(1));
            string rf = StripRefs(refPart);
            return new GitHubTarget
            {
                Kind = TargetKind.Archive, Owner = owner, Repo = repo, Ref = rf, Url = url,
                SuggestedName = $"{repo}-{rf.Split('/')[^1]}.{fmt}", RawInput = raw,
            };
        }

        // ---- blob / raw / tree / blame ----
        if (head is "blob" or "raw" or "tree" or "blame")
        {
            var (rf, path) = SplitRefPath(string.Join("/", tail.Skip(1)));

            if (head == "tree")
            {
                return new GitHubTarget
                {
                    Kind = TargetKind.Tree, Owner = owner, Repo = repo, Ref = rf, Path = path,
                    RawInput = raw, SuggestedName = $"{repo}-{rf ?? "HEAD"}.zip",
                    Note = string.IsNullOrEmpty(path)
                        ? "" : "目录下载会打包整仓源码（GitHub 不提供单目录包）",
                };
            }

            if (string.IsNullOrEmpty(path)) throw new ParseException("文件链接缺少路径");

            return new GitHubTarget
            {
                Kind = TargetKind.File, Owner = owner, Repo = repo, Ref = rf, Path = path,
                Url = $"https://raw.githubusercontent.com/{owner}/{repo}/{rf ?? "HEAD"}/{path}",
                SuggestedName = path.Split('/')[^1], RawInput = raw,
            };
        }

        // 其它深链（commit / issues / pulls / actions…）一律当整仓
        return new GitHubTarget
        {
            Kind = TargetKind.Repo, Owner = owner, Repo = repo,
            RawInput = raw, SuggestedName = $"{repo}.zip",
        };
    }

    private static string CleanUrl(string s)
    {
        // 去空白、取第一个词
        int sp = s.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
        if (sp > 0) s = s[..sp];

        s = Regex.Replace(s, @"^(git\+)?(https?|ssh)://", "https://", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"^git@github\.com:", "https://github.com/", RegexOptions.IgnoreCase);

        if (s.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("www.github.com/", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("raw.githubusercontent.com/", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("codeload.github.com/", StringComparison.OrdinalIgnoreCase) ||
            s.StartsWith("gist.github.com/", StringComparison.OrdinalIgnoreCase))
        {
            s = "https://" + s;
        }

        // clone 链接末尾的 .git（仅 owner/repo 形态）
        s = DotGitSuffix().Replace(s, "$1$2");
        return s;
    }

    /// <summary>把 refs/heads/main、refs/tags/v1 折叠成 main、v1。</summary>
    private static string StripRefs(string s) =>
        Regex.Replace(s, @"^refs/(heads|tags)/", "", RegexOptions.IgnoreCase);

    /// <summary>
    /// 从 <c>main/src/app.cs</c> 拆出 ref 与 path。ref 可能含斜杠（如 <c>feature/x</c>），
    /// 网页链接里无法可靠区分，故取第一段作 ref —— 与 GitHub 网页行为一致。
    /// </summary>
    private static (string? Ref, string? Path) SplitRefPath(string rest)
    {
        rest = rest.Trim('/');
        if (rest.Length == 0) return (null, null);
        var parts = rest.Split('/');
        if (parts.Length == 1) return (parts[0], null);
        return (parts[0], string.Join("/", parts.Skip(1)));
    }

    /// <summary>构造仓库归档直链。</summary>
    public static string ArchiveUrl(string owner, string repo, string? refName, string fmt = "zip")
    {
        string rf = StripRefs(refName ?? "HEAD");
        if (rf.Length == 0 || rf.Equals("HEAD", StringComparison.OrdinalIgnoreCase))
            return $"https://codeload.github.com/{owner}/{repo}/{fmt}/HEAD";
        return $"https://codeload.github.com/{owner}/{repo}/{fmt}/refs/heads/{rf}";
    }

    /// <summary>构造 raw 直链。</summary>
    public static string RawUrl(string owner, string repo, string? refName, string path)
    {
        string rf = StripRefs(string.IsNullOrEmpty(refName) ? "HEAD" : refName);
        return $"https://raw.githubusercontent.com/{owner}/{repo}/{rf}/{path.TrimStart('/')}";
    }
}
