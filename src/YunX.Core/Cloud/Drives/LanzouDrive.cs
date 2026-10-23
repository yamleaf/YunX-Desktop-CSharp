using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YunX.Core.Cloud;

namespace YunX.Core.Cloud.Drives;

/// <summary>
/// 蓝奏云适配器（含蓝奏优享版）。
///
/// <para>移植自原项目 <c>LanzouApi.kt</c>（960 行）。这个平台没有官方 API，
/// 全是网页表单接口，因此有三个必须照抄的坑：</para>
/// <list type="number">
///   <item><b>acw_sc__v2 人机校验</b>：分享页/下载节点可能先返回一段 JS 校验页，
///         里面给出 40 位十六进制 <c>arg1</c>。要按固定位置表重排、与固定掩码逐字节异或，
///         算出 Cookie <c>acw_sc__v2</c> 后重放请求。这是纯字符串变换，不需要 JS 引擎。</item>
///   <item><b>下载节点要 <c>down_ip=1</c> Cookie</b>，缺它下载节点直接拒绝下发直链。</item>
///   <item><b>下载接口域名可能跨域</b>：新版页面把完整地址放在 <c>domain1</c>/<c>domain2</c>
///         JS 变量里（指向 <c>apifile.woozooo.com</c>），不是同源的 <c>ajaxm.php</c>。</item>
/// </list>
///
/// <para><b>链接形态</b>：<c>https://&lt;任意蓝奏域名&gt;/&lt;分享码&gt;</c>。
/// 蓝奏的域名族很杂（lanzou*/lanzo*/lanzx*，后缀 com/net/org/cn，还有 i/l/b/f 等前缀），
/// 所以只能按域名族正则判断，不能写死一个域名。</para>
/// </summary>
public sealed partial class LanzouDrive : ICloudDrive
{
    /// <summary>
    /// 浏览器 UA。个人盘接口与下载节点都要求与网页一致 ——
    /// 非浏览器 UA 很容易被风控判定成爬虫直接拒绝。
    /// </summary>
    private const string WebUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>下载节点注入的 Cookie，缺它下载节点拒绝直链。</summary>
    private const string DownIpCookie = "down_ip=1";

    /// <summary>分页上限，防止接口异常时死循环。</summary>
    private const int MaxFolderPages = 500;

    /// <summary>蓝奏域名族：lanzou*、lanzo*、lanzx*，后缀 com/net/org/cn。</summary>
    
