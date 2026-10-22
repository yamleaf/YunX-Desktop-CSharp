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

    /// <summary>
    /// 阶段进度回调（「正在探测线路…」这类），与 <see cref="Log"/> 分开。
    ///
    /// <para><b>为什么不复用 Log</b>：Log 是给调试面板的流水账（带接口名、耗时、报错），
    /// 阶段文案要直接显示在遮罩上 —— 两者受众不同，混在一起会把内部细节漏给用户，
    /// 也不方便单独改措辞。</para>
    /// </summary>
    public Action<string>? Progress { get; set; }

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

        // ★ 先摸清这个仓库是公开还是私有。
        //
        //   线路选择完全依赖它：私有仓库必须带 Token 直连（镜像不转发 Authorization），
        //   而公开仓库应当走镜像（通常更快，还不占 API 配额）。
        //   这里查一次 repo JSON，成本一次往返，但能让后面所有请求都选对线路。
        //   ★ 只在**配了 Token** 时才值得探。
        //
        //     没有 Token 的请求会撞 API 限流（每 IP 60 次/小时），不仅查不到结果，
        //     还要多等一轮重试 —— 实测这一句能白吃 1~2 秒，而它想得到的结论
        //     （"按公开仓库处理"）本来就是匿名情况下的默认行为。
        //     有 Token 才探：那时查询几乎一定成功，且能真正区分私有仓库。
        if (_api.HasToken
            && !string.IsNullOrEmpty(target.Owner) && !string.IsNullOrEmpty(target.Repo))
        {
            Progress?.Invoke($"正在探测仓库 {target.FullName}…");
            try
            {
                using var repoDoc = await _api.GetRepoAsync(target.Owner, target.Repo, ct)
                    .ConfigureAwait(false);
            }
            catch (Exception e)
            {
                // 查不到不影响主流程：线路判定会退回「可见性未知 → 允许试镜像」
                Log?.Invoke($"查询仓库信息失败（{e.GetType().Name}），线路按公开仓库处理");
            }
        }

        switch (target.Kind)
        {
            case TargetKind.File:
            {
                // ★ 不再为了"默认分支名"去调 API。
                //
                //   HEAD 本身就是合法的 git ref：raw.githubusercontent.com/<o>/<r>/HEAD/...
                //   和 codeload 的 .../zip/HEAD 都能直接用（实测 200）。
                //   而查默认分支要额外一次 api.github.com 请求 —— 匿名每小时只有 60 次，
                //   纯属浪费配额，还会因限流失败（失败后本来就是退回 "HEAD"，绕了一圈回到原点）。
                string refName = target.Ref ?? "";
                if (refName.Length == 0) refName = "HEAD";
                string url = GitHubUrlParser.RawUrl(target.Owner, target.Repo, refName,
                    target.Path ?? "");
                string name = FileNamePolicy.SanitizeFileName(
                    (target.Path ?? "file.bin").Split('/')[^1]);
                return await WithBestMirrorAsync(new ResolvedContent
                {
                    Kind = TargetKind.File,
                    Owner = target.Owner,
                    Repo = target.Repo,
                    GitRef = target.Ref ?? "",
                    Title = $"{target.FullName} · {name}",
                    Note = $"分支 {refName}",
                    Items = { (url, name, 0L) },
                }, pool, ct).ConfigureAwait(false);
            }

            case TargetKind.Repo:
            case TargetKind.Tree:
            {
                // 同上：HEAD 直接可用，不查 API
                string refName = target.Ref ?? "";
                if (refName.Length == 0) refName = "HEAD";
                string url = GitHubUrlParser.ArchiveUrl(target.Owner, target.Repo, refName);
                string name = $"{target.Repo}-{refName}.zip";
                string note = "整仓源码 zip";
                if (target.Kind == TargetKind.Tree && !string.IsNullOrEmpty(target.Path))
                    note = $"目录 {target.Path} → 打包整仓 zip（GitHub 不提供单目录包）";

                return await WithBestMirrorAsync(new ResolvedContent
                {
                    Kind = TargetKind.Repo,
                    Owner = target.Owner,
                    Repo = target.Repo,
                    GitRef = target.Ref ?? "",
                    Title = $"{target.FullName} @ {refName}",
                    Note = note,
                    Items = { (url, name, 0L) },
                }, pool, ct).ConfigureAwait(false);
            }

            case TargetKind.Archive:
            {
                string name = target.SuggestedName ?? "archive.zip";
                if (!name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                    !name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                    name += ".zip";
                return await WithBestMirrorAsync(new ResolvedContent
                {
                    Kind = TargetKind.Archive,
                    Owner = target.Owner,
                    Repo = target.Repo,
                    GitRef = target.Ref ?? "",
                    Title = $"{target.FullName} @ {target.Ref ?? "默认"}",
                    Note = "归档直链",
                    Items = { (target.Url ?? "", name, 0L) },
                }, pool, ct).ConfigureAwait(false);
            }

            case TargetKind.Asset:
            {
                string name = FileNamePolicy.SanitizeFileName(
                    target.SuggestedName ?? target.AssetName?.Split('/')[^1] ?? "download.bin");
                return await WithBestMirrorAsync(new ResolvedContent
                {
                    Kind = TargetKind.Asset,
                    Owner = target.Owner,
                    Repo = target.Repo,
                    GitRef = target.Ref ?? "",
                    Title = $"{target.FullName} · {name}",
                    Note = string.IsNullOrEmpty(target.Tag) ? "Release 资产" : $"Release {target.Tag}",
                    Items = { (target.Url ?? "", name, 0L) },
                }, pool, ct).ConfigureAwait(false);
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
                Progress?.Invoke($"已取到 {assets.Count} 个资产，正在挑选最快线路…");
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

                // 解析阶段就把线路测出来，界面上能直接显示「将走哪条线路」
                string best = await PickBestMirrorAsync(content, pool, ct).ConfigureAwait(false);
                if (best.Length == 0) return content;
                return new ResolvedContent
                {
                    Kind = content.Kind, Owner = content.Owner, Repo = content.Repo,
                    GitRef = content.GitRef, Title = content.Title,
                    Note = content.Note + $" · 线路 {best}",
                    BestMirror = best, Items = content.Items,
                };
            }

            case TargetKind.Gist:
            {
                // ★ 先走页面抓取，不消耗 API 配额。
                //
                //   Gist 页面的 HTML 里就带着 <code>/<owner>/<gistId>/archive/<sha>.zip</code>，
                //   正则一把就能拿到（实测 2 秒内）。这条路径匿名也能用、不计配额。
                //   只有私有 Gist 才必须用 API（页面需要登录）。
                Progress?.Invoke("正在读取 Gist 页面…");
                string? pageZip = await GistZipFromHtmlAsync(target.Owner, target.Repo, ct)
                    .ConfigureAwait(false);
                if (pageZip != null)
                {
                    Log?.Invoke("Gist 地址取自页面");
                    return await WithBestMirrorAsync(new ResolvedContent
                    {
                        Kind = TargetKind.Gist,
                        Owner = target.Owner,
                        Repo = target.Repo,
                        GitRef = target.Ref ?? "",
                        Title = $"Gist {target.Repo}",
                        Note = "Gist 归档",
                        Items = { (pageZip, $"gist-{target.Repo}.zip", 0L) },
                    }, pool, ct).ConfigureAwait(false);
                }

                // 页面拿不到（多半是私有 Gist）才用 API
                using var doc = await _api.GetGistAsync(target.Repo, ct).ConfigureAwait(false);
                if (doc?.RootElement.TryGetProperty("zipball_url", out var zb) == true &&
                    zb.GetString() is { Length: > 0 } zip)
                {
                    int fileCount = 0;
                    if (doc.RootElement.TryGetProperty("files", out var files))
                        fileCount = files.EnumerateObject().Count();

                    return await WithBestMirrorAsync(new ResolvedContent
                    {
                        Kind = TargetKind.Gist,
                        Owner = target.Owner,
                        Repo = target.Repo,
                        GitRef = target.Ref ?? "",
                        Title = $"Gist {target.Repo}",
                        Note = $"{fileCount} 个文件",
                        Items = { (zip, $"gist-{target.Repo}.zip", 0L) },
                    }, pool, ct).ConfigureAwait(false);
                }
                throw new ParseException("无法获取 Gist 下载地址（私有 Gist 需要在设置里填 Token）");
            }

            default:
                throw new ParseException("暂不支持的链接类型");
        }
    }

    /// <summary>
    /// 给解析结果补上「最优线路」。
    ///
    /// <para>线路只是<b>附带信息</b>：测不通、测超时都不影响解析结果本身，
    /// 所以这里不抛异常，失败就返回原样（下载时引擎还会再择优一次）。</para>
    /// </summary>
    private async Task<ResolvedContent> WithBestMirrorAsync(ResolvedContent content,
        MirrorPool pool, CancellationToken ct)
    {
        string best = await PickBestMirrorAsync(content, pool, ct).ConfigureAwait(false);
        if (best.Length == 0) return content;

        return new ResolvedContent
        {
            Kind = content.Kind,
            Owner = content.Owner,
            Repo = content.Repo,
            GitRef = content.GitRef,
            Title = content.Title,
            Note = content.Note.Length == 0 ? $"线路 {best}" : $"{content.Note} · 线路 {best}",
            BestMirror = best,
            Items = content.Items,
        };
    }

    /// <summary>
    /// 在解析阶段就把下载线路定下来。
    ///
    /// <para><b>为什么要单独做一次</b>：解析结果里只给出「原始 GitHub 地址」，
    /// 线路择优原本发生在点下载时（<c>DownloadEngine</c> → <c>MirrorPool</c>）。
    /// 结果就是：用户看到资产列表却完全不知道会走哪条线路，也没机会在下载前
    /// 发现「所有镜像对这个仓库都很慢」。</para>
    ///
    /// <para><b>为什么只在解析时对单个地址测一次</b>：Release 可能有十几个资产，
    /// 每个都测一轮会把解析拖成十几秒。取<b>第一个文件的地址</b>测一次即可 ——
    /// 同一仓库的资产在镜像侧走的是同一个回源路径，线路优劣基本一致。
    /// 下载时引擎仍会按真实地址重新择优。</para>
    /// </summary>
    private async Task<string> PickBestMirrorAsync(ResolvedContent content, MirrorPool pool,
        CancellationToken ct)
    {
        if (content.Items.Count == 0) return "";

        // 用体积最大的那个资产测：小文件（sha256、yaml）几乎瞬间完成，
        // 测出来的速度取决于体积而不是线路，会把排序搞乱。
        var probeItem = content.Items.OrderByDescending(i => i.Size).First();
        if (string.IsNullOrEmpty(probeItem.Url)) return "";

        Progress?.Invoke("正在测速各条线路…");
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // quick = 只判断通不通、比延迟。解析不该为「精确吞吐」买单：
            // 实测对一个 745MB 资产收满 768KB 样本要 8 秒以上，整次解析被拖到 13.6 秒。
            var best = await pool.PickAsync(probeItem.Url, Log, ct).ConfigureAwait(false);
            sw.Stop();

            if (best == null)
            {
                Log?.Invoke($"线路择优：{sw.ElapsedMilliseconds}ms 内没有可用线路");
                return "";
            }

            Log?.Invoke($"线路择优：{best.MirrorName}（延迟 {best.Latency * 1000:0} ms，{sw.ElapsedMilliseconds}ms 内出结果）");
            return best.MirrorName;
        }
        catch (Exception e)
        {
            // 测速失败不能影响解析结果
            Log?.Invoke($"线路择优失败（{e.GetType().Name}），下载时再选");
            return "";
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

        // ★ 页面优先，API 兜底。
        //
        //   这不是"退而求其次"，而是实测更优：
        //   · API 未带 Token 时每 IP 每小时只有 60 次，撞上限就 403，
        //     还得重试一轮，实测要 8.5 秒才失败 —— 用户看到的就是遮罩转圈半天。
        //   · HTML 那条路（expanded_assets）实测 0.5 秒，资产和大小都全，
        //     而且不消耗任何 API 配额。
        //
        //   之前顺序反了（先 API 后 HTML），且 HTML 的 tag 兜底写的是字面量
        //   "latest" —— 而 GitHub 只有 /releases/expanded_assets/<真实tag>，
        //   没有 .../latest 这个页面，于是兜底永远取不到东西。
        Progress?.Invoke("正在读取 Release 页面…");
        string? resolvedTag = tag;

        if (resolvedTag == null)
        {
            // 用 /releases/latest 的 302 目标拿真实 tag（实测 0.5 秒，无需 API）
            resolvedTag = await ResolveLatestTagAsync(target.Owner, target.Repo, ct)
                .ConfigureAwait(false);
        }

        if (!string.IsNullOrEmpty(resolvedTag))
        {
            var fromHtml = await LoadAssetsFromHtmlAsync(target.Owner, target.Repo,
                resolvedTag!, pool, ct).ConfigureAwait(false);
            if (fromHtml.Count > 0)
            {
                Log?.Invoke($"页面抓取成功：{fromHtml.Count} 个资产（tag {resolvedTag}）");
                return fromHtml;
            }
            Log?.Invoke("页面抓取没拿到资产，改用 API");
        }

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
            Log?.Invoke($"Release API 失败（{e.GetType().Name}）");
        }

        return new List<ReleaseAsset>();
    }

    [GeneratedRegex(@"href=""(/[^""]+/releases/download/[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex AssetHref();

    /// <summary>
    /// 资产大小：优先取 GitHub 页面上那个「右对齐的独立 span」，
    /// 兜底再用裸的「数字 + 单位」文本。
    ///
    /// <para><b>为什么不能简单向后截 N 个字符</b>：实测 expanded_assets 页里，
    /// 大小文本位于 href 之后约 <b>2650</b> 字符处（中间隔着 sha256 复制按钮的整段 SVG
    /// 和 aria 文本）。原来只截 600 字符，18 个资产<b>一个都没取到大小</b>，
    /// 界面上就全显示成 “—”。</para>
    /// </summary>
    [GeneratedRegex(@"color-fg-muted text-right[^>]*>\s*([0-9.,]+)\s*(Bytes|KB|MB|GB|TB)\s*<",
        RegexOptions.IgnoreCase)]
    private static partial Regex AssetSizeSpan();

    /// <summary>裸的大小文本（无 class 定位时的兜底，例如页面结构改版）。</summary>
    [GeneratedRegex(@"\b([0-9]+(?:\.[0-9]+)?)\s*(Bytes|KB|MB|GB|TB)\b")]
    private static partial Regex SizePattern();

    /// <summary>
    /// 取仓库最新 Release 的 tag 名。
    ///
    /// <para><b>为什么要这一步</b>：资产列表页是
    /// <c>/releases/expanded_assets/&lt;真实tag&gt;</c>，没有 <c>.../latest</c> 这个页面。
    /// 而 <c>/releases/latest</c> 会 302 到 <c>/releases/tag/&lt;tag&gt;</c> ——
    /// 顺着重定向看最终 URL 就能拿到 tag，实测约 0.5 秒，且不消耗 API 配额。</para>
    ///
    /// <para>失败返回 null，调用方会退回 API。这不是致命错误，所以不打日志吓人。</para>
    /// </summary>
    private async Task<string?> ResolveLatestTagAsync(string owner, string repo,
        CancellationToken ct)
    {
        string url = $"https://github.com/{owner}/{repo}/releases/latest";
        try
        {
            // 同样给短超时：只是看一次 302 跳转，不该等几十秒
            using var client = _provider.CreateClient(timeout: TimeSpan.FromSeconds(8));
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // 只要跳转目标，不要整页内容
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            // HttpClient 默认跟随重定向，所以看 RequestMessage.RequestUri 的最终地址
            var finalUri = resp.RequestMessage?.RequestUri;
            if (finalUri != null)
            {
                var m = LatestTagInPath().Match(finalUri.AbsolutePath);
                if (m.Success) return Uri.UnescapeDataString(m.Groups[1].Value);
            }

            // 没跟随（或禁止了重定向）时，从 Location 头取
            var loc = resp.Headers.Location;
            if (loc != null)
            {
                var m2 = LatestTagInPath().Match(loc.OriginalString);
                if (m2.Success) return Uri.UnescapeDataString(m2.Groups[1].Value);
            }
        }
        catch (Exception e)
        {
            Log?.Invoke($"取最新 tag 失败（{e.GetType().Name}），改走 API");
        }
        return null;
    }

    /// <summary>Gist 页面里那个 <c>.../archive/&lt;sha&gt;.zip</c> 路径。</summary>
    [GeneratedRegex(@"/[^""'/]+/[0-9a-f]{20,}/archive/[0-9a-f]+\.zip", RegexOptions.IgnoreCase)]
    private static partial Regex GistArchivePath();

    /// <summary>
    /// 从 Gist 页面 HTML 里取归档 zip 的绝对地址；取不到返回 null。
    ///
    /// <para>好处是**不消耗 API 配额**（匿名每小时只有 60 次），
    /// 而且公开 Gist 本来就不需要鉴权。</para>
    /// </summary>
    private async Task<string?> GistZipFromHtmlAsync(string owner, string gistId,
        CancellationToken ct)
    {
        string url = $"https://gist.github.com/{owner}/{gistId}";
        try
        {
            using var client = _provider.CreateClient(timeout: TimeSpan.FromSeconds(10));
            using var resp = await client.GetAsync(url, ct).ConfigureAwait(false);
            if (resp.StatusCode != System.Net.HttpStatusCode.OK) return null;

            string html = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var m = GistArchivePath().Match(html);
            return m.Success ? "https://gist.github.com" + m.Value : null;
        }
        catch (Exception e)
        {
            Log?.Invoke($"读 Gist 页面失败（{e.GetType().Name}），改走 API");
            return null;
        }
    }

    /// <summary>从 <c>/owner/repo/releases/tag/&lt;tag&gt;</c> 里抠出 tag。</summary>
    [GeneratedRegex(@"/releases/tag/([^/]+)/?$", RegexOptions.IgnoreCase)]
    private static partial Regex LatestTagInPath();

    /// <summary>单个条目窗口上限：页面每个资产条目实测 4~7.5KB，给足余量。</summary>
    private const int AssetWindow = 8192;

    private async Task<List<ReleaseAsset>> LoadAssetsFromHtmlAsync(string owner, string repo,
        string tag, MirrorPool pool, CancellationToken ct)
    {
        string page = $"https://github.com/{owner}/{repo}/releases/expanded_assets/{tag}";

        // ★ 先直连试一次，只有失败了才去测速 + 走镜像。
        //
        //   OrderedUrlsAsync 内部要对全部线路测速（固定 ~2 秒），而实测直连
        //   github.com 的这个 HTML 页面 0.5~1.5 秒就能拿到（还带重试）。
        //   之前无条件先测速，等于每次解析都白付 2 秒 —— 用户看到的「卡住」有它一份。
        var first = await TryFetchAssetsAsync(page, tag).ConfigureAwait(false);
        if (first != null) return first;

        var candidates = new List<string>();
        foreach (var (_, mirrorUrl) in await pool.OrderedUrlsAsync(page, false, null, ct)
                     .ConfigureAwait(false))
        {
            if (mirrorUrl != page && !candidates.Contains(mirrorUrl)) candidates.Add(mirrorUrl);
        }

        foreach (var url in candidates)
        {
            var got = await TryFetchAssetsAsync(url, tag).ConfigureAwait(false);
            if (got != null) return got;
        }
        return new List<ReleaseAsset>();
    }

    /// <summary>
    /// 抓一个页面并解析出资产列表；拿不到（非 200 / 没链接 / 异常）返回 null。
    ///
    /// <para>抽成独立方法是为了让「直连」和「镜像」两条路径共用同一套解析 ——
    /// 以前解析逻辑嵌在循环里，想在前面插一次直连就得复制一整段。</para>
    /// </summary>
    private async Task<List<ReleaseAsset>?> TryFetchAssetsAsync(string url, string tag)
    {
        {
            try
            {
                // ★ 必须给短超时。默认是 100 秒（为下载准备的），
                //   而这里只是抓一个 HTML 页面：直连不通时若等 100 秒，
                //   解析会长时间「卡住」，用户根本不知道发生了什么。
                //   8 秒足够一个正常页面返回，超了就换镜像。
                using var client = _provider.CreateClient(timeout: TimeSpan.FromSeconds(8));
                using var resp = await client.GetAsync(url).ConfigureAwait(false);
                if (resp.StatusCode != HttpStatusCode.OK) return null;

                string html = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                if (!html.Contains("/releases/download/", StringComparison.Ordinal)) return null;

                var list = new List<ReleaseAsset>();
                var seen = new HashSet<string>();

                // 每个资产条目从自己的 href 起、到下一个 href 止 —— 这样不会串到隔壁资产的
                // 大小上去（朴素「向后截 600 字符」既取不到，也容易取错）。
                var matches = AssetHref().Matches(html);
                for (int i = 0; i < matches.Count; i++)
                {
                    var m = matches[i];
                    string href = WebUtility.HtmlDecode(m.Groups[1].Value);
                    string name = href.Split('/')[^1];
                    if (!seen.Add(name)) continue;

                    int start = m.Index;
                    int end = i + 1 < matches.Count ? matches[i + 1].Index : html.Length;
                    if (end - start > AssetWindow) end = start + AssetWindow;

                    long size = ParseSize(html.Substring(start, end - start));

                    list.Add(new ReleaseAsset
                    {
                        Name = name, Size = size, Tag = tag,
                        Url = "https://github.com" + href,
                    });
                }

                if (list.Count > 0)
                {
                    int withSize = list.Count(a => a.Size > 0);
                    Log?.Invoke(withSize == list.Count
                        ? $"已从页面抓取 {list.Count} 个资产（含大小）"
                        : $"已从页面抓取 {list.Count} 个资产（{withSize} 个取到大小）");
                    return list;
                }
                return null;
            }
            catch
            {
                // 试下一个候选
            }
        }
        return new List<ReleaseAsset>();
    }

    /// <summary>从一段 HTML 里取资产大小；优先用 GitHub 的尺寸 span，兜底用裸文本。</summary>
    private static long ParseSize(string segment)
    {
        var m = AssetSizeSpan().Match(segment);
        if (!m.Success) m = SizePattern().Match(segment);
        if (!m.Success) return 0;

        string num = m.Groups[1].Value.Replace(",", "");
        if (!double.TryParse(num, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double v))
            return 0;

        long mult = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "BYTES" => 1,
            "KB" => 1024,
            "MB" => 1024 * 1024,
            "GB" => 1024L * 1024 * 1024,
            "TB" => 1024L * 1024 * 1024 * 1024,
            _ => 1,
        };
        return (long)(v * mult);
    }
}
