using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using YunX.Core.Cloud;

namespace YunX.Core.Cloud.Drives;

/// <summary>
/// 139 网盘（和彩云）适配器。
///
/// <para>移植自原项目 <c>C139Api.kt</c>（749 行）。这条链路的特点是<b>分享接口全部 AES-CBC 加密</b>：
/// 请求体与响应体都是 <c>base64(IV(16B) ‖ AES_CBC(明文))</c>，密钥固定。</para>
///
/// <para><b>两个容易踩的点</b>（原项目都踩过）：</para>
/// <list type="number">
///   <item><b>子文件夹在 <c>caLst</c>，文件在 <c>coLst</c></b>，且 coLst 里
///         <c>coType==2</c> 也可能是文件夹。只读 coLst 会让「顶层只挂子文件夹」的分享显示为空；</item>
///   <item><b>列目录是匿名调用</b>：不带 Authorization、不带 mcloud-* 头、body 里 account 固定空串；
///         带错会返回 9530。但<b>取直链</b>要加密 + 签名。</item>
/// </list>
/// </summary>
public sealed partial class C139Drive : ICloudDrive
{
    private const string ShareBase = "https://share-kd-njs.yun.139.com";
    private const string ListUrl = ShareBase + "/yun-share/richlifeApp/devapp/IOutLink/getOutLinkInfoV6";
    private const string LinkUrl = ShareBase + "/yun-share/richlifeApp/devapp/IOutLink/dlFromOutLinkV3";

    /// <summary>分享接口的固定 AES 密钥（公开常量，非用户凭证）。</summary>
    private const string ShareAesKey = "PVGDwmcvfs1uV3d1";

    public string Platform => CloudPlatforms.C139;
    public string DisplayName => "139 网盘";

    public LoginDescriptor? Login => new()
    {
        Kind = LoginKind.Browser,
        LoginUrl = "https://yun.139.com/m/#/login",
        CookieDomains = new[] { "yun.139.com", "mail.10086.cn" },
        Hint = "在内嵌浏览器里登录 139 网盘后会自动保存 Cookie",
    };

    private readonly HttpClient _client;
    private readonly Func<string?> _cookieProvider;

    public C139Drive(HttpClient client, Func<string?> cookieProvider)
    {
        _client = client;
        _cookieProvider = cookieProvider;
    }

    // ================= 识别 =================

    public bool CanHandle(string input) =>
        !string.IsNullOrWhiteSpace(input) &&
        (input.Contains("yun.139.com", StringComparison.OrdinalIgnoreCase) ||
         input.Contains("caiyun.139.com", StringComparison.OrdinalIgnoreCase));

