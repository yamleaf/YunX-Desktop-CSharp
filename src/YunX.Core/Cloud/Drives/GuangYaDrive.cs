using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using YunX.Core.Cloud;

namespace YunX.Core.Cloud.Drives;

/// <summary>
/// 光鸭云盘适配器。
///
/// <para>移植自原项目 <c>GuangYaApi.kt</c>（827 行）。这是所有平台里协议最规整的一个：
/// 全部业务接口都是 <c>POST + JSON</c>，无需 Cookie，靠四个请求头做设备识别：
/// <c>dt: 4</c>、<c>traceparent</c>（W3C 链路追踪格式）、<c>did</c>（32 位设备标识）、
/// <c>Origin/Referer</c>。</para>
///
/// <para><b>链路</b>：</para>
/// <list type="number">
///   <item><c>get_share_access_token</c>：提取码换 <c>accessToken</c>（分享级令牌，不是账号 token）；</item>
///   <item><c>get_share_page_files_list</c>：游标分页列目录，根目录 <c>parentId</c> 是<b>空串</b>；</item>
///   <item><c>get_share_download_url</c>：换签名直链，直接可用，不需要二次跳转。</item>
/// </list>
///
/// <para><b>字段名很不统一</b>：直链可能是 <c>signedURL</c>/<c>signedUrl</c>/<c>downloadUrl</c>/
/// <c>url</c>/<c>cdnUrl</c>，列表数组可能是 <c>list</c>/<c>items</c>/<c>files</c>/<c>records</c>/<c>content</c>。
/// 原项目为此写了一套「多候选」取值，这里照抄 —— 不照抄的话服务端一改字段名就全挂。</para>
///
/// <para><b>错误码</b>（全是字符串）：<c>209</c> 提取码错；<c>200/201/202</c> 分享失效；
/// <c>207</c> 分享者关闭了免登录下载（需登录转存）。</para>
/// </summary>
public sealed partial class GuangYaDrive : ICloudDrive
{
    private const string ApiBase = "https://api.guangyapan.com";
    private const string WebSite = "https://www.guangyapan.com";
    private const string WebUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
        "(KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private const string ShareAccessTokenUrl = $"{ApiBase}/userres/v1/get_share_access_token";
    private const string ShareSummaryUrl = $"{ApiBase}/userres/v1/get_share_summary";
    private const string ShareFilesUrl = $"{ApiBase}/userres/v1/get_share_page_files_list";
    private const string ShareDownloadUrl = $"{ApiBase}/userres/v1/get_share_download_url";

    private const int PageSize = 100;