    #if NET48
    // 源生成器在 net48 上不可用，改用惰性初始化的运行时 Regex
    private static Regex? _lanzouHostPatternCache;
    private static Regex LanzouHostPattern() =>
        _lanzouHostPatternCache ??= new Regex(@"^(?:[a-z0-9-]+\.)*(?:lanzou[a-z]?|lan[zs]o[ux]|lanzx[a-z]?)\.(?:com|net|org|cn)$", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    #else
    [GeneratedRegex(@"^(?:[a-z0-9-]+\.)*(?:lanzou[a-z]?|lan[zs]o[ux]|lanzx[a-z]?)\.(?:com|net|org|cn)$",
            RegexOptions.IgnoreCase)]
        private static partial Regex LanzouHostPattern();
    #endif

    
    #if NET48
    // 源生成器在 net48 上不可用，改用惰性初始化的运行时 Regex
    private static Regex? _titlePatternCache;
    private static Regex TitlePattern() =>
        _titlePatternCache ??= new Regex(@"<title>([^<]*)</title>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    #else
    [GeneratedRegex(@"<title>([^<]*)</title>", RegexOptions.IgnoreCase)]
        private static partial Regex TitlePattern();
    #endif

    
    #if NET48
    // 源生成器在 net48 上不可用，改用惰性初始化的运行时 Regex
    private static Regex? _iframePatternCache;
    private static Regex IframePattern() =>
        _iframePatternCache ??= new Regex(@"<iframe[^>]+src=[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    #else
    [GeneratedRegex(@"<iframe[^>]+src=[""']([^""']+)[""']", RegexOptions.IgnoreCase)]
        private static partial Regex IframePattern();
    #endif

    
    #if NET48
    // 源生成器在 net48 上不可用，改用惰性初始化的运行时 Regex
    private static Regex? _ajaxFilePatternCache;
    private static Regex AjaxFilePattern() =>
        _ajaxFilePatternCache ??= new Regex(@"(?:ajaxm|ajaxfile)\.php\?file=(\d+)", RegexOptions.Compiled);
    #else
    [GeneratedRegex(@"(?:ajaxm|ajaxfile)\.php\?file=(\d+)")]
        private static partial Regex AjaxFilePattern();
    #endif

    
    #if NET48
    // 源生成器在 net48 上不可用，改用惰性初始化的运行时 Regex
    private static Regex? _fileInputPatternCache;
    private static Regex FileInputPattern() =>
        _fileInputPatternCache ??= new Regex(@"name=[""']file[""']\s+value=[""'](\d+)[""']", RegexOptions.Compiled);
    #else
    [GeneratedRegex(@"name=[""']file[""']\s+value=[""'](\d+)[""']")]
        private static partial Regex FileInputPattern();
    #endif

    /// <summary>单文件分享的展示大小：「文件大小：1.5 M」。</summary>
    
    #if NET48
    // 源生成器在 net48 上不可用，改用惰性初始化的运行时 Regex
    private static Regex? _displaySizePatternCache;
    private static Regex DisplaySizePattern() =>
        _displaySizePatternCache ??= new Regex(@"(?:文件大小|大小)\s*[:：]\s*([\d.]+)\s*([KMGT]?)(?:i?B)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    #else
    [GeneratedRegex(@"(?:文件大小|大小)\s*[:：]\s*([\d.]+)\s*([KMGT]?)(?:i?B)?",
            RegexOptions.IgnoreCase)]
        private static partial Regex DisplaySizePattern();
    #endif

    
    #if NET48
    // 源生成器在 net48 上不可用，改用惰性初始化的运行时 Regex
    private static Regex? _sizeTokenCache;
    private static Regex SizeToken() =>
        _sizeTokenCache ??= new Regex(@"([\d.]+)\s*([KMGT]?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    #else
    [GeneratedRegex(@"([\d.]+)\s*([KMGT]?)", RegexOptions.IgnoreCase)]
        private static partial Regex SizeToken();
    #endif

    private readonly HttpClient _client;
    private readonly Func<string?> _cookieProvider;

    /// <summary>
    /// 自己维护 Cookie（按 host 分组）。
    /// 不用 <see cref="CookieContainer"/> 是因为要把 <c>acw_sc__v2</c> 精确写到
    /// 「刚刚被挑战的那个 host」上，而自动容器会把 Cookie 按域规则扩散。
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, string>> _cookies = new();

    /// <summary>
    /// 分享会话缓存：规范化 URL → 解析好的分享页。避免重复抓页面。
    ///
    /// <para><b>必须有上限</b>：单页 HTML 上限 2MB，而这个缓存只增不删 ——
    /// 长时间挂着客户端、反复解析不同分享，内存会一路涨上去。</para>
    /// </summary>
    private readonly ConcurrentDictionary<string, LanzouSharePage> _pages = new();

    /// <summary>页面缓存的插入顺序（FIFO 淘汰用）。</summary>
    private readonly ConcurrentQueue<string> _pageOrder = new();

    /// <summary>页面缓存上限。一次解析最多用到 2~3 个页面，64 足够。</summary>
    private const int MaxCachedPages = 64;

    /// <summary>本类自己创建的 handler（仅在未注入专用客户端时非 null）。</summary>
    private readonly HttpMessageHandler? _ownedHandler;

    /// <param name="client">共用客户端。</param>
    /// <param name="cookieProvider">取该平台的已登录 Cookie。</param>
    /// <param name="noRedirectClient">
    /// 禁用自动重定向的客户端（由 <c>HttpClientProvider.CreateClientNoRedirect</c> 提供，
    /// 与共享连接池共用 hosts 绕过）。为 null 时退回本类自建 handler —— 那样
    /// <c>ConnectCallback</c>（hosts 覆盖 / DoH）会失效，蓝奏就不受 hosts 绕过保护了。
    /// </param>
    public LanzouDrive(HttpClient client, Func<string?> cookieProvider,
        HttpClient? noRedirectClient = null)
    {
        _cookieProvider = cookieProvider;

        if (noRedirectClient != null)
        {
            _client = noRedirectClient;
            return;
        }

        // 回退路径：蓝奏需要「手动跟随重定向」（逐跳校验域名，防 SSRF），
        // 自建 handler 能做到，但代价是脱离共享连接池与 hosts 绕过。
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = Net48Constants.DecompressionAll,
        };
        _ownedHandler = handler;
        _client = new HttpClient(handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromSeconds(45),
        };
        _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", WebUa);
    }

    public string Platform => CloudPlatforms.Lanzou;
    public string DisplayName => "蓝奏云";

    public LoginDescriptor? Login => new()
    {
        Kind = LoginKind.Password,
        LoginUrl = "https://pc.woozooo.com/mydisk.php",
        CookieDomains = new[] { "woozooo.com" },
        Hint = "分享链接可直接匿名解析，无需登录；登录后才能访问自己的个人盘",
    };

    // ================= 识别 =================

    public bool CanHandle(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        var url = ShareTextParser.ExtractUrl(input) ?? input;
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsLanzouHost(uri.Host);
    }

    public ShareLink? ParseShareLink(string input)
    {
        var (url, passcode) = ShareTextParser.Parse(input);
        var raw = url ?? ShareTextParser.ExtractUrl(input);
        if (raw == null) return null;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)) return null;
        if (!IsLanzouHost(uri.Host)) return null;

        // 蓝奏的分享码就是路径最后一段（i/b/f/l 开头的一串），没有二级路径语义。
        string shareId = uri.AbsolutePath.Trim('/');
        if (shareId.Length == 0) return null;
        if (shareId.Contains('/')) shareId = shareId[(shareId.LastIndexOf('/') + 1)..];
        if (shareId.Length == 0) return null;

        return new ShareLink
        {
            Platform = Platform,
            ShareId = shareId,
            Passcode = passcode,
            RawInput = input,
        };
    }

    private static bool IsLanzouHost(string host) => LanzouHostPattern().IsMatch(host);

    // ================= 列目录 =================

    public async Task<CloudListing> ListAsync(ShareLink share, string? parentId, string? cursor,
        CancellationToken ct = default)
    {
        var page = await GetSharePageAsync(share, ct).ConfigureAwait(false);

        if (!page.IsFolder)
        {
            // 单文件分享：列表就一项
            var single = new CloudFile
            {
                Id = page.ShareId,
                Name = page.Title,
                Size = page.SingleSize,
                IsDirectory = false,
                ParentId = "",
            };
            return new CloudListing
            {
                Title = page.Title,
                Files = new List<CloudFile> { single },
            };
        }

        if (page.FolderLx == null || page.FolderT == null || page.FolderK == null)
        {
            // 文件夹页但抽不到分页参数：通常是提取码缺失导致页面不是预期的那个
            throw new PasscodeRequiredException(
                page.NeedsPwd ? "该蓝奏分享需要提取码，请补上后重新解析" : "蓝奏文件夹页面结构异常，请重新解析");
        }

        var files = new List<CloudFile>();
        var seen = new HashSet<string>();
        int pg = 1;

        while (pg <= MaxFolderPages)
        {
            JsonNode? body = null;
            int attempt = 0;
            while (attempt <= 2)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    body = await FormPostAsync(
                        $"{page.BaseUrl}/filemoreajax.php",
                        new Dictionary<string, string>
                        {
                            ["lx"] = page.FolderLx,
                            ["fid"] = page.FolderFid ?? "",
                            ["t"] = page.FolderT,
                            ["k"] = page.FolderK,
                            ["pwd"] = share.Passcode ?? "",
                            ["pg"] = pg.ToString(),
                        },
                        referer: page.ShareUrl, origin: page.BaseUrl, ct).ConfigureAwait(false);
                    break;
                }
                catch (Exception) when (attempt < 2)
                {
                    attempt++;
                    await Task.Delay(300, ct).ConfigureAwait(false);
                }
            }

            if (body == null) throw new CloudException("蓝奏请求过于频繁，请稍后再试");

            int zt = (int?)body["zt"] ?? 0;
            switch (zt)
            {
                case 1:
                    break;
                case 2:
                    // zt==2 表示没有更多页了
                    return new CloudListing { Title = page.Title, Files = files };
                case 3:
                    throw new PasscodeRequiredException("蓝奏提取码错误，请检查后重新解析");
                case 4:
                    // 页码越界/被限流：退一页重试一次
                    if (attempt >= 2) throw new CloudException("蓝奏请求过于频繁，请稍后再试");
                    pg--;
                    pg++;
                    break;
                default:
                    throw new CloudException("蓝奏解析失败，请确认分享链接和提取码后重试");
            }

            var arr = body["text"] as JsonArray;
            if (arr == null || arr.Count == 0) break;

            int added = 0;
            foreach (var node in arr)
            {
                if (node is not JsonObject item) continue;
                string id = Str(item, "id");
                if (id.Length == 0 || !seen.Add(id)) continue;

                files.Add(new CloudFile
                {
                    Id = id,
                    Name = FirstNonBlank(Str(item, "name_all"), Str(item, "name")),
                    Size = ParseDisplaySize(Str(item, "size")),
                    IsDirectory = false,
                    ParentId = "",
                    ModifyTime = Str(item, "time"),
                });
                added++;
            }

            if (added == 0) throw new CloudException("蓝奏分页重复，请重新打开目录");
            pg++;
        }

        return new CloudListing { Title = page.Title, Files = files };
    }

