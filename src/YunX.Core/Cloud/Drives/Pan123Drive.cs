using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using YunX.Core.Cloud;

namespace YunX.Core.Cloud.Drives;

/// <summary>
/// 123 云盘适配器。
///
/// <para>移植自原项目 <c>Pan123Api.kt</c>（749 行）。选择它作为首个完整移植对象，原因：
/// <b>分享文件不需要转存</b>，拿 ShareKey + FileID 就能直接换直链，
/// 是 6 个网盘里链路最短、最容易验证的一个。</para>
///
/// <para><b>鉴权体系</b>（原项目依据官方文档 + 抓包实证）：</para>
/// <list type="bullet">
///   <item>登录：<c>user.123pan.cn</c>，无需签名，成功判定是 <c>code == 200</c>（注意不是其它接口的 0）；</item>
///   <item>分享列表：<c>yun.123pan.cn/b/api/share/get</c>，匿名、无需签名；</item>
///   <item>分享取链：<c>www.123865.com/b/api/share/download/info</c>，需登录 + 签名头。</item>
/// </list>
/// </summary>
public sealed partial class Pan123Drive : ICloudDrive
{
    // ================= 常量（与原项目 Pan123Constants 一致） =================

    private const string LoginUrl = "https://user.123pan.cn/api/user/sign_in";
    private const string ShareListUrl = "https://yun.123pan.cn/b/api/share/get";
    private const string ShareDownloadInfoUrl = "https://www.123865.com/b/api/share/download/info";
    private const string DownloadReferer = "https://yun.123pan.cn/";
    private const string DartUa = "Dart/3.12 (dart:io)";

    /// <summary>签名时间偏移：基准时间 + 16 小时（原项目实证）。</summary>
    private const long SignOffsetSeconds = 57_600;
    private const string SignTable = "adefghlmyijnopkqrstubcvwsz";
    private const string SignOs = "web";
    private const string SignVer = "3";

    public string Platform => CloudPlatforms.Pan123;
    public string DisplayName => "123 云盘";

    public LoginDescriptor? Login => new()
    {
        Kind = LoginKind.Password,
        // 123 的网页登录也能拿 token，但账号密码是最直接的方式
        LoginUrl = "https://www.123pan.com/",
        CookieDomains = new[] { "123pan.com", "123865.com" },
        Hint = "使用账号密码登录后获取 authorToken；也可从浏览器 localStorage 复制 token",
    };

    private readonly HttpClient _client;
    private readonly Func<string?> _tokenProvider;

    public Pan123Drive(HttpClient client, Func<string?> tokenProvider)
    {
        _client = client;
        _tokenProvider = tokenProvider;
    }

    // ================= 识别 =================

    /// <summary>识别 123pan.com / 123684.com / 123865.com / 123912.com 各种分享域名。</summary>
    private static readonly string[] Hosts =
    {
        "123pan.com", "123684.com", "123865.com", "123912.com", "123pan.cn",
    };