    /// <summary>根目录 parentId 是空串，不是 "0"。</summary>
    private const string RootParentId = "";

    
    #if NET48
    // 源生成器在 net48 上不可用，改用惰性初始化的运行时 Regex
    private static Regex? _shareIdPatternCache;
    private static Regex ShareIdPattern() =>
        _shareIdPatternCache ??= new Regex(@"guangyapan\.com/s/([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    #else
    [GeneratedRegex(@"guangyapan\.com/s/([A-Za-z0-9_-]+)", RegexOptions.IgnoreCase)]
        private static partial Regex ShareIdPattern();
    #endif

    
    #if NET48
    // 源生成器在 net48 上不可用，改用惰性初始化的运行时 Regex
    private static Regex? _pwdInUrlPatternCache;
    private static Regex PwdInUrlPattern() =>
        _pwdInUrlPatternCache ??= new Regex(@"[?&]pwd=([A-Za-z0-9]+)", RegexOptions.Compiled);
    #else
    [GeneratedRegex(@"[?&]pwd=([A-Za-z0-9]+)")]
        private static partial Regex PwdInUrlPattern();
    #endif

    private readonly HttpClient _client;
    private readonly Func<string?> _cookieProvider;

    /// <summary>匿名设备的 32 位标识，进程内固定即可。</summary>
    private readonly string _deviceId = NewDeviceId();

    /// <summary>分享会话缓存：shareId → (accessToken, 标题)。</summary>
    private readonly ConcurrentDictionary<string, (string AccessToken, string Title)> _sessions = new();

    public GuangYaDrive(HttpClient client, Func<string?> cookieProvider)
    {
        _client = client;
        _cookieProvider = cookieProvider;
    }

    public string Platform => CloudPlatforms.GuangYa;
    public string DisplayName => "光鸭云盘";

    public LoginDescriptor? Login => new()
    {
        Kind = LoginKind.Browser,
        LoginUrl = $"{WebSite}/",
        CookieDomains = new[] { "guangyapan.com" },
        Hint = "大部分分享可匿名下载；分享者关闭「免登录下载」时才需要登录",
    };

    // ================= 识别 =================

    public bool CanHandle(string input) =>
        !string.IsNullOrWhiteSpace(input) &&
        input.Contains("guangyapan.com", StringComparison.OrdinalIgnoreCase);

    public ShareLink? ParseShareLink(string input)
    {
        var (url, passcode) = ShareTextParser.Parse(input);
        string text = url ?? input;

        var m = ShareIdPattern().Match(text);
        if (!m.Success) return null;

        string? pwd = PwdInUrlPattern().Match(text) is { Success: true } pm ? pm.Groups[1].Value : passcode;

        return new ShareLink
        {
            Platform = Platform,
            ShareId = m.Groups[1].Value,
            Passcode = pwd,
            RawInput = input,
        };
    }

    // ================= 列目录 =================

    public async Task<CloudListing> ListAsync(ShareLink share, string? parentId, string? cursor,
        CancellationToken ct = default)
    {
        var (token, title) = await GetSessionAsync(share, ct).ConfigureAwait(false);
        string dir = parentId ?? RootParentId;

        var all = new List<CloudFile>();
        string? next = cursor;
        int guard = 0;

        while (guard++ < 1000)
        {
            ct.ThrowIfCancellationRequested();

            var body = new JsonObject
            {
                ["parentId"] = dir,
                ["pageSize"] = PageSize,
                ["orderBy"] = 0,
                ["sortType"] = 0,
                ["accessToken"] = token,
            };
            if (!string.IsNullOrEmpty(next)) body["cursor"] = next;

            var json = await PostJsonAsync(ShareFilesUrl, body, ct).ConfigureAwait(false);
            CheckBusiness(json, "获取分享文件列表失败", out _);

            var data = (json?["data"] as JsonObject) ?? json;
            var list = ListArray(data);
            if (list == null || list.Count == 0) break;

            foreach (var node in list)
            {
                if (node is not JsonObject item) continue;
                var file = ParseFile(item);
                if (file != null) all.Add(file);
            }

            bool hasMore = (bool?)data?["hasMore"] == true;
            string cur = Str(data, "cursor");
            if (!hasMore || cur.Length == 0) break;
            next = cur;
        }

        return new CloudListing { Title = title, Files = all };
    }

    // ================= 取直链 =================

    public async Task<DirectLink> GetDirectLinkAsync(ShareLink share, CloudFile file,
        CancellationToken ct = default)
    {
        var (token, _) = await GetSessionAsync(share, ct).ConfigureAwait(false);

        var body = new JsonObject
        {
            ["fileId"] = file.Id,
            ["accessToken"] = token,
        };

        var json = await PostJsonAsync(ShareDownloadUrl, body, ct).ConfigureAwait(false);

        // 207 = 分享者关闭了免登录下载，必须登录并转存
        string code = Str(json, "code");
        if (code == "207")
            throw new NotLoggedInException("光鸭云盘（该分享已关闭免登录下载）");

        CheckBusiness(json, "获取下载地址失败", out _);

        var data = (json?["data"] as JsonObject) ?? json;
        string url = FirstNonBlank(
            Str(data, "signedURL"), Str(data, "signedUrl"), Str(data, "downloadUrl"),
            Str(data, "download_url"), Str(data, "url"), Str(data, "cdnUrl"));

        if (url.Length == 0)
            throw new CloudException("光鸭未返回有效下载地址，请确认该分享的下载权限");

        long size = long.TryParse(Str(data, "size"), out long s) ? s : file.Size;

        return new DirectLink
        {
            Url = url,
            FileName = file.Name,
            Size = size,
            Headers = new Dictionary<string, string>
            {
                // 直链是签名 CDN 地址，只要 UA + Referer；不要带 Cookie / Authorization
                ["User-Agent"] = WebUa,
                ["Referer"] = $"{WebSite}/",
            },
        };
    }

    public Task CleanupAsync(ShareLink share, CancellationToken ct = default) => Task.CompletedTask;

    // ================= 会话 =================

    /// <summary>
    /// 提取码换分享访问令牌 + 取标题。
    /// 令牌是<b>分享级</b>的，不是账号 accessToken，所以匿名也能拿到。
    /// </summary>
    private async Task<(string AccessToken, string Title)> GetSessionAsync(ShareLink share,
        CancellationToken ct)
    {
        string key = $"{share.ShareId}|{share.Passcode}";
        if (_sessions.TryGetValue(key, out var cached)) return cached;

        var tokenBody = new JsonObject
        {
            ["shareId"] = share.ShareId,
            // 无提取码也要传空串字段，不能省略
            ["code"] = share.Passcode ?? "",
        };

        var tokenJson = await PostJsonAsync(ShareAccessTokenUrl, tokenBody, ct).ConfigureAwait(false);
        CheckBusiness(tokenJson, "获取分享访问令牌失败", out string code);

        var data = tokenJson?["data"] as JsonObject
                   ?? throw new CloudException("光鸭分享链接无效");

        string token = FirstNonBlank(Str(data, "accessToken"), Str(data, "access_token"));
        if (token.Length == 0) throw new CloudException("光鸭分享链接无效");

        // 标题拿不到不影响下载，失败就退回占位
        string title = "光鸭分享";
        try
        {
            var sumJson = await PostJsonAsync(ShareSummaryUrl,
                new JsonObject { ["shareId"] = share.ShareId }, ct).ConfigureAwait(false);
            if ((bool?)sumJson?["success"] != false)
            {
                var sd = (sumJson?["data"] as JsonObject) ?? sumJson;
                string t = FirstNonBlank(Str(sd, "title"), Str(sd, "shareName"));
                if (t.Length > 0) title = t;
            }
        }
        catch
        {
            // 忽略
        }

        var result = (token, title);
        _sessions[key] = result;
        return result;
    }

    // ================= HTTP =================

    private async Task<JsonNode?> PostJsonAsync(string url, JsonObject body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        // 这四个头少一个都可能被拒：dt/traceparent/did 是设备识别，Origin/Referer 是来源校验
        req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
        req.Headers.TryAddWithoutValidation("User-Agent", WebUa);
        req.Headers.TryAddWithoutValidation("Origin", WebSite);
        req.Headers.TryAddWithoutValidation("Referer", $"{WebSite}/");
        req.Headers.TryAddWithoutValidation("dt", "4");
        req.Headers.TryAddWithoutValidation("traceparent", NewTraceparent());
        req.Headers.TryAddWithoutValidation("did", _deviceId);

        using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);

        // 光鸭同样「先看 body 再判状态码」：业务失败也会带 200
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (text.Trim().Length == 0)
            throw new CloudException($"光鸭响应为空（HTTP {(int)resp.StatusCode}）");

        try
        {
            return JsonNode.Parse(text);
        }
        catch
        {
            throw new CloudException($"光鸭响应解析失败（HTTP {(int)resp.StatusCode}）");
        }
    }

