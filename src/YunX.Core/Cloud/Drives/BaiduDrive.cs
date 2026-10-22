using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using YunX.Core.Cloud;

namespace YunX.Core.Cloud.Drives;

/// <summary>
/// 百度网盘适配器。
///
/// <para>移植自原项目 <c>BaiduApi.kt</c>（648 行）。这是 6 个网盘中<b>链路最长</b>的一个，
/// 因为百度分享文件必须先<b>转存到自己网盘</b>才给直链：</para>
/// <list type="number">
///   <item><c>share/verify</c>：验证提取码，返回 <c>randsk</c>（即 sekey）；</item>
///   <item><c>xpan/share?method=list</c>：列目录（根目录 <c>root=1</c>，子目录 <c>root=0</c> 且必须带 BDCLND）；</item>
///   <item><c>share/transfer</c>：转存到自己的临时目录，返回新 fsId；</item>
///   <item><c>pcs/file?method=locatedownload</c>：按路径取直链（返回多个候选，选 appall 明文通道）；</item>
///   <item><c>filemanager?opera=delete</c>：<b>清理</b>转存产生的临时文件。</item>
/// </list>
///
/// <para><b>三个原项目踩过的坑</b>：</para>
/// <list type="bullet">
///   <item>子目录（root=0）必须携带 <c>BDCLND</c> cookie（= randsk），否则 errno=2；</item>
///   <item>公共分享 sekey 为空时**省略** <c>&amp;sekey=</c> 参数，传空值会报错；</item>
///   <item>locatedownload 的 rank1 通常是 <c>d2-ant.baidu.com</c> 加密通道（内容要 AES-CTR 解密，
///         且部分网络 TLS 握手失败），要优先选 <c>appall*.baidupcs.com</c> 的明文通道。</item>
/// </list>
/// </summary>
public sealed partial class BaiduDrive : ICloudDrive
{
    private const string WebUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private const string AppId = "250528";

    /// <summary>转存用的临时目录名（下载完成后删除里面转存的文件）。</summary>
    private const string TempDirName = "YunX临时转存";

    public string Platform => CloudPlatforms.Baidu;
    public string DisplayName => "百度网盘";

    public LoginDescriptor? Login => new()
    {
        Kind = LoginKind.Browser,
        LoginUrl = "https://pan.baidu.com/",
        CookieDomains = new[] { "baidu.com" },
        Hint = "在内嵌浏览器里登录百度网盘后会自动保存 Cookie（BDUSS 是必需项）",
    };

    private readonly HttpClient _client;
    private readonly Func<string?> _cookieProvider;

    /// <summary>分享会话：surl → (sekey, shareId, uk)。列目录与转存都要用到。</summary>
    // 见 QuarkDrive 同名注释：适配器是单例，会被多个下载任务并发调用，必须用并发字典。
    private readonly ConcurrentDictionary<string, (string Sekey, string ShareId, string Uk)> _sessions = new();

    public BaiduDrive(HttpClient client, Func<string?> cookieProvider)
    {
        _client = client;
        _cookieProvider = cookieProvider;
    }

    // ================= 识别 =================

    public bool CanHandle(string input) =>
        !string.IsNullOrWhiteSpace(input) &&
        input.Contains("pan.baidu.com", StringComparison.OrdinalIgnoreCase);

