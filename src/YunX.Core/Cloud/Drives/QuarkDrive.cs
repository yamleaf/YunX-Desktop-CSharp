using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using YunX.Core.Cloud;

namespace YunX.Core.Cloud.Drives;

/// <summary>
/// 夸克网盘适配器。
///
/// <para>移植自原项目 <c>QuarkApi.kt</c>（718 行）。核心链路：</para>
/// <list type="number">
///   <item><c>share/sharepage/token</c>：用 <c>pwd_id</c>(=分享短码) + passcode 换 <c>stoken</c>；</item>
///   <item><c>share/sharepage/detail</c>：用 stoken 列目录，返回 <c>share_fid_token</c>；</item>
///   <item><c>file/download</c>：把「分享凭证」直接交给下载接口换直链 —— <b>不转存</b>，
///         因此不会在用户网盘里留临时目录。</item>
/// </list>
///
/// <para><b>关键细节</b>（都是原项目抓包实测得出的）：</para>
/// <list type="bullet">
///   <item>列表接口参数名是 <c>pwd_id</c>，值就是分享短码；字段名是
///         <c>file_name</c>/<c>size</c>/<c>dir</c>/<c>share_fid_token</c>，不是文档里的 fname/fsize/isdir；</item>
///   <item>必须带 <c>Origin</c> / <c>Referer</c>，否则可能 400；</item>
///   <item>响应会下发新的 <c>__puus</c> Cookie，直链签名绑定取链时刻的 Cookie —— 必须合并回会话；</item>
///   <item>游客态取链有约 50MB 上限（错误码 23018），登录后无此限制。</item>
/// </list>
/// </summary>
public partial class QuarkDrive : ICloudDrive
{
    // 这些做成 virtual：UC 与夸克同源（同一套 clouddrive API），仅域名与 pr 参数不同，
    // 抽基类后 UC 只覆盖这几项，避免复制 700 行逻辑。
    protected virtual string ApiBase => "https://drive-pc.quark.cn";
    protected virtual string PrParam => "pr=ucpro&fr=pc";
    protected virtual string DetailPath => "/1/clouddrive/share/sharepage/detail";
    protected virtual string Referer => "https://pan.quark.cn/";
    protected virtual string OriginHost => "https://pan.quark.cn";

    private string TokenUrl => $"{ApiBase}/1/clouddrive/share/sharepage/token?{PrParam}";
    private string DetailUrl => $"{ApiBase}{DetailPath}?{PrParam}";
    private string DownloadUrl =>
        $"{ApiBase}/1/clouddrive/file/download?{PrParam}&sys=win32&ve=3.23.2";

