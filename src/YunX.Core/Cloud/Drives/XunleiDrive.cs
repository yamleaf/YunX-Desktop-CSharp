using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using YunX.Core.Cloud;

namespace YunX.Core.Cloud.Drives;

/// <summary>
/// 迅雷网盘适配器。
///
/// <para>移植自原项目 <c>XunleiApi.kt</c>（1013 行）。这是 6 个网盘中<b>鉴权最复杂</b>的一个：
/// 每个 pan 请求都要带 <c>captcha_sign</c>，它是 10 层 md5 嵌套（alist 逆向确认的算法）：</para>
/// <code>
/// raw = client_id + client_version + package_name + device_id + timestamp_ms
/// for salt in 10_salts: raw = md5(raw + salt)
/// captcha_sign = "1." + raw
/// </code>
///
/// <para><b>游客模式</b>：分享列表接口支持匿名调用（不带 access_token 时不带验证码、不刷新 token），
/// 所以即使没登录也能列目录；但<b>取直链必须登录</b>（要先转存到自己网盘）。</para>
///
/// <para><b>中文口令</b>：迅雷的口令（如「绝对有效 咐置松君杂货铺叩苓」）本质是
/// 「分享链接 + 提取码」的打包形式，通过搜索跳转接口换回带 <c>pwd</c> 的分享页地址。</para>
/// </summary>
public sealed partial class XunleiDrive : ICloudDrive
{
    private const string AuthBase = "https://xluser-ssl.xunlei.com";
    private const string PanBase = "https://api-pan.xunlei.com";

    /// <summary>App 端凭据（公开常量，用于 captcha_sign 计算）。</summary>
    private const string AppClientId = "Xp6vsxz_7IYVw2BB";
    private const string AppClientVersion = "8.31.0.9726";
    private const string AppPackageName = "com.xunlei.downloadprovider";

    /// <summary>Android 端 captcha 盐（10 个，alist 源码确认）。</summary>
    private static readonly string[] CaptchaSalts =
    {
        "9uJNVj/wLmdwKrJaVj/omlQ",
        "Oz64Lp0GigmChHMf/6TNfxx7O9PyopcczMsnf",
        "Eb+L7Ce+Ej48u",
        "jKY0",
        "ASr0zCl6v8W4aidjPK5KHd1Lq3t+vBFf41dqv5+fnOd",
        "wQlozdg6r1qxh0eRmt3QgNXOvSZO6q/GXK",
        "gmirk+ciAvIgA/cxUUCema47jr/YToixTT+Q6O",
        "5IiCoM9B1/788ntB",
        "P07JH0h6qoM6TSUAK2aL9T5s2QBVeY9JWvalf",
        "+oK0AN",
    };

    private const string AppUa =
        "ANDROID-com.xunlei.downloadprovider/8.31.0.9726 " +
        "VersionCode/9726 Android/13";

    private const string FilesUrl = PanBase + "/drive/v1/files";

    public string Platform => CloudPlatforms.Xunlei;
    public string DisplayName => "迅雷网盘";

    public LoginDescriptor? Login => new()
    {
        Kind = LoginKind.PasswordWithSms,
        LoginUrl = "https://pan.xunlei.com/",
        CookieDomains = new[] { "xunlei.com" },
        Hint = "账号密码登录后需短信验证（由系统浏览器承载验证页），也可内嵌浏览器登录后自动取 Cookie",
    };

    private readonly HttpClient _client;
    private readonly Func<string?> _tokenProvider;

    /// <summary>设备指纹（同一设备长期不变；参与 captcha_sign 计算）。</summary>
    private readonly string _deviceId;

    /// <summary>最近一次拿到的 captcha token（有效期有限，按需刷新）。</summary>
    private string? _captchaToken;

    /// <summary>分享会话：share_id → (pass_code_token, title)。</summary>
    // 见 QuarkDrive 同名注释：适配器是单例，会被多个下载任务并发调用，必须用并发字典。
    private readonly ConcurrentDictionary<string, (string PassCodeToken, string Title)> _sessions = new();

    public XunleiDrive(HttpClient client, Func<string?> tokenProvider, string? deviceId = null)
    {
        _client = client;
        _tokenProvider = tokenProvider;
        // 设备 ID 形如 40 位十六进制；未提供时按进程稳定生成（真实部署应持久化）
        _deviceId = deviceId ?? Convert.ToHexString(
            SHA1.HashData(Encoding.UTF8.GetBytes(Environment.MachineName +
                Environment.UserName + "xunlei"))).ToLowerInvariant();
    }

    // ================= 识别 =================