    /// <summary>W3C traceparent：<c>00-&lt;32hex&gt;-&lt;16hex&gt;-01</c>，每次请求重新生成。</summary>
    private static string NewTraceparent()
    {
        static string Hex(int n)
        {
#if NET48
            // net48 没有 string.Create<TState>(int, TState, SpanAction)，
            // 用 StringBuilder 等价实现（这里每次只生成 32/16 个字符，开销可忽略）。
            const string chars = "0123456789abcdef";
            var sb = new System.Text.StringBuilder(n);
            for (int i = 0; i < n; i++) sb.Append(chars[Rng48.GetInt32(16)]);
            return sb.ToString();
#else
            return string.Create(n, 0, (span, _) =>
            {
                const string chars = "0123456789abcdef";
                for (int i = 0; i < span.Length; i++) span[i] = chars[Rng48.GetInt32(16)];
            });
#endif
        }
        return $"00-{Hex(32)}-{Hex(16)}-01";
    }

    private static string NewDeviceId() =>
        Helpers.ToHexString(Rng48.GetBytes(16)).ToLowerInvariant();

    // ================= 解析 =================

    /// <summary>
    /// 列表数组可能有 5 种字段名。原项目实测不同接口/版本不统一，必须逐个试。
    /// </summary>
    private static JsonArray? ListArray(JsonNode? node)
    {
        if (node is not JsonObject data) return null;
        foreach (var key in new[] { "list", "items", "files", "records", "content" })
        {
            if (data[key] is JsonArray arr) return arr;
        }
        return null;
    }

