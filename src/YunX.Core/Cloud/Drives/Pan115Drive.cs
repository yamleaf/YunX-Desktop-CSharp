using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YunX.Core.Cloud;

namespace YunX.Core.Cloud.Drives;

/// <summary>
/// 115 网盘适配器。
///
/// <para>移植自原项目 <c>Pan115Api.kt</c>（693 行）。核心链路只有三步：</para>
/// <list type="number">
///   <item><c>share/snap</c>：列分享目录 —— 同时校验提取码、拿分享者 UID；</item>
///   <item><c>share/downurl</c>：用「分享者 UID + file_id」换直链；</item>
///   <item>下载：<b>必须带 Referer + 客户端 UA + 取链响应下发的 900 秒 CDN Cookie</b>。</item>
/// </list>
///
/// <para><b>三个极其容易踩错的点</b>（都是原项目抓包实证）：</para>
/// <list type="bullet">
///   <item><c>user_id</c> 必须是<b>分享者</b>的 UID（在 snap 响应的 <c>data.userinfo.user_id</c> 里），
///         不是自己的 UID；</item>
///   <item>取链要用 115 客户端 UA（<c>Mozilla/5.0 115Browser/xx</c>），
///         用浏览器 UA 会被判「当前版本过低」直接拒发直链（50029）；</item>
///   <item>CDN 同时校验登录 Cookie 与取链下发的 CDN Cookie，缺任一 →
///         <c>403 no cookie value</c>。CDN Cookie 有效期 900 秒且 path 绑定到对象路径，
///         不能跨文件复用。</item>
/// </list>
///
/// <para><b>能力边界</b>：未登录也能列目录、也能取到小文件直链；但 115 对大文件不向
/// 网页端下发直链（50029），必须「登录 + 转存 + 电脑端 RSA 取链」，那部分需要账号，
/// 本实现不覆盖 —— 遇到 50029 会给出明确提示而不是静默失败。</para>
/// </summary>
public sealed partial class Pan115Drive : ICloudDrive
{
    private const string ApiBase = "https://webapi.115.com";
    private const string ShareHost = "https://115cdn.com";
    private const string Referer = "https://115.com/";
    private const string WebUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/127.0.0.0 Safari/537.36";

    /// <summary>取链与下载都要用客户端 UA；先用兜底版本，启动后异步刷新。</summary>
    private const string ClientUaFallback = "36.0.1";
    private const string AppVersionUrl = "https://appversion.115.com/1/web/1.0/api/chrome";

    private const int SharePageLimit = 100;
    private const string RootCid = "0";