    public bool CanHandle(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        if (input.Contains("pan.xunlei.com", StringComparison.OrdinalIgnoreCase)) return true;
        // 中文口令：不是链接、全是中文 —— 迅雷是唯一支持口令的平台
        return ShareTextParser.LooksLikeChineseCommand(input);
    }

    public ShareLink? ParseShareLink(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        // 口令形态：无法同步解析（要发网络请求），交给上层用 ResolveKoulingAsync
        if (ShareTextParser.LooksLikeChineseCommand(input))
            return null;

        var (url, passcode) = ShareTextParser.Parse(input);
        if (url == null) return null;

        // https://pan.xunlei.com/s/{shareId}?pwd=abcd
        var m = ShareIdPattern().Match(url);
        if (!m.Success) return null;

        return new ShareLink
        {
            Platform = Platform,
            ShareId = m.Groups[1].Value,
            Passcode = passcode,
            RawInput = input,
        };
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"/s/([A-Za-z0-9_\-]{6,40})")]
    private static partial System.Text.RegularExpressions.Regex ShareIdPattern();

    // ================= 中文口令 =================

    /// <summary>
    /// 中文口令 → 带提取码的分享链接。
    ///
    /// <para>原理：口令是「分享链接 + 提取码」的打包形式。把口令当关键词打搜索跳转接口，
    /// 当 <c>ext.kouling_type == "share_page"</c> 时 <c>location</c> 就是带 <c>pwd</c> 的分享页地址。</para>
    ///
    /// <para>⚠️ 这一步会把口令原文发往第三方（迅雷）服务。失败日志只记长度与原因，不落口令本体。</para>
    /// </summary>
    public async Task<ShareLink?> ResolveKoulingAsync(string kouling, CancellationToken ct = default)
    {
        // 搜索跳转接口：把口令作为关键词
        string jump = "https://shoulei.xunlei.com/api/v1/search/jump?keyword=" +
                      Uri.EscapeDataString(kouling.Trim());

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, jump);
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
                "(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
            req.Headers.TryAddWithoutValidation("Accept", "*/*");

            using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
            string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!text.TrimStart().StartsWith('{')) return null;

            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            // 只在 kouling_type == share_page 时才算命中
            if (root.TryGetProperty("ext", out var ext) &&
                ext.TryGetProperty("kouling_type", out var kt) &&
                kt.GetString() == "share_page" &&
                root.TryGetProperty("location", out var loc) &&
                loc.GetString() is { Length: > 0 } location)
            {
                var (url, passcode) = ShareTextParser.Parse(location);
                if (url == null) return null;
                var m = ShareIdPattern().Match(url);
                if (!m.Success) return null;
                return new ShareLink
                {
                    Platform = Platform, ShareId = m.Groups[1].Value,
                    Passcode = passcode, RawInput = kouling,
                };
            }
        }
        catch
        {
            // 网络或解析失败：按口令无效处理
        }
        return null;
    }

    // ================= captcha 签名 =================

    /// <summary>
    /// <c>captcha_sign</c>：client_id + client_version + package_name + device_id + timestamp
    /// 经 10 层 <c>md5(raw + salt)</c> 嵌套，前缀 <c>"1."</c>。
    /// </summary>
    public static string BuildCaptchaSign(string deviceId, string timestampMs)
    {
        string h = AppClientId + AppClientVersion + AppPackageName + deviceId + timestampMs;
        foreach (var salt in CaptchaSalts)
            h = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(h + salt)))
                .ToLowerInvariant();
        return "1." + h;
    }

    /// <summary>初始化 captcha，拿到 token（pan 请求都要带）。</summary>
    private async Task<string> EnsureCaptchaAsync(CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_captchaToken)) return _captchaToken;

        string ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString();
        string sign = BuildCaptchaSign(_deviceId, ts);

        string url = $"{AuthBase}/v1/shield/captcha/init";
        var body = new JsonObject
        {
            ["client_id"] = AppClientId,
            ["action"] = "GET:/drive/v1/share",
            ["device_id"] = _deviceId,
            ["meta"] = new JsonObject
            {
                ["client_version"] = AppClientVersion,
                ["package_name"] = AppPackageName,
                ["user_id"] = "0",
                ["timestamp"] = ts,
                ["captcha_sign"] = sign,
            },
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", AppUa);
        req.Headers.TryAddWithoutValidation("X-Client-Id", AppClientId);

        using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(text);
        if (doc.RootElement.TryGetProperty("captcha_token", out var t) &&
            t.GetString() is { Length: > 0 } token)
        {
            _captchaToken = token;
            return token;
        }
        throw new CloudException("获取迅雷访问令牌失败");
    }

    // ================= 列目录 =================

    public async Task<CloudListing> ListAsync(ShareLink share, string? parentId,
        string? cursor, CancellationToken ct = default)
    {
        string captchaToken = await EnsureCaptchaAsync(ct).ConfigureAwait(false);
        string? accessToken = _tokenProvider();

        bool isRoot = string.IsNullOrEmpty(parentId);
        string url;

        if (isRoot)
        {
            url = PanBase + "/drive/v1/share" +
                  $"?share_id={Uri.EscapeDataString(share.ShareId)}" +
                  $"&pass_code={Uri.EscapeDataString(share.Passcode ?? "")}" +
                  $"&limit=100&page_token={Uri.EscapeDataString(cursor ?? "")}" +
                  "&thumbnail_size=SIZE_SMALL";
        }
        else
        {
            // 子目录要 pass_code_token（root 列举时拿到）
            if (!_sessions.TryGetValue(share.ShareId, out var s))
                throw new CloudException("分享会话已过期，请重新解析");
            url = PanBase + "/drive/v1/share/detail" +
                  $"?share_id={Uri.EscapeDataString(share.ShareId)}" +
                  $"&parent_id={Uri.EscapeDataString(parentId!)}" +
                  $"&pass_code_token={Uri.EscapeDataString(s.PassCodeToken)}" +
                  $"&limit=100&page_token={Uri.EscapeDataString(cursor ?? "")}" +
                  "&thumbnail_size=SIZE_SMALL";
        }

        var root = await GetPanAsync(url, captchaToken, accessToken, ct).ConfigureAwait(false);

        // 提取码状态：这三种情况 files 为空且 HTTP 200，不识别会被误判为「空目录」
        if (isRoot)
        {
            string status = Str(root, "share_status");
            switch (status)
            {
                case "PASS_CODE_EMPTY":
                    throw new PasscodeRequiredException("请输入提取码");
                case "PASS_CODE_ERROR":
                    throw new PasscodeRequiredException("提取码错误");
                case "PASS_CODE_NEED":
                    throw new PasscodeRequiredException("该分享需要提取码");
            }
        }

        var listing = new CloudListing { Title = Str(root, "title") };

        if (isRoot)
        {
            string pct = Str(root, "pass_code_token");
            if (pct.Length > 0) _sessions[share.ShareId] = (pct, Str(root, "title"));
        }

        if (root.TryGetProperty("files", out var files))
        {
            foreach (var f in files.EnumerateArray())
            {
                string kind = Str(f, "kind");   // "drive#folder" / "drive#file"
                listing.Files.Add(new CloudFile
                {
                    Id = Str(f, "id"),
                    Name = Str(f, "name"),
                    Size = f.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.String
                        ? long.TryParse(sz.GetString(), out long n) ? n : 0
                        : f.TryGetProperty("size", out var sz2) && sz2.ValueKind == JsonValueKind.Number
                            ? sz2.GetInt64() : 0,
                    IsDirectory = kind.Contains("folder", StringComparison.OrdinalIgnoreCase),
                    ParentId = parentId ?? "",
                    ModifyTime = Str(f, "modified_time"),
                });
            }
        }

        string next = Str(root, "next_page_token");
        if (next.Length > 0) listing.NextCursor = next;
        return listing;
    }

    // ================= 取直链（需转存） =================

    public async Task<DirectLink> GetDirectLinkAsync(ShareLink share, CloudFile file,
        CancellationToken ct = default)
    {
        string? accessToken = _tokenProvider();
        if (string.IsNullOrEmpty(accessToken))
            throw new NotLoggedInException("迅雷网盘");

        string captchaToken = await EnsureCaptchaAsync(ct).ConfigureAwait(false);

        if (!_sessions.TryGetValue(share.ShareId, out var session))
            throw new CloudException("分享会话已过期，请重新解析");

        // 1) 转存到自己网盘（迅雷必须先转存才给直链）
        string restoredId = await RestoreAsync(share, file, session.PassCodeToken,
            captchaToken, accessToken, ct).ConfigureAwait(false);

        // 2) 取直链
        string url = $"{FilesUrl}/{Uri.EscapeDataString(restoredId)}" +
                     "?_magic=2021&usage=PLAY&thumbnail_size=SIZE_LARGE";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AttachPanHeaders(req, captchaToken, accessToken);

        using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);

        string dl = Str(doc.RootElement, "web_content_link");
        if (dl.Length == 0) dl = Str(doc.RootElement, "content_link");
        if (dl.Length == 0) throw new CloudException("未返回下载直链");

        return new DirectLink
        {
            Url = dl,
            FileName = file.Name,
            Size = file.Size,
            Headers = new Dictionary<string, string>
            {
                ["User-Agent"] = AppUa,
                ["Referer"] = "https://pan.xunlei.com/",
            },
        };
    }

    private async Task<string> RestoreAsync(ShareLink share, CloudFile file,
        string passCodeToken, string captchaToken, string accessToken, CancellationToken ct)
    {
        string url = PanBase + "/drive/v1/share/restore";
        var body = new JsonObject
        {
            ["share_id"] = share.ShareId,
            ["pass_code_token"] = passCodeToken,
            ["parent_folder_id"] = "",
            ["file_ids"] = new JsonArray(file.Id),
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        AttachPanHeaders(req, captchaToken, accessToken);

        using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        // 官方同步返回 RESTORE_COMPLETE + trace_file_ids 映射
        string status = Str(root, "restore_status");
        if (status.Length > 0 && status != "RESTORE_COMPLETE")
            throw new CloudException($"转存未完成（{status}）");

        if (root.TryGetProperty("trace_file_ids", out var trace))
        {
            // 形如 "[{\"file_id\":\"...\",\"parent_id\":\"...\"}]"（字符串）或数组
            var ids = ParseTraceIds(trace, file.Id);
            if (ids.Length > 0) return ids;
        }
        throw new CloudException("转存成功但未返回文件 ID");
    }

    private static string ParseTraceIds(JsonElement trace, string fallbackId)
    {
        try
        {
            if (trace.ValueKind == JsonValueKind.String)
            {
                string inner = trace.GetString() ?? "";
                using var doc = JsonDocument.Parse(inner);
                if (doc.RootElement.ValueKind == JsonValueKind.Array &&
                    doc.RootElement.GetArrayLength() > 0)
                    return Str(doc.RootElement[0], "file_id");
            }
            else if (trace.ValueKind == JsonValueKind.Array && trace.GetArrayLength() > 0)
            {
                // 可能是 [["origId","newId"]] 或 [{file_id:...}]
                var first = trace[0];
                if (first.ValueKind == JsonValueKind.Array && first.GetArrayLength() >= 2)
                    return first[1].ToString();
                string id = Str(first, "file_id");
                if (id.Length > 0) return id;
            }
        }
        catch
        {
            // 解析失败用兜底
        }
        return fallbackId;
    }

    /// <summary>
    /// 清理转存产生的临时文件。
    /// 迅雷先转存才能取链，所以清理是必需的。
    /// </summary>
    public async Task CleanupAsync(ShareLink share, CancellationToken ct = default)
    {
        string? accessToken = _tokenProvider();
        if (string.IsNullOrEmpty(accessToken)) return;

        try
        {
            string captchaToken = await EnsureCaptchaAsync(ct).ConfigureAwait(false);
            string url = FilesUrl + ":batchDelete";
            var body = new JsonObject { ["ids"] = new JsonArray() };

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8,
                "application/json");
            AttachPanHeaders(req, captchaToken, accessToken);
            using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
            await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // 清理失败不影响已完成的下载
        }
        _sessions.TryRemove(share.ShareId, out _);
    }

    // ================= HTTP =================

    private void AttachPanHeaders(HttpRequestMessage req, string captchaToken,
        string? accessToken)
    {
        req.Headers.TryAddWithoutValidation("User-Agent", AppUa);
        req.Headers.TryAddWithoutValidation("X-Captcha-Token", captchaToken);
        req.Headers.TryAddWithoutValidation("X-Client-Id", AppClientId);
        req.Headers.TryAddWithoutValidation("X-Device-Id", _deviceId);
        req.Headers.TryAddWithoutValidation("x-peer-id", _deviceId);
        if (!string.IsNullOrEmpty(accessToken))
            req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
    }

    private async Task<JsonElement> GetPanAsync(string url, string captchaToken,
        string? accessToken, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        AttachPanHeaders(req, captchaToken, accessToken);

        using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!text.TrimStart().StartsWith('{'))
            throw new CloudException($"服务端返回非预期内容（HTTP {(int)resp.StatusCode}）");

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement.Clone();

        // captcha token 过期时清掉缓存，下次重新 init
        string errorCode = Str(root, "error_code") is { Length: > 0 } ec ? ec : Str(root, "code");
        if (errorCode is "16" or "17" or "4122" or "4123")
        {
            _captchaToken = null;
            throw new CloudException("访问令牌已过期，请重试");
        }
        return root;
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