    private const string ApiUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) quark-cloud-drive/3.23.2 Chrome/112.0.5615.165 " +
        "Electron/24.1.3.8 Safari/537.36 Channel/pckk_other_ch";

    public virtual string Platform => CloudPlatforms.Quark;
    public virtual string DisplayName => "夸克网盘";

    public virtual LoginDescriptor? Login => new()
    {
        Kind = LoginKind.Browser,
        LoginUrl = "https://pan.quark.cn/?fr=pc&platform=pc",
        CookieDomains = new[] { "quark.cn" },
        Hint = "在内嵌浏览器里登录夸克后会自动保存 Cookie",
    };

    private readonly HttpClient _client;
    private readonly Func<string?> _cookieProvider;

    /// <summary>分享会话缓存：shareId → (stoken, title)。避免每次列目录都重新换 stoken。</summary>
    // 适配器是进程级单例，ListAsync / GetDirectLinkAsync 会在各下载任务的 Task 里并发调用。
    // 用普通 Dictionary 时并发 Add/Remove 会破坏内部桶链 → FindEntry 死循环（CPU 100% 不可恢复）。
    private readonly ConcurrentDictionary<string, (string Stoken, string Title)> _sessions = new();

    public QuarkDrive(HttpClient client, Func<string?> cookieProvider)
    {
        _client = client;
        _cookieProvider = cookieProvider;
    }

    // ================= 识别 =================

    public virtual bool CanHandle(string input) =>
        !string.IsNullOrWhiteSpace(input) &&
        input.Contains("quark.cn", StringComparison.OrdinalIgnoreCase);

    public virtual ShareLink? ParseShareLink(string input)
    {
        var (url, passcode) = ShareTextParser.Parse(input);
        if (url == null) return null;

        // https://pan.quark.cn/s/{shareId}
        var m = ShareIdPattern().Match(url);
        if (!m.Success) return null;

        return new ShareLink
        {
            Platform = Platform,
            ShareId = m.Groups[1].Value,
            Passcode = passcode ?? QueryPasscode(url),
            RawInput = input,
        };
    }

    
    #if NET48
    // 源生成器在 net48 上不可用，改用惰性初始化的运行时 Regex
    private static Regex? _shareIdPatternCache;
    private static Regex ShareIdPattern() =>
        _shareIdPatternCache ??= new Regex(@"/s/([A-Za-z0-9]{6,32})", RegexOptions.Compiled);
    #else
    [System.Text.RegularExpressions.GeneratedRegex(@"/s/([A-Za-z0-9]{6,32})")]
        protected static partial System.Text.RegularExpressions.Regex ShareIdPattern();
    #endif

    private static string? QueryPasscode(string url)
    {
        int q = url.IndexOf('?');
        if (q < 0) return null;
        var query = HttpUtility.ParseQueryString(url[(q + 1)..]);
        return query["pwd"] ?? query["passcode"];
    }

    // ================= 会话 =================

    private async Task<(string Stoken, string Title)> GetStokenAsync(ShareLink share,
        CancellationToken ct)
    {
        if (_sessions.TryGetValue(share.ShareId, out var cached)) return cached;

        var body = new JsonObject
        {
            ["pwd_id"] = share.ShareId,
            ["passcode"] = share.Passcode ?? "",
            ["support_visit_limit_private_share"] = true,
        };

        var (ok, root) = await PostJsonAsync(TokenUrl, body, ct).ConfigureAwait(false);
        if (!ok)
        {
            int code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
            string msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            if (code == 41004 || msg.Contains("密码", StringComparison.Ordinal))
                throw new PasscodeRequiredException(
                    string.IsNullOrEmpty(share.Passcode) ? "该分享需要提取码" : "提取码错误");
            throw new CloudException($"获取分享凭证失败：{msg}");
        }

        var data = root.GetProperty("data");
        string stoken = data.TryGetProperty("stoken", out var st) ? st.GetString() ?? "" : "";
        string title = data.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "";
        if (stoken.Length == 0) throw new CloudException("响应缺少 stoken");

        var session = (stoken, title);
        _sessions[share.ShareId] = session;
        return session;
    }

    // ================= 列目录 =================

    public async Task<CloudListing> ListAsync(ShareLink share, string? parentId,
        string? cursor, CancellationToken ct = default)
    {
        var (stoken, title) = await GetStokenAsync(share, ct).ConfigureAwait(false);

        int page = int.TryParse(cursor, out int p) ? p : 1;
        string pdir = string.IsNullOrEmpty(parentId) ? "0" : parentId;

        string url = DetailUrl +
                     $"&pwd_id={Uri.EscapeDataString(share.ShareId)}" +
                     $"&stoken={Uri.EscapeDataString(stoken)}" +
                     $"&pdir_fid={Uri.EscapeDataString(pdir)}" +
                     "&ver=2&force=0" +
                     $"&_page={page}&_size=100" +
                     "&_fetch_banner=0&_fetch_share=0&fetch_relate_conversation=0&_fetch_total=1" +
                     "&_sort=file_type:asc,file_name:asc";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AttachHeaders(req, needCookie: false);

        using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        if (!Succeeded(root, out string message))
        {
            if (message.Contains("失效", StringComparison.Ordinal) ||
                message.Contains("不存在", StringComparison.Ordinal))
                throw new CloudException("分享已失效");
            throw new CloudException($"获取文件列表失败：{message}");
        }

        var listing = new CloudListing { Title = title };
        if (!root.TryGetProperty("data", out var data)) return listing;

        if (data.TryGetProperty("list", out var list))
        {
            foreach (var f in list.EnumerateArray())
            {
                listing.Files.Add(new CloudFile
                {
                    Id = Str(f, "fid"),
                    Name = Str(f, "file_name"),
                    Size = f.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0,
                    IsDirectory = f.TryGetProperty("dir", out var d) && d.GetBoolean(),
                    ParentId = pdir,
                    // share_fid_token：取链时必须原样带回
                    Token = Str(f, "share_fid_token"),
                    ModifyTime = f.TryGetProperty("updated_at", out var ua) &&
                                 ua.ValueKind == JsonValueKind.Number
                        ? DateTimeOffset.FromUnixTimeMilliseconds(ua.GetInt64()).ToString("yyyy-MM-dd HH:mm")
                        : "",
                });
            }
        }

        // 有 total 且已取满则还有下一页
        if (data.TryGetProperty("total", out var total) && total.GetInt32() > page * 100)
            listing.NextCursor = (page + 1).ToString();

        return listing;
    }

    // ================= 取直链 =================

    /// <summary>
    /// 免转存取链：把分享凭证直接交给 download 接口。
    /// 登录态下无 50MB 限制，且不会在用户网盘留临时目录。
    /// </summary>
    public async Task<DirectLink> GetDirectLinkAsync(ShareLink share, CloudFile file,
        CancellationToken ct = default)
    {
        var (stoken, _) = await GetStokenAsync(share, ct).ConfigureAwait(false);

        var body = new JsonObject
        {
            ["fids"] = new JsonArray(file.Id),
            ["fids_token"] = new JsonArray(file.Token),
            ["pwd_id"] = share.ShareId,
            ["stoken"] = stoken,
            // speedup_session / token 是社交转存令牌，官方前端同样 catch 后传空串
            ["speedup_session"] = "",
            ["token"] = "",
        };

        var (ok, root, setCookies) = await PostJsonWithCookiesAsync(DownloadUrl, body, ct)
            .ConfigureAwait(false);

        if (!ok)
        {
            int code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
            string msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            string friendly = code switch
            {
                31001 => "夸克登录态已失效，请重新登录后再试",
                23018 => "服务端按游客限制拒绝了该文件（夸克约 50MB 上限），请先登录",
                41004 => "提取码错误",
                _ => msg.Length > 0 ? msg : "获取下载链接失败",
            };
            if (code == 31001) throw new NotLoggedInException("夸克网盘");
            throw new CloudException(friendly);
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array ||
            data.GetArrayLength() == 0)
            throw new CloudException("未返回下载链接");

        var item = data[0];
        string url = Str(item, "download_url");
        if (url.Length == 0) throw new CloudException("响应缺少 download_url");

        // 直链签名绑定取链时刻的 Cookie：把本次响应下发的新 Cookie 带上
        var headers = new Dictionary<string, string>
        {
            ["Referer"] = Referer,
            ["User-Agent"] = ApiUserAgent,
        };
        string? mergedCookie = MergeCookies(_cookieProvider(), setCookies);
        if (!string.IsNullOrEmpty(mergedCookie)) headers["Cookie"] = mergedCookie;

        return new DirectLink
        {
            Url = url,
            FileName = Str(item, "file_name") is { Length: > 0 } fn ? fn : file.Name,
            Size = item.TryGetProperty("size", out var sz) ? sz.GetInt64() : file.Size,
            Headers = headers,
        };
    }

    /// <summary>夸克免转存，无临时文件。</summary>
    public Task CleanupAsync(ShareLink share, CancellationToken ct = default)
        => Task.CompletedTask;

    // ================= HTTP 辅助 =================

    private void AttachHeaders(HttpRequestMessage req, bool needCookie)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", ApiUserAgent);
        req.Headers.TryAddWithoutValidation("Origin", OriginHost);
        req.Headers.TryAddWithoutValidation("Referer", Referer);
        if (!needCookie) return;
        var cookie = _cookieProvider();
        if (!string.IsNullOrEmpty(cookie))
            req.Headers.TryAddWithoutValidation("Cookie", cookie);
    }

    private async Task<(bool Ok, JsonElement Root)> PostJsonAsync(string url, JsonObject body,
        CancellationToken ct)
    {
        var (ok, root, _) = await PostJsonWithCookiesAsync(url, body, ct).ConfigureAwait(false);
        return (ok, root);
    }

    private async Task<(bool Ok, JsonElement Root, List<string> SetCookies)>
        PostJsonWithCookiesAsync(string url, JsonObject body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8,
            "application/json");
        AttachHeaders(req, needCookie: true);

        using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
        var cookies = resp.Headers.TryGetValues("Set-Cookie", out var vals)
            ? vals.ToList() : new List<string>();

        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // 非 JSON（风控页 / HTML 错误页）：给出可分辨的提示而不是 JSON 解析异常
        if (!text.TrimStart().StartsWith('{'))
            throw new CloudException($"服务端返回了非预期内容（HTTP {(int)resp.StatusCode}），可能触发了风控");

        var doc = JsonDocument.Parse(text);
        // JsonDocument 被释放后 JsonElement 失效，这里 Clone 出去
        var root = doc.RootElement.Clone();
        bool ok = Succeeded(root, out _);
        return (ok, root, cookies);
    }

    /// <summary>夸克判断成功：status == 200 或 code == 0。</summary>
    private static bool Succeeded(JsonElement root, out string message)
    {
        message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
        int status = root.TryGetProperty("status", out var s) ? s.GetInt32() : 0;
        int code = root.TryGetProperty("code", out var c) ? c.GetInt32() : 0;
        return status == 200 || code == 0;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    /// <summary>
    /// 合并 Cookie：响应下发的新值覆盖旧值。
    /// 夸克取链后会给新的 <c>__puus</c>，直链签名与它绑定，必须回写会话。
    /// </summary>
    private static string? MergeCookies(string? original, List<string> setCookies)
    {
        if (setCookies.Count == 0) return original;

        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(original))
        {
            foreach (var part in original.Split(';'))
            {
                int eq = part.IndexOf('=');
                if (eq <= 0) continue;
                map[part[..eq].Trim()] = part[(eq + 1)..].Trim();
            }
        }

        foreach (var sc in setCookies)
        {
            // Set-Cookie: name=value; Path=/; ...
            string first = sc.Split(';')[0];
            int eq = first.IndexOf('=');
            if (eq <= 0) continue;
            string name = first[..eq].Trim();
            string value = first[(eq + 1)..].Trim();
            if (value.Length == 0) map.Remove(name);
            else map[name] = value;
        }

        return map.Count == 0 ? null : string.Join("; ", map.Select(kv => $"{kv.Key}={kv.Value}"));
    }
}