    private static CloudFile? ParseFile(JsonObject item)
    {
        string id = FirstNonBlank(
            Str(item, "fileId"), Str(item, "file_id"), Str(item, "id"),
            Str(item, "resId"), Str(item, "resourceId"));
        if (id.Length == 0) return null;

        string name = FirstNonBlank(
            Str(item, "fileName"), Str(item, "name"), Str(item, "filename"),
            Str(item, "dirName"), Str(item, "title"));

        long size = long.TryParse(Str(item, "fileSize"), out long s1) ? s1
            : long.TryParse(Str(item, "size"), out long s2) ? s2
            : long.TryParse(Str(item, "file_size"), out long s3) ? s3
            : 0;

        return new CloudFile
        {
            Id = id,
            Name = name.Length > 0 ? name : id,
            Size = size,
            IsDirectory = IsDirectory(item),
            ParentId = FirstNonBlank(Str(item, "parentId"), Str(item, "parentFileId"),
                Str(item, "parent_id")),
            ModifyTime = FirstNonBlank(Str(item, "updatedAt"), Str(item, "updateAt"),
                Str(item, "updated_at"), Str(item, "updateTime"), Str(item, "mtime"),
                Str(item, "utime"), Str(item, "createdAt"), Str(item, "createTime"),
                Str(item, "ctime")),
        };
    }

    /// <summary>
    /// 判断是否目录。光鸭的判定方式有六七种混用，按优先级逐个试：
    /// <c>resType</c>（"2" 目录 / "1" 文件）→ 各种布尔字段 → 有 dirName →
    /// type 字段 → 「名字没有扩展名 且 dirType=="1"」的兜底。
    /// </summary>
    private static bool IsDirectory(JsonObject item)
    {
        string resType = Str(item, "resType");
        if (resType == "2") return true;
        if (resType == "1") return false;

        foreach (var key in new[] { "isDir", "isdir", "dir", "isFolder", "folder" })
        {
            var v = item[key];
            if (v == null) continue;
            try
            {
                return v.GetValueKind() == System.Text.Json.JsonValueKind.True ||
                       (v.GetValueKind() == System.Text.Json.JsonValueKind.String &&
                        bool.TryParse(v.GetValue<string>(), out bool b) && b);
            }
            catch
            {
                // 继续试下一个
            }
        }

        if (Str(item, "dirName").Length > 0) return true;

        string type = FirstNonBlank(Str(item, "type"), Str(item, "fileType")).ToLowerInvariant();
        if (type is "folder" or "dir" or "directory" or "0") return true;

        string name = FirstNonBlank(Str(item, "fileName"), Str(item, "name"));
        return name.Length > 0 && !name.Contains('.') && Str(item, "dirType") == "1";
    }

    // ================= 业务码 =================

    /// <summary>
    /// 业务码判定。顺序必须照抄：先看 <c>success</c>（默认 true），再看 <c>error</c> 是否非空，
    /// 最后看 <c>code</c> 是否为空或 "0"。code 一律按字符串比较。
    /// </summary>
    private static void CheckBusiness(JsonNode? json, string fallback, out string code)
    {
        code = Str(json, "code");
        if (json == null) throw new CloudException($"{fallback}（响应为空）");

        bool success = (bool?)json["success"] ?? true;
        bool hasError = Str(json, "error").Length > 0;
        bool codeOk = code.Length == 0 || code == "0";

        if (success && !hasError && codeOk) return;

        string serverMsg = FirstNonBlank(Str(json, "msg"), Str(json, "message"),
            Str(json, "error_description"));

        string? mapped = code switch
        {
            "209" => "光鸭分享提取码错误或缺失",
            "200" or "201" or "202" => "光鸭分享已取消、过期或不可访问",
            "205" or "206" or "207" or "504" => "光鸭分享下载受限，请在官网确认该分享的下载权限",
            "157" => "光鸭网盘空间不足",
            "429" => "光鸭请求过于频繁，请稍后重试",
            _ => null,
        };

        string message = mapped ?? (serverMsg.Length > 0 ? serverMsg : $"{fallback}（code={code}）");

        if (code == "209") throw new PasscodeRequiredException(message);
        throw new CloudException(message);
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