    public bool CanHandle(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        foreach (var h in Hosts)
            if (input.Contains(h, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>
    /// 分享链接形态：<c>https://www.123pan.com/s/{shareKey}</c>
    /// 也支持 <c>/s/{shareKey}?提取码=xxxx</c> 与短链。
    /// </summary>
    public ShareLink? ParseShareLink(string input)
    {
        var (url, passcode) = ShareTextParser.Parse(input);
        if (url == null) return null;

        var m = ShareKeyPattern().Match(url);
        if (!m.Success) return null;

        // 提取码也可能在 query 里（?pwd=xxxx）
        if (string.IsNullOrEmpty(passcode))
        {
            int q = url.IndexOf('?');
            if (q >= 0)
            {
                var query = HttpUtility.ParseQueryString(url[(q + 1)..]);
                passcode = query["pwd"] ?? query["password"] ?? query["code"];
            }
        }

        return new ShareLink
        {
            Platform = Platform,
            ShareId = m.Groups[1].Value,
            Passcode = passcode,
            RawInput = input,
        };
    }

    // 分享 key：/s/ 后面的一串（字母数字，常见 10~20 位）
    [System.Text.RegularExpressions.GeneratedRegex(@"/s/([A-Za-z0-9_-]{5,40})")]
    private static partial System.Text.RegularExpressions.Regex ShareKeyPattern();

    // ================= 签名 =================

    /// <summary>CRC-32（IEEE）转 8 位小写十六进制。</summary>
    private static string Crc32Hex(string s)
    {
        // .NET 8 起 System.IO.Hashing 提供 Crc32；这里手写 20 行避免额外依赖
        uint crc = 0xFFFFFFFFu;
        foreach (byte b in Encoding.UTF8.GetBytes(s))
        {
            crc ^= b;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320u : crc >> 1;
        }
        crc ^= 0xFFFFFFFFu;
        return crc.ToString("x", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 生成 <c>auth-key</c> / <c>auth-value</c> 签名头。
    ///
    /// <para>算法（原项目抓包逐字还原 + 实时验证）：</para>
    /// <list type="number">
    ///   <item>auth-key = crc32_hex(把 UTC「YYYYMMDDHHmm」(基准+16h) 的每个数字按替换表映射)；</item>
    ///   <item>auth-value = <c>{ts}-{random}-{crc32_hex(ts|random|path|web|3|auth-key)}</c>。</item>
    /// </list>
    /// <para>签名内部固定 OS=web / VER=3，与请求头的 platform / app-version **无关**。</para>
    /// </summary>
    public static (string AuthKey, string AuthValue) MakeSign(string path, long? timestamp = null)
    {
        long ts = timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // 1) auth-key
        var utc = DateTimeOffset.FromUnixTimeSeconds(ts + SignOffsetSeconds);
        string minute = utc.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture);
        var substituted = new StringBuilder(minute.Length);
        foreach (char c in minute)
            substituted.Append(SignTable[c - '0']);
        string authKey = Crc32Hex(substituted.ToString());

        // 2) auth-value
        int random = Random.Shared.Next(0, 10_000_000);
        string data = $"{ts}|{random}|{path}|{SignOs}|{SignVer}|{authKey}";
        string authValue = $"{ts}-{random}-{Crc32Hex(data)}";
        return (authKey, authValue);
    }

    // ================= 登录 =================

    /// <summary>账号密码登录，返回 authorToken；失败抛 <see cref="CloudException"/>。</summary>
    public async Task<string> PasswordLoginAsync(string account, string password,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrEmpty(password))
            throw new CloudException("请输入账号和密码");

        // 账号 trim、密码不 trim（密码里有空格是合法的）
        var body = new JsonObject
        {
            ["passport"] = account.Trim(),
            ["password"] = password,
            ["remember"] = true,
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, LoginUrl);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8,
            "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", DartUa);
        // 缺 Origin / Referer 会被风控拒绝
        req.Headers.TryAddWithoutValidation("Origin", "https://user.123pan.cn");
        req.Headers.TryAddWithoutValidation("Referer", "https://user.123pan.cn/");
        req.Headers.TryAddWithoutValidation("platform", "web");
        req.Headers.TryAddWithoutValidation("app-version", "132");

        using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        // 注意：这个接口成功是 code == 200，不是其它接口的 0
        int code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
        if (code != 200)
        {
            string msg = root.TryGetProperty("message", out var m)
                ? m.GetString() ?? "" : "";
            // 不透传服务端原文，给统一文案
            throw new CloudException($"登录失败：{FriendlyLoginError(msg)}");
        }

        if (root.TryGetProperty("data", out var data) &&
            data.TryGetProperty("token", out var tk) &&
            tk.GetString() is { Length: > 0 } token)
        {
            return token;
        }
        throw new CloudException("登录响应中缺少 token");
    }

    private static string FriendlyLoginError(string serverMessage)
    {
        if (serverMessage.Contains("密码", StringComparison.Ordinal) ||
            serverMessage.Contains("password", StringComparison.OrdinalIgnoreCase))
            return "账号或密码错误";
        if (serverMessage.Contains("频繁", StringComparison.Ordinal) ||
            serverMessage.Contains("frequent", StringComparison.OrdinalIgnoreCase))
            return "操作过于频繁，请稍后再试";
        return "账号或密码错误，或账号被限制";
    }

    // ================= 列目录 =================

    public async Task<CloudListing> ListAsync(ShareLink share, string? parentId,
        string? cursor, CancellationToken ct = default)
    {
        var q = HttpUtility.ParseQueryString(string.Empty);
        q["limit"] = "100";
        q["next"] = cursor ?? "";
        q["orderBy"] = "file_name";
        q["orderDirection"] = "asc";
        q["shareKey"] = share.ShareId;
        q["ParentFileId"] = string.IsNullOrEmpty(parentId) ? "-1" : parentId;
        q["Page"] = "1";
        // 关键：无提取码时**不能**传 SharePwd（传空值会 400 "请输入Next"）
        if (!string.IsNullOrWhiteSpace(share.Passcode)) q["SharePwd"] = share.Passcode;

        string url = $"{ShareListUrl}?{q}";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", DartUa);

        using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;

        int code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
        if (code != 0)
        {
            string msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            if (msg.Contains("密码", StringComparison.Ordinal) ||
                msg.Contains("提取", StringComparison.Ordinal))
                throw new PasscodeRequiredException(
                    string.IsNullOrEmpty(share.Passcode) ? "该分享需要提取码" : "提取码错误");
            if (msg.Contains("失效", StringComparison.Ordinal) ||
                msg.Contains("不存在", StringComparison.Ordinal))
                throw new CloudException("分享已失效或不存在");
            throw new CloudException($"获取文件列表失败：{msg}");
        }

        var listing = new CloudListing();
        if (!root.TryGetProperty("data", out var data)) return listing;

        if (data.TryGetProperty("Expired", out var exp) && exp.GetBoolean())
            throw new CloudException("分享已失效");

        if (data.TryGetProperty("InfoList", out var infoList))
        {
            foreach (var f in infoList.EnumerateArray())
            {
                listing.Files.Add(new CloudFile
                {
                    Id = f.TryGetProperty("FileId", out var fid)
                        ? fid.ValueKind == JsonValueKind.Number ? fid.GetInt64().ToString() : fid.GetString() ?? ""
                        : "",
                    Name = f.TryGetProperty("FileName", out var fn) ? fn.GetString() ?? "" : "",
                    Size = f.TryGetProperty("Size", out var sz) ? sz.GetInt64() : 0,
                    IsDirectory = f.TryGetProperty("Type", out var tp) && tp.GetInt32() == 1,
                    ParentId = parentId ?? "-1",
                    // fidToken 编码了 "S3KeyFlag|Etag"，取直链时要原样带回
                    Token = f.TryGetProperty("Etag", out var et) ? et.GetString() ?? "" : "",
                    ModifyTime = f.TryGetProperty("UpdateAt", out var ua)
                        ? ua.GetString() ?? "" : "",
                });
            }
        }

        // Next == "-1" 表示无下一页；空串表示还有（需要继续翻页）；数字为游标
        if (data.TryGetProperty("Next", out var next))
        {
            string n = next.GetString() ?? "";
            listing.NextCursor = n == "-1" ? null : n;
        }

        return listing;
    }

    // ================= 取直链 =================

    public async Task<DirectLink> GetDirectLinkAsync(ShareLink share, CloudFile file,
        CancellationToken ct = default)
    {
        string? token = _tokenProvider();
        if (string.IsNullOrWhiteSpace(token))
            throw new NotLoggedInException("123 云盘");

        const string path = "/b/api/share/download/info";
        var (authKey, authValue) = MakeSign(path);

        // S3KeyFlag 编码在 fidToken 里；本版列表接口拿的是 Etag，
        // S3KeyFlag 由列表返回的 FileId 与 Size 组合推导 —— 这里按官方流程只用必要字段。
        var body = new JsonObject
        {
            ["ShareKey"] = share.ShareId,
            ["FileID"] = long.TryParse(file.Id, out long id) ? id : 0,
            ["S3KeyFlag"] = "",
            ["Size"] = file.Size,
            ["Etag"] = file.Token,
        };
        if (!string.IsNullOrWhiteSpace(share.Passcode))
            body["SharePwd"] = share.Passcode;

        using var req = new HttpRequestMessage(HttpMethod.Post, ShareDownloadInfoUrl);
        req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8,
            "application/json");
        req.Headers.TryAddWithoutValidation("User-Agent", DartUa);
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        req.Headers.TryAddWithoutValidation("auth-key", authKey);
        req.Headers.TryAddWithoutValidation("auth-value", authValue);
        req.Headers.TryAddWithoutValidation("platform", "web");
        req.Headers.TryAddWithoutValidation("app-version", "3");
        req.Headers.TryAddWithoutValidation("Referer", DownloadReferer);

        using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        int code = root.TryGetProperty("code", out var c) ? c.GetInt32() : -1;
        if (code != 0)
        {
            string msg = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            if (code == 401) throw new NotLoggedInException("123 云盘");
            throw new CloudException($"获取下载直链失败：{msg}");
        }

        if (!root.TryGetProperty("data", out var data))
            throw new CloudException("响应缺少 data");

        string? downloadUrl = data.TryGetProperty("DownloadURL", out var du)
            ? du.GetString() : null;
        if (string.IsNullOrEmpty(downloadUrl))
            throw new CloudException("响应缺少 DownloadURL");

        // download-v2 包装：对 params 做 Base64 解码得到真实 CDN 直链
        if (downloadUrl.Contains("download-v2", StringComparison.OrdinalIgnoreCase))
        {
            var m = V2ParamsPattern().Match(downloadUrl);
            if (m.Success)
            {
                try
                {
                    string decoded = Encoding.UTF8.GetString(
                        Convert.FromBase64String(Uri.UnescapeDataString(m.Groups[1].Value)));
                    if (decoded.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        downloadUrl = decoded;
                }
                catch
                {
                    // 解码失败就用原链接
                }
            }
        }

        return new DirectLink
        {
            Url = downloadUrl,
            FileName = file.Name,
            Size = file.Size,
            Headers = new Dictionary<string, string>
            {
                // 123 的 CDN 校验 Referer，缺了 403
                ["Referer"] = DownloadReferer,
                ["User-Agent"] = DartUa,
            },
        };
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"params=([^&]+)")]
    private static partial System.Text.RegularExpressions.Regex V2ParamsPattern();

    /// <summary>123 分享文件无需转存，无临时文件要清理。</summary>
    public Task CleanupAsync(ShareLink share, CancellationToken ct = default)
        => Task.CompletedTask;
}