    /// <summary>分享链接形态：<c>https://caiyun.139.com/m/i?xxxx</c> 或 <c>.../w/i?xxxx</c>。</summary>
    public ShareLink? ParseShareLink(string input)
    {
        var (url, passcode) = ShareTextParser.Parse(input);
        if (url == null) return null;

        var m = LinkIdPattern().Match(url);
        if (!m.Success) return null;

        return new ShareLink
        {
            Platform = Platform, ShareId = m.Groups[1].Value, Passcode = passcode, RawInput = input,
        };
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"[?&](?:f|linkID|linkId)=([A-Za-z0-9_\-]{6,64})",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex LinkIdPattern();

    // ================= 加密 / 签名 =================

    private static byte[] AesKey => Encoding.UTF8.GetBytes(ShareAesKey);

    /// <summary>明文 JSON → base64(IV ‖ ciphertext)。</summary>
    private static string EncryptBody(string plaintext)
    {
        byte[] iv = RandomNumberGenerator.GetBytes(16);
        using var aes = Aes.Create();
        aes.Key = AesKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        byte[] ct = aes.CreateEncryptor().TransformFinalBlock(
            Encoding.UTF8.GetBytes(plaintext), 0, Encoding.UTF8.GetByteCount(plaintext));
        return Convert.ToBase64String(iv.Concat(ct).ToArray());
    }

    /// <summary>base64 → 明文；解密后若是 gzip 先解压（与原项目一致）。</summary>
    private static string DecryptBody(string base64)
    {
        byte[] raw = Convert.FromBase64String(base64);
        if (raw.Length <= 16) throw new CloudException("响应密文过短");

        byte[] iv = raw[..16];
        byte[] ct = raw[16..];

        using var aes = Aes.Create();
        aes.Key = AesKey;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        byte[] plain = aes.CreateDecryptor().TransformFinalBlock(ct, 0, ct.Length);

        // gzip 魔数 0x1f 0x8b
        if (plain.Length > 2 && plain[0] == 0x1f && plain[1] == 0x8b)
        {
            using var gz = new GZipStream(new MemoryStream(plain), CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            gz.CopyTo(outMs);
            plain = outMs.ToArray();
        }
        return Encoding.UTF8.GetString(plain);
    }

    private static string Md5Hex(string s) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    /// <summary>
    /// <c>mcloud-sign</c> 头：<c>{ts},{rand},{sign}</c>。
    ///
    /// <para>算法：body 先 encodeURIComponent，再按单字符 ASCII 升序排序，base64，
    /// 然后 md5(b64) + md5("ts:rand") 拼接再 md5，最后转大写。</para>
    /// <para><b>必须基于与实际发送完全一致的明文 JSON</b>（字段顺序、无空格），
    /// 否则服务端验签失败。</para>
    /// </summary>
    public static (string Header, string Timestamp) SignHeader(string bodyJson)
    {
        string ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        const string pool = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var rand = new StringBuilder(16);
        for (int i = 0; i < 16; i++)
            rand.Append(pool[Random.Shared.Next(pool.Length)]);

        string encoded = EncodeUriComponent(bodyJson);
        var sorted = new string(encoded.OrderBy(c => (int)c).ToArray());
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(sorted));
        string res = Md5Hex(b64) + Md5Hex($"{ts}:{rand}");
        string sign = Md5Hex(res).ToUpperInvariant();

        return ($"{ts},{rand},{sign}", ts);
    }

    /// <summary>encodeURIComponent：+ 转 %20，并还原 ! ' ( ) *。</summary>
    private static string EncodeUriComponent(string s) =>
        HttpUtility.UrlEncode(s, Encoding.UTF8)
            .Replace("+", "%20")
            .Replace("%21", "!")
            .Replace("%27", "'")
            .Replace("%28", "(")
            .Replace("%29", ")")
            .Replace("%2a", "*")
            .Replace("%2A", "*");

    // ================= 列目录 =================

    public async Task<CloudListing> ListAsync(ShareLink share, string? parentId,
        string? cursor, CancellationToken ct = default)
    {
        // 根目录传 "root"（不能为空，否则返回 9530）
        string pcaId = string.IsNullOrEmpty(parentId) ? "root" : parentId;
        int begin = int.TryParse(cursor, out int b) ? b : 1;

        var req = new JsonObject
        {
            ["account"] = "",     // 列目录端点 account 必须为空串
            ["linkID"] = share.ShareId,
            ["passwd"] = share.Passcode ?? "",
            ["caSrt"] = 1,
            ["coSrt"] = 1,
            ["srtDr"] = 0,
            ["bNum"] = begin,
            ["pCaID"] = pcaId,
            ["eNum"] = begin + 199,
        };
        var body = new JsonObject { ["getOutLinkInfoReq"] = req };

        // 匿名调用：不带 Authorization、不带 mcloud-sign
        var root = await PostAsync(ListUrl, body.ToJsonString(), encrypted: false,
            authorization: null, ct).ConfigureAwait(false);

        string resultCode = Str(root, "resultCode");
        if (resultCode.Length > 0 && resultCode != "0")
        {
            string desc = Str(root, "desc");
            if (resultCode == "9188")
                throw new PasscodeRequiredException(
                    string.IsNullOrEmpty(share.Passcode) ? "该分享需要提取码" : "提取码错误");
            throw new CloudException(desc.Length > 0 ? desc : $"获取文件列表失败（{resultCode}）");
        }

        var listing = new CloudListing();
        if (!root.TryGetProperty("data", out var data)) return listing;

        // 1) 子文件夹在 caLst
        if (data.TryGetProperty("caLst", out var caLst))
        {
            foreach (var item in caLst.EnumerateArray())
            {
                listing.Files.Add(new CloudFile
                {
                    Id = Str(item, "caID"),
                    Name = Str(item, "caName"),
                    Size = 0,
                    IsDirectory = true,
                    ParentId = pcaId,
                    ModifyTime = Str(item, "udTime") is { Length: > 0 } t1 ? t1 : Str(item, "ctTime"),
                });
            }
        }

        // 2) 文件（含 coType==2 的嵌套文件夹）在 coLst
        if (data.TryGetProperty("coLst", out var coLst))
        {
            foreach (var item in coLst.EnumerateArray())
            {
                bool isDir = item.TryGetProperty("isdir", out var d) && d.ValueKind == JsonValueKind.True;
                if (!isDir && item.TryGetProperty("coType", out var t) &&
                    t.ValueKind == JsonValueKind.Number)
                    isDir = t.GetInt32() == 2;

                listing.Files.Add(new CloudFile
                {
                    Id = Str(item, "coID"),
                    Name = Str(item, "coName"),
                    Size = item.TryGetProperty("coSize", out var sz) && sz.ValueKind == JsonValueKind.Number
                        ? sz.GetInt64() : 0,
                    IsDirectory = isDir,
                    ParentId = pcaId,
                    ModifyTime = Str(item, "udTime") is { Length: > 0 } t2 ? t2 : Str(item, "ctTime"),
                });
            }
        }

        // 有满页则可能有下一页
        if (listing.Files.Count >= 199) listing.NextCursor = (begin + 200).ToString();
        return listing;
    }

    // ================= 取直链 =================

    public async Task<DirectLink> GetDirectLinkAsync(ShareLink share, CloudFile file,
        CancellationToken ct = default)
    {
        string cookie = _cookieProvider() ?? "";
        string? authorization = ExtractAuthorization(cookie);
        string account = AccountFromAuthorization(authorization) ?? "";

        var req = new JsonObject
        {
            ["account"] = account,
            ["linkID"] = share.ShareId,
            ["coIDLst"] = new JsonObject { ["item"] = new JsonArray(file.Id) },
            ["commonAccountInfo"] = new JsonObject { ["account"] = account, ["accountType"] = 1 },
        };
        var body = new JsonObject { ["dlFromOutLinkReqV3"] = req };

        // 取直链要加密 + 签名
        var root = await PostAsync(LinkUrl, body.ToJsonString(), encrypted: true,
            authorization, ct).ConfigureAwait(false);

        string resultCode = Str(root, "resultCode");
        if (resultCode.Length > 0 && resultCode != "0")
        {
            string desc = Str(root, "desc");
            if (resultCode is "31001" or "9530")
                throw new NotLoggedInException("139 网盘");
            throw new CloudException(desc.Length > 0 ? desc : $"获取下载链接失败（{resultCode}）");
        }

        if (!root.TryGetProperty("data", out var data))
            throw new CloudException("响应缺少 data");

        string url = Str(data, "redrUrl");
        if (url.Length == 0) throw new CloudException("响应缺少 redrUrl");

        return new DirectLink
        {
            Url = url,
            FileName = Str(data, "fileName") is { Length: > 0 } fn ? fn : file.Name,
            Size = data.TryGetProperty("coSize", out var sz) && sz.ValueKind == JsonValueKind.Number
                ? sz.GetInt64() : file.Size,
            Headers = new Dictionary<string, string>
            {
                // OBS S3 签名直链 900 秒有效，不需要额外头
                ["User-Agent"] = "Mozilla/5.0 (Windows NT 10.0; Win64; x64)",
            },
        };
    }

    /// <summary>139 分享取链不转存。</summary>
    public Task CleanupAsync(ShareLink share, CancellationToken ct = default)
        => Task.CompletedTask;

    // ================= HTTP =================

    private async Task<JsonElement> PostAsync(string url, string plainJson, bool encrypted,
        string? authorization, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url);

        string payload = encrypted ? EncryptBody(plainJson) : plainJson;
        req.Content = new StringContent(payload, Encoding.UTF8,
            encrypted ? "text/plain" : "application/json");

        // 设备头：缺了会被拒
        req.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/116.0.0.0 Safari/537.36");
        req.Headers.TryAddWithoutValidation("x-DeviceInfo",
            "||9|7.17.9|chrome|116.0.0.0|2cdaf7ada9e353c70eba99092e177991||windows 10||zh-CN|||");
        req.Headers.TryAddWithoutValidation("x-ClientInfo",
            "||9|7.17.9|chrome|116.0.0.0|2cdaf7ada9e353c70eba99092e177991||windows 10||zh-CN|||dW5kZWZpbmVk||");
        req.Headers.TryAddWithoutValidation("mcloud-version", "7.17.9");
        req.Headers.TryAddWithoutValidation("mcloud-client", "10701");
        req.Headers.TryAddWithoutValidation("mcloud-channel", "1000101");
        req.Headers.TryAddWithoutValidation("yx-channel-source", "10000034");

        if (encrypted && !string.IsNullOrEmpty(authorization))
        {
            // 签名基于**明文** body，加密只是传输包装
            var (sign, _) = SignHeader(plainJson);
            req.Headers.TryAddWithoutValidation("Authorization", authorization);
            req.Headers.TryAddWithoutValidation("mcloud-sign", sign);
            req.Headers.TryAddWithoutValidation("mcloud-skey", "");
        }

        using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (encrypted)
        {
            // 加密接口的响应可能也是密文
            string trimmed = text.Trim();
            if (!trimmed.StartsWith('{')) text = DecryptBody(trimmed);
        }

        if (!text.TrimStart().StartsWith('{'))
            throw new CloudException($"服务端返回非预期内容（HTTP {(int)resp.StatusCode}）");

        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    /// <summary>从 Cookie 里找 Authorization 的等价信息；139 把 authToken 放在 Cookie 中。</summary>
    private static string? ExtractAuthorization(string cookie)
    {
        if (string.IsNullOrEmpty(cookie)) return null;
        // 优先直接用 Cookie（139 网页端的鉴权在 Cookie 里）
        return "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(cookie.Length > 200
            ? cookie[..200] : cookie));
    }

    private static string? AccountFromAuthorization(string? authorization)
    {
        if (string.IsNullOrEmpty(authorization)) return null;
        try
        {
            string b64 = authorization.Replace("Basic", "").Trim();
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(b64));
            var parts = decoded.Split(':');
            return parts.Length > 1 ? parts[1] : null;
        }
        catch
        {
            return null;
        }
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
}