    public ShareLink? ParseShareLink(string input)
    {
        var (url, passcode) = ShareTextParser.Parse(input);
        if (url == null) return null;

        // https://pan.baidu.com/s/1xxxxxxx?pwd=abcd
        var m = SurlPattern().Match(url);
        if (!m.Success) return null;

        if (string.IsNullOrEmpty(passcode))
        {
            int q = url.IndexOf('?');
            if (q >= 0)
            {
                var query = HttpUtility.ParseQueryString(url[(q + 1)..]);
                passcode = query["pwd"];
            }
        }

        return new ShareLink
        {
            Platform = Platform, ShareId = m.Groups[1].Value, Passcode = passcode, RawInput = input,
        };
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"/s/([A-Za-z0-9_\-]{4,32})")]
    private static partial System.Text.RegularExpressions.Regex SurlPattern();

    // ================= 列目录 =================

    public async Task<CloudListing> ListAsync(ShareLink share, string? parentId,
        string? cursor, CancellationToken ct = default)
    {
        string cookie = RequireCookie();
        string surl = share.ShareId;

        // 建立会话：验证提取码拿 randsk（= sekey）
        var session = await EnsureSessionAsync(share, cookie, ct).ConfigureAwait(false);

        string dir = string.IsNullOrEmpty(parentId) ? "/" : parentId;
        bool isRoot = dir == "/";
        int page = int.TryParse(cursor, out int p) ? p : 1;

        // 根目录 root=1；子目录 root=0（root=1 下百度忽略 dir，进不去子目录）
        string root = isRoot ? "1" : "0";
        string sekeyPart = session.Sekey.Length > 0
            ? $"&sekey={Uri.EscapeDataString(session.Sekey)}" : "";

        string url = "https://pan.baidu.com/rest/2.0/xpan/share?method=list" +
                     $"&shorturl={Uri.EscapeDataString(surl)}&page={page}&num=100" +
                     $"&root={root}&dir={Uri.EscapeDataString(dir)}{sekeyPart}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        // 子目录必须带 BDCLND（= randsk），否则 errno=2
        string authCookie = cookie.Contains("BDCLND=") || session.Sekey.Length == 0
            ? cookie : $"{cookie}; BDCLND={session.Sekey}";
        req.Headers.TryAddWithoutValidation("Cookie", authCookie);
        req.Headers.TryAddWithoutValidation("User-Agent", WebUa);
        req.Headers.TryAddWithoutValidation("Referer", $"https://pan.baidu.com/s/{surl}");

        using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(text);
        var rootEl = doc.RootElement;

        int errno = rootEl.TryGetProperty("errno", out var e) ? e.GetInt32() : 0;
        if (errno != 0)
        {
            if (errno == 2)
                throw new PasscodeRequiredException(
                    string.IsNullOrEmpty(share.Passcode) ? "该分享需要提取码" : "提取码错误或已失效");
            string msg = rootEl.TryGetProperty("errmsg", out var m) ? m.GetString() ?? "" : "";
            throw new CloudException(msg.Length > 0 ? msg : $"获取文件列表失败（errno={errno}）");
        }

        var listing = new CloudListing();
        if (!rootEl.TryGetProperty("list", out var list)) return listing;

        foreach (var f in list.EnumerateArray())
        {
            int isDir = f.TryGetProperty("isdir", out var d) ? d.GetInt32() : 0;
            string name = Str(f, "server_filename");
            listing.Files.Add(new CloudFile
            {
                // 百度用 path 作为唯一标识（转存与取链都按路径走）
                Id = Str(f, "path"),
                Name = name,
                Size = f.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0,
                IsDirectory = isDir == 1,
                ParentId = dir,
                ModifyTime = f.TryGetProperty("server_mtime", out var mt) &&
                             mt.ValueKind == JsonValueKind.Number
                    ? DateTimeOffset.FromUnixTimeSeconds(mt.GetInt64()).ToString("yyyy-MM-dd HH:mm")
                    : "",
            });
        }

        if (listing.Files.Count >= 100) listing.NextCursor = (page + 1).ToString();
        return listing;
    }

    private async Task<(string Sekey, string ShareId, string Uk)> EnsureSessionAsync(
        ShareLink share, string cookie, CancellationToken ct)
    {
        if (_sessions.TryGetValue(share.ShareId, out var cached)) return cached;

        string sekey = "";
        if (!string.IsNullOrEmpty(share.Passcode))
            sekey = await VerifyAsync(share.ShareId, share.Passcode, cookie, ct).ConfigureAwait(false);

        // 第一次列目录拿 share_id / uk（转存需要）
        var (shareId, uk) = await ProbeShareIdsAsync(share.ShareId, sekey, cookie, ct)
            .ConfigureAwait(false);

        var session = (sekey, shareId, uk);
        _sessions[share.ShareId] = session;
        return session;
    }

    private async Task<string> VerifyAsync(string surl, string pwd, string cookie,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post,
            $"https://pan.baidu.com/share/verify?surl={Uri.EscapeDataString(surl)}");
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["pwd"] = pwd,
            ["vcode_str"] = "",
            ["vcode"] = "",
        });
        req.Headers.TryAddWithoutValidation("Cookie", cookie);
        req.Headers.TryAddWithoutValidation("User-Agent", WebUa);
        req.Headers.TryAddWithoutValidation("Referer", $"https://pan.baidu.com/s/{surl}");

        using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);

        int errno = doc.RootElement.TryGetProperty("errno", out var e) ? e.GetInt32() : 0;
        if (errno != 0)
            throw new PasscodeRequiredException(errno == -9 ? "提取码错误" : $"验证提取码失败（errno={errno}）");

        string randsk = Str(doc.RootElement, "randsk");
        if (randsk.Length == 0) throw new CloudException("验证通过但未返回分享密钥");
        return randsk;
    }

    private async Task<(string ShareId, string Uk)> ProbeShareIdsAsync(string surl, string sekey,
        string cookie, CancellationToken ct)
    {
        string sekeyPart = sekey.Length > 0 ? $"&sekey={Uri.EscapeDataString(sekey)}" : "";
        string url = "https://pan.baidu.com/rest/2.0/xpan/share?method=list" +
                     $"&shorturl={Uri.EscapeDataString(surl)}&page=1&num=1&root=1&dir=%2F{sekeyPart}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Cookie", cookie);
        req.Headers.TryAddWithoutValidation("User-Agent", WebUa);

        using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);

        string shareId = "";
        string uk = "";
        var root = doc.RootElement;
        // share_id 可能在顶层也可能在 data 里
        if (root.TryGetProperty("share_id", out var sid)) shareId = sid.ToString();
        if (root.TryGetProperty("uk", out var u)) uk = u.ToString();
        if (root.TryGetProperty("data", out var data))
        {
            if (shareId.Length == 0 && data.TryGetProperty("share_id", out var s2))
                shareId = s2.ToString();
            if (uk.Length == 0 && data.TryGetProperty("uk", out var u2)) uk = u2.ToString();
        }
        return (shareId, uk);
    }

    // ================= 取直链（转存 → 定位 → 清理） =================

    public async Task<DirectLink> GetDirectLinkAsync(ShareLink share, CloudFile file,
        CancellationToken ct = default)
    {
        string cookie = RequireCookie();
        var session = await EnsureSessionAsync(share, cookie, ct).ConfigureAwait(false);

        // 目录需要先逐级转存；这里只处理文件（目录由上层展开后逐个下载）
        if (session.ShareId.Length == 0 || session.Uk.Length == 0)
            throw new CloudException("未能取到分享标识（share_id / uk），请确认分享链接有效且已登录");

        // 1) 确保临时目录存在
        string tempDir = "/" + TempDirName;
        await CreateDirAsync(tempDir, cookie, ct).ConfigureAwait(false);

        // 2) 转存
        string fsId = Str2(file);
        var (newPath, newFsId) = await TransferAsync(session.ShareId, session.Uk,
            session.Sekey, fsId, tempDir, cookie, ct).ConfigureAwait(false);

        // 3) 定位直链
        string url = await LocateDownloadAsync(newPath, cookie, ct).ConfigureAwait(false);

        return new DirectLink
        {
            Url = url,
            FileName = file.Name,
            Size = file.Size,
            Headers = new Dictionary<string, string>
            {
                // 百度 CDN 校验 UA（要用手机 UA 风格，网页 UA 也可以）
                ["User-Agent"] = WebUa,
                ["Referer"] = "https://pan.baidu.com/",
                ["Cookie"] = cookie,
            },
        };
    }

    /// <summary>
    /// 清理转存产生的临时文件。
    /// 百度必须先转存才能取链，所以这一步是**必需**的 —— 否则用户网盘会被垃圾文件塞满。
    /// </summary>
    public async Task CleanupAsync(ShareLink share, CancellationToken ct = default)
    {
        string? cookie = _cookieProvider();
        if (string.IsNullOrEmpty(cookie)) return;

        try
        {
            // 删除整个临时目录（比逐个删文件更彻底）
            await DeleteAsync("/" + TempDirName, cookie, ct).ConfigureAwait(false);
        }
        catch
        {
            // 清理失败不应影响主流程：文件已下载完成
        }
        _sessions.TryRemove(share.ShareId, out _);
    }

    private static string Str2(CloudFile file) => file.Id;   // 百度用 path，转存走 fsidlist 时用 fs_id

    private async Task CreateDirAsync(string path, string cookie, CancellationToken ct)
    {
        string bdstoken = await GetBdstokenAsync(cookie, ct).ConfigureAwait(false);
        string url = "https://pan.baidu.com/api/create?a=commit&clienttype=0" +
                     $"&app_id={AppId}&web=1&bdstoken={bdstoken}";

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["path"] = path,
            ["isdir"] = "1",
            ["block_list"] = "[]",
        });
        req.Headers.TryAddWithoutValidation("Cookie", cookie);
        req.Headers.TryAddWithoutValidation("User-Agent", WebUa);

        using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try
        {
            using var doc = JsonDocument.Parse(text);
            int errno = doc.RootElement.TryGetProperty("errno", out var e) ? e.GetInt32() : 0;
            // -8 = 目录已存在，视为成功
            if (errno != 0 && errno != -8)
                throw new CloudException($"创建临时目录失败（errno={errno}）");
        }
        catch (JsonException)
        {
            // 非 JSON 响应：目录可能已存在，继续走
        }
    }

    private async Task<(string Path, string FsId)> TransferAsync(string shareId, string uk,
        string sekey, string fsId, string toDir, string cookie, CancellationToken ct)
    {
        string bdstoken = await GetBdstokenAsync(cookie, ct).ConfigureAwait(false);
        string url = "https://pan.baidu.com/share/transfer" +
                     $"?shareid={Uri.EscapeDataString(shareId)}&from={Uri.EscapeDataString(uk)}" +
                     $"&channel=chunlei&sekey={Uri.EscapeDataString(sekey)}" +
                     $"&ondup=newcopy&web=1&app_id={AppId}&bdstoken={bdstoken}&clienttype=0";

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["fsidlist"] = $"[\"{fsId}\"]",
            ["path"] = toDir,
        });
        // transfer 必须带 BDCLND（= sekey）
        string authCookie = cookie.Contains("BDCLND=") ? cookie : $"{cookie}; BDCLND={sekey}";
        req.Headers.TryAddWithoutValidation("Cookie", authCookie);
        req.Headers.TryAddWithoutValidation("User-Agent", WebUa);
        req.Headers.TryAddWithoutValidation("Origin", "https://pan.baidu.com");
        req.Headers.TryAddWithoutValidation("Referer", "https://pan.baidu.com/s/");

        using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        int errno = root.TryGetProperty("errno", out var e) ? e.GetInt32() : 0;
        if (errno != 0)
            throw new CloudException(errno switch
            {
                -6 => "登录态已失效（bdstoken 无效），请重新登录百度网盘",
                12 => "转存失败：分享已取消",
                _ => $"转存失败（errno={errno}）",
            });

        if (root.TryGetProperty("extra", out var extra) &&
            extra.TryGetProperty("list", out var list) && list.GetArrayLength() > 0)
        {
            var first = list[0];
            string newFsId = first.TryGetProperty("to_fs_id", out var tf) ? tf.ToString() : "";
            string newPath = Str(first, "to");
            if (newFsId.Length > 0 && newPath.Length > 0) return (newPath, newFsId);
        }
        throw new CloudException("转存成功但未返回新文件位置");
    }

    private async Task<string> LocateDownloadAsync(string path, string cookie,
        CancellationToken ct)
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string url = "https://d.pcs.baidu.com/rest/2.0/pcs/file?method=locatedownload" +
                     $"&app_id={AppId}&path={Uri.EscapeDataString(path)}" +
                     $"&clienttype=0&version=7.0.0&time={now}&rand=1&devuid=1&cuid=1";

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.TryAddWithoutValidation("Cookie", cookie);
        // 手机 UA 更容易拿到明文通道
        req.Headers.TryAddWithoutValidation("User-Agent",
            "netdisk;7.0.0;PC;PC-Windows;10.0.19045;WindowsBaiduYunGuanJia");

        using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        if (!root.TryGetProperty("urls", out var urls) || urls.GetArrayLength() == 0)
        {
            int errno = root.TryGetProperty("errno", out var e) ? e.GetInt32() : 0;
            throw new CloudException(errno switch
            {
                112 => "转存的文件还未就绪，稍后重试",
                -6 => "登录态已失效，请重新登录",
                _ => $"获取下载直链失败（errno={errno}）",
            });
        }

        // 优先选 appall*.baidupcs.com 的**明文**通道：
        // rank1 通常是 d2-ant.baidu.com 加密通道（内容需 AES-CTR 解密，且部分网络 TLS 握手失败）
        string? plain = null;
        string? fallback = null;
        foreach (var u in urls.EnumerateArray())
        {
            string href = Str(u, "url");
            if (href.Length == 0) continue;
            fallback ??= href;

            int encrypt = u.TryGetProperty("encrypt", out var en) ? en.GetInt32() : 1;
            if (encrypt == 0 || href.Contains("appall", StringComparison.OrdinalIgnoreCase) ||
                href.Contains("baidupcs.com", StringComparison.OrdinalIgnoreCase))
            {
                plain = href;
                break;
            }
        }

        return plain ?? fallback ?? throw new CloudException("候选直链都不可用");
    }

    private async Task DeleteAsync(string path, string cookie, CancellationToken ct)
    {
        string bdstoken = await GetBdstokenAsync(cookie, ct).ConfigureAwait(false);
        string url = "https://pan.baidu.com/api/filemanager?async=2&onnest=fail&opera=delete" +
                     $"&bdstoken={bdstoken}&newVerify=1&clienttype=0&app_id={AppId}&web=1";

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["filelist"] = $"[\"{path}\"]",
        });
        req.Headers.TryAddWithoutValidation("Cookie", cookie);
        req.Headers.TryAddWithoutValidation("User-Agent", WebUa);

        using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
        await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private async Task<string> GetBdstokenAsync(string cookie, CancellationToken ct)
    {
        string url = "https://pan.baidu.com/api/gettemplatevariable" +
                     "?fields=[\"bdstoken\"]&clienttype=0&app_id=250528&web=1";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Cookie", cookie);
        req.Headers.TryAddWithoutValidation("User-Agent", WebUa);

        using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("result", out var r) &&
                r.TryGetProperty("bdstoken", out var bt))
                return bt.GetString() ?? "";
        }
        catch (JsonException)
        {
            // 落到异常
        }
        throw new NotLoggedInException("百度网盘");
    }

    private string RequireCookie()
    {
        string? cookie = _cookieProvider();
        if (string.IsNullOrEmpty(cookie) || !cookie.Contains("BDUSS", StringComparison.Ordinal))
            throw new NotLoggedInException("百度网盘");
        return cookie;
    }

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