    // ================= 取直链 =================

    public async Task<DirectLink> GetDirectLinkAsync(ShareLink share, CloudFile file,
        CancellationToken ct = default)
    {
        var page = await GetSharePageAsync(share, ct).ConfigureAwait(false);
        string origin = page.BaseUrl;

        // 同源 iframe 才是下载页；跨域 iframe 说明页面被改过，直接拒掉
        string html = page.Html;
        if (page.IframeUrl != null)
        {
            if (Uri.TryCreate(page.IframeUrl, UriKind.Absolute, out var iframeUri))
            {
                var shareHost = Uri.TryCreate(page.ShareUrl, UriKind.Absolute, out var su) ? su.Host : "";
                if (!iframeUri.Host.Equals(shareHost, StringComparison.OrdinalIgnoreCase))
                    throw new CloudException("蓝奏下载页面地址异常，请检查分享链接");
                html = await FetchPageAsync(page.IframeUrl, page.ShareUrl, null, ct).ConfigureAwait(false);
            }
        }

        string? fileId = page.FileId;
        if (fileId == null)
        {
            var m1 = AjaxFilePattern().Match(html);
            if (m1.Success) fileId = m1.Groups[1].Value;
        }
        if (fileId == null)
        {
            var m2 = Regex.Match(html, @"file=(\d+)");
            if (m2.Success) fileId = m2.Groups[1].Value;
        }
        if (string.IsNullOrEmpty(fileId))
            throw new CloudException("蓝奏解析失败，请确认分享链接和提取码后重试");

        bool withPwd = !string.IsNullOrEmpty(share.Passcode);
        string ajaxUrl = ResolveAjaxUrl(html, origin, fileId, withPwd);

        // 新版页面签名变量叫 wp_sign，旧版叫 sign
        string? sign = JsVar(html, "wp_sign") ?? JsVar(html, "sign") ?? InputValue(html, "sign");
        if (string.IsNullOrEmpty(sign))
            throw new CloudException("蓝奏分享页缺少下载参数，请重新解析");

        var fields = new Dictionary<string, string> { ["action"] = "downprocess", ["sign"] = sign };

        if (withPwd)
        {
            fields["p"] = share.Passcode!;
            fields["kd"] = "1";
        }
        else
        {
            // 新版无密码协议：websignkey/signs 用 ajaxdata，websign 留空，kd 用 kdns
            string key = JsVar(html, "ajaxdata") ?? JsVar(html, "websignkey")
                         ?? InputValue(html, "websignkey") ?? "";
            fields["signs"] = JsVar(html, "signs") ?? key;
            fields["websignkey"] = key;
            fields["websign"] = JsVar(html, "websign") ?? "";
            fields["kd"] = JsVarRaw(html, "kdns") ?? "1";
            fields["ves"] = "1";
        }

        var json = await FormPostAsync(ajaxUrl, fields, page.ShareUrl, origin, ct).ConfigureAwait(false)
                   ?? throw new CloudException("蓝奏解析失败，请确认分享链接和提取码后重试");
        ValidateAjax(json, out string dom, out string rel);

        if (!Uri.TryCreate(dom, UriKind.Absolute, out var domOrigin))
            throw new CloudException("蓝奏解析失败，请确认分享链接和提取码后重试");

        // 下载节点地址形如 https://<节点>/file/<rel>，rel 本身不是 URL
        string jump = $"{domOrigin.Scheme}://{domOrigin.Host}/file/{rel}";
        var (finalUrl, size) = await ProbeAndVerifyAsync(jump, origin, ct).ConfigureAwait(false);

        string fileName = FirstNonBlank(file.Name, Str(json, "inf"));

        return new DirectLink
        {
            Url = finalUrl,
            FileName = fileName,
            Size = size,
            Headers = new Dictionary<string, string>
            {
                // 蓝奏下载节点校验 UA + Referer，且必须带 down_ip=1
                ["User-Agent"] = WebUa,
                ["Referer"] = page.ShareUrl,
                ["Cookie"] = DownIpCookie,
            },
        };
    }