    // 错误码
    private const int ErrnoLoginTimeout = 990001;
    private const int ErrnoNeedCode = 4100012;
    private const int ErrnoWrongCode = 4100008;
    private const int ErrnoShareGone = 4100026;
    private const int ErrnoBusy = 990009;
    private const int ErrnoClientTooOld = 50029;

    
    #if NET48
    // 源生成器在 net48 上不可用，改用惰性初始化的运行时 Regex
    private static Regex? _shareIdPatternCache;
    private static Regex ShareIdPattern() =>
        _shareIdPatternCache ??= new Regex(@"115(?:cdn|rc)?\.com/s/(sw[A-Za-z0-9]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    #else
    [GeneratedRegex(@"115(?:cdn|rc)?\.com/s/(sw[A-Za-z0-9]+)", RegexOptions.IgnoreCase)]
        private static partial Regex ShareIdPattern();
    #endif

    /// <summary>口令形态：<c>https://115.com/swxxxxxxxx-1234/</c>（中划线后是提取码，没有 /s/）。</summary>
    
    #if NET48
    // 源生成器在 net48 上不可用，改用惰性初始化的运行时 Regex
    private static Regex? _commandPatternCache;
    private static Regex CommandPattern() =>
        _commandPatternCache ??= new Regex(@"115(?:cdn|rc)?\.com/(sw[A-Za-z0-9]{8,})-([A-Za-z0-9]{4,8})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    #else
    [GeneratedRegex(@"115(?:cdn|rc)?\.com/(sw[A-Za-z0-9]{8,})-([A-Za-z0-9]{4,8})",
            RegexOptions.IgnoreCase)]
        private static partial Regex CommandPattern();
    #endif

    
    #if NET48
    // 源生成器在 net48 上不可用，改用惰性初始化的运行时 Regex
    private static Regex? _pwdInUrlPatternCache;
    private static Regex PwdInUrlPattern() =>
        _pwdInUrlPatternCache ??= new Regex(@"[?&]password=([A-Za-z0-9]+)", RegexOptions.Compiled);
    #else
    [GeneratedRegex(@"[?&]password=([A-Za-z0-9]+)")]
        private static partial Regex PwdInUrlPattern();
    #endif

    
    #if NET48
    // 源生成器在 net48 上不可用，改用惰性初始化的运行时 Regex
    private static Regex? _pathAttrPatternCache;
    private static Regex PathAttrPattern() =>
        _pathAttrPatternCache ??= new Regex(@"path=([^;]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    #else
    [GeneratedRegex(@"path=([^;]+)", RegexOptions.IgnoreCase)]
        private static partial Regex PathAttrPattern();
    #endif

    private readonly HttpClient _client;
    private readonly Func<string?> _cookieProvider;

    /// <summary>分享会话缓存：shareCode → (receiveCode, 分享者UID, 标题)。</summary>
    private readonly ConcurrentDictionary<string, (string ReceiveCode, string UserId, string Title)>
        _sessions = new();

    private string _clientUa = $"Mozilla/5.0 115Browser/{ClientUaFallback}";
    private bool _versionFetched;

    public Pan115Drive(HttpClient client, Func<string?> cookieProvider)
    {
        _client = client;
        _cookieProvider = cookieProvider;
    }

    public string Platform => CloudPlatforms.Pan115;
    public string DisplayName => "115 网盘";

    public LoginDescriptor? Login => new()
    {
        Kind = LoginKind.Browser,
        LoginUrl = "https://115.com/?ct=login",
        CookieDomains = new[] { "115.com" },
        Hint = "小文件可匿名下载；大文件需登录（115 不对未登录用户下发大文件直链）",
    };

    // ================= 识别 =================

    public bool CanHandle(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        return ShareIdPattern().IsMatch(input) || CommandPattern().IsMatch(input);
    }

    public ShareLink? ParseShareLink(string input)
    {
        var (url, passcode) = ShareTextParser.Parse(input);
        string text = url ?? input;

        var share = ShareIdPattern().Match(text);
        if (share.Success)
        {
            string sid = share.Groups[1].Value;
            // 链接里不带 password 不代表不需要码，服务端会返回 4100012
            string? pwd = PwdInUrlPattern().Match(text) is { Success: true } pm
                ? pm.Groups[1].Value
                : passcode;
            return new ShareLink
            {
                Platform = Platform, ShareId = sid, Passcode = pwd, RawInput = input,
            };
        }

        var cmd = CommandPattern().Match(text);
        if (cmd.Success)
        {
            return new ShareLink
            {
                Platform = Platform,
                ShareId = cmd.Groups[1].Value,
                // 口令形态里中划线后面直接就是提取码
                Passcode = passcode ?? cmd.Groups[2].Value,
                RawInput = input,
            };
        }

        return null;
    }

    // ================= 列目录 =================

    public async Task<CloudListing> ListAsync(ShareLink share, string? parentId, string? cursor,
        CancellationToken ct = default)
    {
        string receiveCode = share.Passcode ?? "";
        string cid = string.IsNullOrEmpty(cursor) ? (string.IsNullOrEmpty(parentId) ? RootCid : parentId)
            : parentId ?? RootCid;

        // 115 是索引分页：offset 递进，空页 / 不足一页 / 已取满 count 都算到底
        int offset = cursor != null && cursor.StartsWith("off:") &&
                     int.TryParse(cursor[4..], out int parsed) ? parsed : 0;

        var all = new List<CloudFile>();
        string title = "";
        int total = 0;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            string url = $"{ApiBase}/share/snap?share_code={Uri.EscapeDataString(share.ShareId)}" +
                         $"&receive_code={Uri.EscapeDataString(receiveCode)}" +
                         $"&offset={offset}&limit={SharePageLimit}&asc=0" +
                         $"&cid={Uri.EscapeDataString(cid)}&format=json";

            var json = await GetJsonAsync(url, WebUa, ShareReferer(share.ShareId, receiveCode), ct)
                .ConfigureAwait(false);
            CheckState(json, "获取分享列表失败");

            var data = json?["data"] as JsonObject;
            if (data == null) break;

            var shareInfo = data["shareinfo"] as JsonObject;
            if (title.Length == 0) title = Str(shareInfo, "share_title");

            string userId = Str(data["userinfo"] as JsonObject, "user_id");
            if (userId.Length > 0)
            {
                // 取直链的 user_id 只能是分享者，这里顺手缓存下来
                _sessions[share.ShareId] = (receiveCode, userId, title);
            }

            total = (int?)data["count"] ?? 0;

            var list = data["list"] as JsonArray;
            if (list == null || list.Count == 0) break;

            foreach (var node in list)
            {
                if (node is not JsonObject item) continue;
                // fc==0 是目录；目录没有 fid，用 cid 作 id
                bool isDir = ((int?)item["fc"] ?? (Str(item, "fid").Length == 0 ? 0 : 1)) == 0;
                string id = isDir ? Str(item, "cid") : Str(item, "fid");
                if (id.Length == 0) continue;

                all.Add(new CloudFile
                {
                    Id = id,
                    Name = Str(item, "n"),
                    Size = isDir ? 0 : (long?)item["s"] ?? 0,
                    IsDirectory = isDir,
                    ParentId = isDir ? Str(item, "pid") : Str(item, "cid"),
                    ModifyTime = Str(item, "t"),
                });
            }

            offset += list.Count;
            bool done = list.Count < SharePageLimit || (total > 0 && all.Count >= total);
            if (done) break;
        }

        // 分页游标只在「本页取满且未到总数」时给；这里已经一次性取完，故不返回游标
        return new CloudListing { Title = title, Files = all };
    }

    // ================= 取直链 =================

    public async Task<DirectLink> GetDirectLinkAsync(ShareLink share, CloudFile file,
        CancellationToken ct = default)
    {
        await EnsureClientVersionAsync(ct).ConfigureAwait(false);

        string receiveCode = share.Passcode ?? "";
        if (!_sessions.TryGetValue(share.ShareId, out var session))
        {
            // 用户直接点下载而没先列目录：先探一次根目录把 user_id 拿到
            await ListAsync(share, null, null, ct).ConfigureAwait(false);
            _sessions.TryGetValue(share.ShareId, out session);
        }

        string userId = session.UserId ?? "";
        if (userId.Length == 0)
            throw new CloudException("缺少分享者标识，请重新解析分享链接");

        string url = $"{ApiBase}/share/downurl?dl=1" +
                     $"&user_id={Uri.EscapeDataString(userId)}" +
                     $"&share_code={Uri.EscapeDataString(share.ShareId)}" +
                     $"&file_id={Uri.EscapeDataString(file.Id)}";
        if (receiveCode.Length > 0)
            url += $"&receive_code={Uri.EscapeDataString(receiveCode)}";

        var (json, cdnCookie) = await GetJsonWithCookieAsync(
            url, _clientUa, ShareReferer(share.ShareId, receiveCode), ct).ConfigureAwait(false);
        CheckState(json, "获取下载链接失败");

        var data = json?["data"] as JsonObject
                   ?? throw new CloudException("115 未返回下载链接");

        // 网页 JS 取的是 file_url_302 || url.url，三种形态都兜住
        string fileUrl = FirstNonBlank(Str(data, "file_url_302"), Str(data, "file_url"),
            Str(data["url"] as JsonObject, "url"));
        if (fileUrl.Length == 0) throw new CloudException("115 未返回有效下载地址");

        long size = long.TryParse(Str(data, "fs"), out long fs) ? fs : file.Size;

        var headers = new Dictionary<string, string>
        {
            // Referer 必须是 https://115.com/（不是分享页的 115cdn.com）
            ["Referer"] = Referer,
            // UA 必须是客户端串；浏览器 UA 会被判「网页端」拒发大文件直链
            ["User-Agent"] = _clientUa,
            // 匿名时只有下发的 CDN Cookie；登录态由调用方在 extra 里追加登录 Cookie
            ["Cookie"] = cdnCookie ?? "",
        };

        // 有登录 Cookie 时合并（CDN 校验两份都在）
        var login = _cookieProvider();
        if (!string.IsNullOrWhiteSpace(login))
            headers["Cookie"] = headers["Cookie"].Length > 0
                ? $"{login.TrimEnd(';', ' ')}; {headers["Cookie"]}"
                : login;

        if (headers["Cookie"].Length == 0) headers.Remove("Cookie");

        return new DirectLink
        {
            Url = fileUrl,
            FileName = FirstNonBlank(Str(data, "fn"), file.Name),
            Size = size,
            Headers = headers,
        };
    }

    public Task CleanupAsync(ShareLink share, CancellationToken ct = default) => Task.CompletedTask;

    // ================= HTTP =================

    /// <summary>
    /// 分享页 Referer。注意有提取码时结尾那个 <c>&amp;</c> 是<b>刻意保留</b>的 ——
    /// 115 校验 Referer 时确实吃这个形式，去掉会 4100008。
    /// </summary>
    private static string ShareReferer(string shareCode, string receiveCode) =>
        string.IsNullOrEmpty(receiveCode)
            ? $"{ShareHost}/s/{shareCode}"
            : $"{ShareHost}/s/{shareCode}?password={receiveCode}&";

    private async Task<JsonNode?> GetJsonAsync(string url, string ua, string referer,
        CancellationToken ct)
    {
        var (json, _) = await GetJsonWithCookieAsync(url, ua, referer, ct).ConfigureAwait(false);
        return json;
    }

    /// <summary>
    /// GET 一个 115 JSON 接口，同时把响应头里的 CDN Cookie 抓出来。
    ///
    /// <para>必须自己解析原始 <c>Set-Cookie</c>：<see cref="HttpClientHandler.UseCookies"/>
    /// 会自动按域规则管理，而这里要的是「path 绑定到对象路径」的那一条，
    /// 且要跨主机带到 CDN 去。</para>
    /// </summary>
    private async Task<(JsonNode? Json, string? CdnCookie)> GetJsonWithCookieAsync(
        string url, string ua, string referer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", ua);
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
        req.Headers.TryAddWithoutValidation("Referer", referer);

        var cookie = _cookieProvider();
        if (!string.IsNullOrWhiteSpace(cookie))
            req.Headers.TryAddWithoutValidation("Cookie", cookie);

        using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);

        string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (body.Trim().Length == 0)
            throw new CloudException($"115 响应为空（HTTP {(int)resp.StatusCode}）");

        // 115 会「HTTP 非 2xx 但 body 是合法 JSON」，所以先解析 body 再判状态码
        JsonNode? json = null;
        try
        {
            json = JsonNode.Parse(body);
        }
        catch
        {
            throw new CloudException($"115 响应解析失败（HTTP {(int)resp.StatusCode}）");
        }

        string? cdn = DownloadCookie(resp);
        return (json, cdn);
    }

    /// <summary>
    /// 从 Set-Cookie 里挑出 CDN 那条。
    ///
    /// <para>规则：跳过 expires/path/domain 这类怪名，优先取「path 属性存在且不为 /」的那条
    /// （直链 CDN 只认 path 绑定的；WAF 的 <c>acw_tc</c> 之类不是它）。</para>
    /// </summary>
    private static string? DownloadCookie(HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("Set-Cookie", out var values)) return null;

        string? fallback = null;
        foreach (var raw in values)
        {
            string pair = raw.Split(';')[0].Trim();
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;

            string name = pair[..eq].Trim();
            if (name.Equals("expires", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("path", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("domain", StringComparison.OrdinalIgnoreCase))
                continue;

            fallback ??= pair;

            var pm = PathAttrPattern().Match(raw);
            if (pm.Success)
            {
                string path = pm.Groups[1].Value.Trim();
                if (path.Length > 0 && path != "/") return pair;
            }
        }
        return fallback;
    }

    /// <summary>拉一次 115 客户端版本号（免登录）。失败就用兜底版本。</summary>
    private async Task EnsureClientVersionAsync(CancellationToken ct)
    {
        if (_versionFetched) return;
        _versionFetched = true;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, AppVersionUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", WebUa);
            req.Headers.TryAddWithoutValidation("Referer", Referer);

            using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var json = JsonNode.Parse(body);
            string ver = Str((json?["data"] as JsonObject)?["win"] as JsonObject, "version_code");
            if (ver.Length > 0) _clientUa = $"Mozilla/5.0 115Browser/{ver}";
        }
        catch
        {
            // 拿不到就继续用兜底版本
        }
    }

    // ================= 错误码 =================

    private static void CheckState(JsonNode? json, string fallback)
    {
        if (json == null) throw new CloudException($"{fallback}（响应为空）");
        if ((bool?)json["state"] == true) return;

        int errno = (int?)json["errno"] ?? (int?)json["errNo"] ?? 0;
        string raw = FirstNonBlank(Str(json, "error"), Str(json, "msg"));

        string message = errno switch
        {
            ErrnoLoginTimeout => "115 登录已过期，请重新登录",
            ErrnoNeedCode => "该分享需要访问码，请填写访问码后重试",
            ErrnoWrongCode => "访问码错误，请检查后重试",
            ErrnoShareGone => "分享链接不存在或已被删除",
            ErrnoBusy => "115 上一次操作还没完成，请稍后重试",
            ErrnoClientTooOld =>
                "115 判定当前标识版本过低、拒绝下发直链（大文件需登录 115 后下载）",
            _ => raw.Length > 0 ? raw : fallback,
        };

        if (errno == ErrnoNeedCode || errno == ErrnoWrongCode)
            throw new PasscodeRequiredException(message);

        string suffix = errno != 0 && !message.Contains(errno.ToString())
            ? $"（errno={errno}）"
            : "";
        throw new CloudException(message + suffix);
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
}