    /// <summary>蓝奏分享不需要转存，没有临时文件要清。</summary>
    public Task CleanupAsync(ShareLink share, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>写入页面缓存并按 FIFO 维持上限。</summary>
    private void CachePage(string key, LanzouSharePage page)
    {
        _pages[key] = page;
        _pageOrder.Enqueue(key);

        while (_pages.Count > MaxCachedPages && _pageOrder.TryDequeue(out var old))
            _pages.TryRemove(old, out _);      // 同一个 key 可能被更新的值覆盖过，TryRemove 是安全的
    }

    /// <summary>释放自建的 handler（注入了专用客户端时无事可做）。</summary>
    public void Dispose() => _ownedHandler?.Dispose();

    // ================= 分享页解析 =================

    private async Task<LanzouSharePage> GetSharePageAsync(ShareLink share, CancellationToken ct)
    {
        string normalized = NormalizeShareUrl(share.RawInput, share.ShareId);
        string cacheKey = $"{normalized}|{share.Passcode}";
        if (_pages.TryGetValue(cacheKey, out var cached)) return cached;

        string html = await FetchPageFollowingAsync(normalized, ct).ConfigureAwait(false);

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
            throw new CloudException("蓝奏分享链接无效");

        var page = new LanzouSharePage
        {
            ShareUrl = normalized,
            BaseUrl = $"{uri.Scheme}://{uri.Host}",
            ShareId = share.ShareId,
            Html = html,
            Title = ExtractTitle(html),
        };

        page.NeedsPwd = html.Contains("请输入密码") || html.Contains("id=\"pwbox\"") ||
                        html.Contains("input_password") || html.Contains("密码不正确");

        page.IsFolder = html.Contains("filemoreajax") || html.Contains("class=\"filemore\"") ||
                        html.Contains("file_more");

        var iframe = IframePattern().Match(html);
        if (iframe.Success) page.IframeUrl = Absolutize(page.BaseUrl, iframe.Groups[1].Value);

        page.FileId = AjaxFilePattern().Match(html) is { Success: true } fa
            ? fa.Groups[1].Value
            : FileInputPattern().Match(html) is { Success: true } fi
                ? fi.Groups[1].Value
                : Regex.Match(html, @"file=(\d+)") is { Success: true } ff ? ff.Groups[1].Value : null;

        if (page.IsFolder)
        {
            string? lx = JsVar(html, "lx");
            string? fid = JsVar(html, "fid") ?? uri.AbsolutePath.Trim('/');
            string? t = JsVar(html, "t");
            string? k = JsVar(html, "k");
            if (lx != null && t != null && k != null)
            {
                page.FolderLx = lx;
                page.FolderFid = fid;
                page.FolderT = t;
                page.FolderK = k;
            }
        }
        else
        {
            var dm = DisplaySizePattern().Match(html);
            page.SingleSize = dm.Success ? ParseDisplaySize($"{dm.Groups[1].Value} {dm.Groups[2].Value}") : 0;
        }

        if (page.Title.Length == 0) page.Title = "蓝奏分享";
        CachePage(cacheKey, page);
        return page;
    }

    /// <summary>把输入规范成可请求的分享页地址（补 scheme、去 fragment、去尾斜杠）。</summary>
    private static string NormalizeShareUrl(string rawInput, string shareId)
    {
        var url = ShareTextParser.ExtractUrl(rawInput);
        if (url == null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            // 用户可能只贴了分享码
            return $"https://pan.lanzoui.com/{shareId}";
        }

        string scheme = uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ? "https" : uri.Scheme;
        return $"{scheme}://{uri.Host}{uri.AbsolutePath.TrimEnd('/')}";
    }

    private static string ExtractTitle(string html)
    {
        var m = TitlePattern().Match(html);
        if (m.Success)
        {
            string t = m.Groups[1].Value
                .Replace(" - 蓝奏云", "")
                .Replace("蓝奏云", "")
                .Trim();
            if (t.Length > 0 && !t.Contains("密码")) return t;
        }

        var n = Regex.Match(html, @"class=[""']n[""']>([^<]+)<");
        if (n.Success) return n.Groups[1].Value.Trim();

        var f = Regex.Match(html, @"var\s+filename\s*=\s*['""]([^'""]+)['""]");
        return f.Success ? f.Groups[1].Value.Trim() : "";
    }

    // ================= HTTP 基础 =================

    /// <summary>跟随重定向抓页面，但每一跳都必须在蓝奏域名内。</summary>
    private async Task<string> FetchPageFollowingAsync(string url, CancellationToken ct)
    {
        string current = url;
        var challenged = new HashSet<string>();

        for (int hop = 0; hop < 8; hop++)
        {
            ct.ThrowIfCancellationRequested();
            string host = HostOf(current);

            using var req = new HttpRequestMessage(HttpMethod.Get, current);
            req.Headers.TryAddWithoutValidation("Referer", current);
            string ck = CookieHeader(host);
            if (ck.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", ck);

            using var resp = await _client
                .SendAsync(req, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);

            if ((int)resp.StatusCode is >= 300 and <= 399)
            {
                var loc = resp.Headers.Location
                          ?? throw new CloudException("蓝奏页面跳转次数过多，请重新解析");
                string next = Absolutize(current, loc.ToString());
                var nextUri = Uri.TryCreate(next, UriKind.Absolute, out var nu) ? nu : null;
                if (nextUri == null || !IsLanzouHost(nextUri.Host))
                    throw new CloudException("蓝奏分享链接无效");
                current = next;
                continue;
            }

            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (body.Length > 2_000_000) throw new CloudException("蓝奏分享页面过大或异常");

            // 先看是不是人机校验页：能算出 Cookie 就重放
            string? challenge = ChallengeCookie(body);
            if (challenge != null)
            {
                if (!challenged.Add(host))
                    throw new CloudException("蓝奏需要进一步验证，请在分享页完成验证后重试");
                SetCookie(host, "acw_sc__v2", challenge);
                continue;
            }

            if (body.Contains("文件已取消") || body.Contains("不存在"))
                throw new CloudException("蓝奏文件已取消分享或不存在");
            if (body.Contains("请求过于频繁") || body.Contains("稍后再试"))
                throw new CloudException("蓝奏请求过于频繁，请稍后再试");

            return body;
        }

        throw new CloudException("蓝奏页面跳转次数过多，请重新解析");
    }

    private async Task<string> FetchPageAsync(string url, string? referer, string? cookie,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Referer", referer ?? url);
        string ck = MergeCookie(HostOf(url), cookie);
        if (ck.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", ck);

        using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new CloudException("蓝奏文件已取消分享或不存在");
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private async Task<JsonNode?> FormPostAsync(string url, Dictionary<string, string> fields,
        string? referer, string? origin, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new FormUrlEncodedContent(fields),
        };
        if (referer != null) req.Headers.TryAddWithoutValidation("Referer", referer);
        if (origin != null) req.Headers.TryAddWithoutValidation("Origin", origin);

        string ck = CookieHeader(HostOf(url));
        if (ck.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", ck);

        using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);

        if ((int)resp.StatusCode == 429)
            throw new CloudException("蓝奏请求过于频繁，请稍后再试");

        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (text.Length > 4_000_000)
            throw new CloudException("蓝奏解析服务返回内容过大，请稍后重试");
        if (!resp.IsSuccessStatusCode && text.Trim().Length == 0)
            throw new CloudException($"蓝奏解析服务响应异常（HTTP {(int)resp.StatusCode}），请稍后重试");

        return TryParseJson(text);
    }

    private static JsonNode? TryParseJson(string text)
    {
        try
        {
            return JsonNode.Parse(text);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 校验 downprocess 的响应。
    /// zt==1 才算成功；其余根据 inf 文案区分「提取码错 / 已失效 / 太频繁」。
    /// </summary>
    private static void ValidateAjax(JsonNode json, out string dom, out string rel)
    {
        dom = Str(json, "dom");
        rel = Str(json, "url");

        if ((int?)json["zt"] == 1 && dom.Length > 0 && rel.Length > 0 &&
            !rel.StartsWith('/') && !rel.Contains("://"))
        {
            return;
        }

        string inf = Str(json, "inf");
        if (inf.Contains("密码") || inf.Contains("提取码"))
            throw new PasscodeRequiredException("蓝奏提取码错误，请检查后重新解析");
        if (inf.Contains("不存在") || inf.Contains("取消") || inf.Contains("过期") || inf.Contains("删除"))
            throw new CloudException("蓝奏文件已取消分享或不存在");
        if (inf.Contains("频繁") || inf.Contains("次数") || inf.Contains("稍后"))
            throw new CloudException("蓝奏请求过于频繁，请稍后再试");
        throw new CloudException("蓝奏解析失败，请确认分享链接和提取码后重试");
    }

    /// <summary>
    /// HEAD 探测最终下载地址，顺便拿到真实大小。
    ///
    /// <para>不能只看一次响应：下载节点可能先返回人机校验页或「验证并下载」确认页，
    /// 需要算出 Cookie / 提交确认后重放。这也是原项目踩了很多次才跑通的路径。</para>
    /// </summary>
    private async Task<(string Url, long Size)> ProbeAndVerifyAsync(string jump, string shareOrigin,
        CancellationToken ct)
    {
        string url = jump;
        // 下载节点要求注入 down_ip=1（对每个跳转后的 host 都要设一次）
        SetCookie(HostOf(jump), "down_ip", "1");

        var confirmedHosts = new HashSet<string>();
        var challenges = new HashSet<string>();
        var visited = new HashSet<string>();

        for (int hop = 0; hop < 8; hop++)
        {
            ct.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host.Length == 0)
                throw new CloudException("蓝奏下载地址暂时不可用，请稍后重试");
            if (!visited.Add(url))
                throw new CloudException("蓝奏下载地址循环跳转，请重新解析");

            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            req.Headers.TryAddWithoutValidation("Referer", shareOrigin);
            string ck = MergeCookie(uri.Host, DownIpCookie);
            if (ck.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", ck);

            using var resp = await _client
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            int code = (int)resp.StatusCode;

            if (code is >= 300 and <= 399)
            {
                var loc = resp.Headers.Location
                          ?? throw new CloudException("蓝奏下载地址暂时不可用，请稍后重试");
                url = Absolutize($"{uri.Scheme}://{uri.Host}", loc.ToString());
                continue;
            }

            if (code == 200 || code == 206)
            {
                long size = resp.Content.Headers.ContentLength ?? 0;
                string ctype = resp.Content.Headers.ContentType?.MediaType ?? "";
                bool isHtml = ctype.Contains("text/html", StringComparison.OrdinalIgnoreCase);

                if (isHtml)
                {
                    string body = await HttpGetTextAsync(url, shareOrigin, ct).ConfigureAwait(false);

                    string? challenge = ChallengeCookie(body);
                    if (challenge != null)
                    {
                        if (!challenges.Add(uri.Host))
                            throw new CloudException("蓝奏需要进一步验证，请在分享页完成验证后重试");
                        SetCookie(uri.Host, "acw_sc__v2", challenge);
                        continue;
                    }

                    if (body.Contains("验证并下载"))
                    {
                        if (confirmedHosts.Add(uri.Host))
                        {
                            string? verified = await SubmitVerifyAsync(url, body, shareOrigin, ct)
                                .ConfigureAwait(false);
                            if (verified != null)
                            {
                                url = verified;
                                continue;
                            }
                        }
                        throw new CloudException("蓝奏需要进一步验证，请在分享页完成验证后重试");
                    }

                    throw new CloudException("蓝奏返回的是验证页面，未创建下载任务，请在分享页完成验证后重试");
                }

                // HEAD 没给大小：回退 GET + Range 探测
                if (size <= 0)
                {
                    var (_, probed) = await RangeProbeAsync(url, shareOrigin, ct).ConfigureAwait(false);
                    if (probed > 0) return (url, probed);
                }

                return (url, size);
            }

            if (code is 403 or 404 or 410)
                throw new CloudException("蓝奏下载地址已失效，请稍后重新解析");
            if (code == 429)
                throw new CloudException("蓝奏下载请求过于频繁，请稍后再试");

            var (_, fallbackSize) = await RangeProbeAsync(url, shareOrigin, ct).ConfigureAwait(false);
            if (fallbackSize > 0) return (url, fallbackSize);
            throw new CloudException("蓝奏下载地址暂时不可用，请稍后重试");
        }

        throw new CloudException("蓝奏下载地址暂时不可用，请稍后重试");
    }

    /// <summary>
    /// 提交「验证并下载」确认页。
    /// 新版把 file/el/sign 放在一段 <c>data : {...}</c> 的 JS 对象里（值是 base64）。
    /// </summary>
    private async Task<string?> SubmitVerifyAsync(string nodeUrl, string html, string shareOrigin,
        CancellationToken ct)
    {
        if (!Uri.TryCreate(nodeUrl, UriKind.Absolute, out var uri)) return null;

        var dataBlocks = Regex.Matches(html, @"data\s*:\s*\{([^{}]*(?:\{[^{}]*\}[^{}]*)*)\}",
                RegexOptions.Singleline)
            // net48 的 MatchCollection 只实现 ICollection（非泛型），不能直接 .Select；
            // Cast<Match>() 后才能用 LINQ（.NET Framework 的这一处历史遗留）。
            .Cast<Match>()
            .Select(m => m.Groups[1].Value);
        string? block = dataBlocks.FirstOrDefault(b => b.Contains("'file'") || b.Contains("\"file\""));

        string? file = block != null
            ? Regex.Match(block, @"['""]?file['""]?\s*:\s*['""]([^'""]+)['""]") is { Success: true } fm
                ? fm.Groups[1].Value
                : null
            : null;
        file ??= Regex.Match(html, @"name=[""']file[""'][^>]*value=[""']([^""']+)[""']") is
            { Success: true } fm2
            ? fm2.Groups[1].Value
            : null;
        if (file == null) return null;

        string? sign = block != null
            ? Regex.Match(block, @"['""]?sign['""]?\s*:\s*['""]([^'""]+)['""]") is { Success: true } sm
                ? sm.Groups[1].Value
                : null
            : null;

        var fields = new Dictionary<string, string> { ["file"] = file };
        if (sign != null) fields["sign"] = sign;

        using var req = new HttpRequestMessage(HttpMethod.Post, nodeUrl)
        {
            Content = new FormUrlEncodedContent(fields),
        };
        req.Headers.TryAddWithoutValidation("Referer", nodeUrl);
        string ck = MergeCookie(uri.Host, DownIpCookie);
        if (ck.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", ck);

        using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
        // 确认成功后节点会 302 到真实文件
        if ((int)resp.StatusCode is >= 300 and <= 399 && resp.Headers.Location != null)
            return Absolutize($"{uri.Scheme}://{uri.Host}", resp.Headers.Location.ToString());
        return null;
    }

    private async Task<string> HttpGetTextAsync(string url, string? referer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (referer != null) req.Headers.TryAddWithoutValidation("Referer", referer);
        string ck = MergeCookie(HostOf(url), DownIpCookie);
        if (ck.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", ck);

        using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
        return await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>GET + Range 探测大小（从 Content-Range 的 total 拿）。</summary>
    private async Task<(string Url, long Size)> RangeProbeAsync(string url, string? referer,
        CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("Range", "bytes=0-0");
            if (referer != null) req.Headers.TryAddWithoutValidation("Referer", referer);
            string ck = MergeCookie(HostOf(url), DownIpCookie);
            if (ck.Length > 0) req.Headers.TryAddWithoutValidation("Cookie", ck);

            using var resp = await _client
                .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            // Content-Range: bytes 0-0/12345
            var cr = resp.Content.Headers.ContentRange;
            if (cr?.Length is > 0) return (url, cr.Length.Value);

            if ((int)resp.StatusCode == 200)
            {
                long len = resp.Content.Headers.ContentLength ?? 0;
                if (len > 0) return (url, len);
            }
        }
        catch
        {
            // 探测失败不影响主流程
        }

        return (url, 0);
    }

    // ================= 小工具 =================

    /// <summary>
    /// 下载接口地址：优先页面里的 domain1/domain2（新版在 apifile.woozooo.com，跨域），
    /// 其次页面中出现的绝对 ajaxm/ajaxfile 地址，最后回退同源。
    /// </summary>
    private static string ResolveAjaxUrl(string html, string origin, string fileId, bool withPwd)
    {
        string? candidate = JsVar(html, "domain1") is { } d1 && d1.Contains("/ajax") ? d1 : null;
        candidate ??= JsVar(html, "domain2") is { } d2 && d2.Contains("/ajax") ? d2 : null;
        candidate ??= Regex.Match(html, @"https?://[^'""\s]+/(?:ajaxm|ajaxfile)\.php\?file=\d+") is
            { Success: true } m
            ? m.Value
            : null;

        if (!string.IsNullOrEmpty(candidate)) return candidate;
        return withPwd ? $"{origin}/ajaxfile.php?file={fileId}" : $"{origin}/ajaxm.php?file={fileId}";
    }

    /// <summary>读形如 <c>var x = 'value'</c> 的 JS 变量。</summary>
    private static string? JsVar(string html, string name) =>
        Regex.Match(html, $@"(?:(?:var|let|const)\s+)?{name}\s*=\s*['""]([^'""]*)['""]") is
            { Success: true } m
            ? m.Groups[1].Value
            : null;

    /// <summary>读无引号的 JS 变量值（如 <c>var kdns = 0</c>）。</summary>
    private static string? JsVarRaw(string html, string name) =>
        Regex.Match(html, $@"(?:var|let|const)\s+{name}\s*=\s*([^;,\n\r]+)") is { Success: true } m
            ? m.Groups[1].Value.Trim().Trim('\'', '"')
            : null;

    private static string? InputValue(string html, string name) =>
        Regex.Match(html, $@"<input[^>]*name=[""']{name}[""'][^>]*value=[""']([^""']*)[""']") is
            { Success: true } m
            ? m.Groups[1].Value
            : Regex.Match(html, $@"<input[^>]*value=[""']([^""']*)[""'][^>]*name=[""']{name}[""']") is
                { Success: true } m2
                ? m2.Groups[1].Value
                : null;

    private static string Absolutize(string baseUrl, string target)
    {
        if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return target;

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)) return target;
        string root = $"{uri.Scheme}://{uri.Host}";
        return target.StartsWith('/') ? root + target : $"{root}/{target}";
    }

    private static long ParseDisplaySize(string text)
    {
        var t = text.Trim();
        if (t.Length == 0) return 0;
        var m = SizeToken().Match(t);
        if (!m.Success) return 0;

        if (!double.TryParse(m.Groups[1].Value, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double value))
            return 0;

        long factor = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "K" => 1024L,
            "M" => 1024L * 1024,
            "G" => 1024L * 1024 * 1024,
            "T" => 1024L * 1024 * 1024 * 1024,
            _ => 1L,
        };
        return (long)(value * factor);
    }

    private static string Str(JsonNode? node, string key)
    {
        var v = node?[key];
        if (v == null) return "";
        try
        {
            return v.GetValueKind() == System.Text.Json.JsonValueKind.String
                ? v.GetValue<string>()
                : v.ToString();
        }
        catch
        {
            return v.ToString();
        }
    }

    private static string FirstNonBlank(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? "";

    private static string HostOf(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "";

    // ================= Cookie / acw 校验 =================

    private void SetCookie(string host, string name, string value)
    {
        if (host.Length == 0) return;
        var jar = _cookies.GetOrAdd(host, _ => new ConcurrentDictionary<string, string>());
        jar[name] = value;
    }

    private string CookieHeader(string host)
    {
        if (host.Length == 0) return "";
        return _cookies.TryGetValue(host, out var jar) && jar.Count > 0
            ? string.Join("; ", jar.Select(kv => $"{kv.Key}={kv.Value}"))
            : "";
    }

    /// <summary>把自动维护的 Cookie 与调用方额外指定的 Cookie 合并。</summary>
    private string MergeCookie(string host, string? extra)
    {
        string own = CookieHeader(host);
        if (string.IsNullOrWhiteSpace(extra)) return own;
        return own.Length == 0 ? extra : $"{own}; {extra}";
    }

    /// <summary>
    /// 页面若是 acw_sc__v2 校验页，返回应设置的 Cookie 值；否则 null。
    ///
    /// <para>这是与阿里云 WAF 同款的固定字符串变换：把 <c>arg1</c>（40 位 hex）
    /// 按位置表重排后与固定掩码逐字节异或，得到 20 字节 hex。</para>
    /// </summary>
    private static string? ChallengeCookie(string source)
    {
        var m = Regex.Match(source, @"\barg1\s*=\s*'([0-9A-Fa-f]{40})'");
        if (!m.Success) return null;
        return AcwScV2(m.Groups[1].Value);
    }

    /// <summary>重排位置表：把 arg1 的第 pos[z] 个字符放到第 z 位（1-based）。</summary>
    private static readonly int[] AcwPos =
    {
        15, 35, 29, 24, 33, 16, 1, 38, 10, 9, 19, 31, 40, 27, 22, 23, 25, 13, 6, 11,
        39, 18, 20, 8, 14, 21, 32, 26, 2, 30, 7, 4, 17, 5, 3, 28, 34, 37, 12, 36,
    };

    /// <summary>异或掩码（40 位十六进制 = 20 字节）。</summary>
    private const string AcwMask = "3000176000856006061501533003690027800375";

    public static string AcwScV2(string arg1)
    {
        var q = new char[AcwPos.Length];
        for (int x = 0; x < arg1.Length; x++)
        {
            for (int z = 0; z < AcwPos.Length; z++)
            {
                if (AcwPos[z] == x + 1) q[z] = arg1[x];
            }
        }

        string u = new(q);
        var sb = new StringBuilder();
        for (int i = 0; i + 2 <= u.Length && i + 2 <= AcwMask.Length; i += 2)
        {
            int a = Convert.ToInt32(u.Substring(i, 2), 16);
            int b = Convert.ToInt32(AcwMask.Substring(i, 2), 16);
            sb.Append((a ^ b).ToString("x2"));
        }
        return sb.ToString();
    }
}

/// <summary>解析好的蓝奏分享页快照。</summary>
internal sealed class LanzouSharePage
{
    public required string ShareUrl { get; init; }
    public required string BaseUrl { get; init; }
    public required string ShareId { get; init; }
    public required string Html { get; init; }
    public string Title { get; set; } = "";
    public bool NeedsPwd { get; set; }
    public bool IsFolder { get; set; }
    public string? IframeUrl { get; set; }
    public string? FileId { get; set; }
    /// <summary>单文件分享的展示大小（页面上的「文件大小：1.5 M」）。</summary>
    public long SingleSize { get; set; }

    // 文件夹分页参数（filemoreajax.php 的 lx/fid/t/k）
    public string? FolderLx { get; set; }
    public string? FolderFid { get; set; }
    public string? FolderT { get; set; }
    public string? FolderK { get; set; }
}
