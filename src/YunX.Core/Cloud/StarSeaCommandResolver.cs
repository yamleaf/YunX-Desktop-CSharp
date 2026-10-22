using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace YunX.Core.Cloud;

/// <summary>
/// 星海口令解析器。
///
/// <para><b>它不是网盘</b> —— 没有分享链接、没有目录、没有取直接口。
/// 它只做一件事：把一个中文口令（如「绝对有效 咐置松君杂货铺叩苓」）换成一个
/// <c>pwdId</c>，然后合成夸克或 UC 的分享链接，后面<b>完全复用</b>夸克/UC 的链路。</para>
///
/// <para>所以它挂在 <see cref="SourceResolver"/> 的口令兜底分支上，而不是注册成
/// <see cref="ICloudDrive"/>。顺序是：迅雷口令优先（能直接解析出分享+提取码），
/// 迅雷失败再走这里（覆盖夸克/UC 资源的口令）。</para>
///
/// <para><b>隐私说明</b>：这一步会把口令原文发往第三方服务。日志只记长度与失败原因，
/// 不落口令本体 —— 和迅雷口令处理保持一致的谨慎程度。</para>
///
/// <para>服务端限流 60 次/分钟/IP，超限返回 429。</para>
/// </summary>
public sealed partial class StarSeaCommandResolver
{
    private const string ResolveUrl = "https://starsea-api.qwq.pics/api/resolve";

    /// <summary>服务端签发的固定 Key（原项目内置值）。</summary>
    private const string ApiKey = "8bfbe163c670ae500aca5f3b2f060ecc8463e1eb1691ba8d";

    /// <summary>口令长度上限（按 UTF-16 char 数）。超过就不当作口令，避免把长文本乱发出去。</summary>
    private const int MaxCommandLength = 120;

    private readonly HttpClient _client;

    public StarSeaCommandResolver(HttpClient client) => _client = client;

    /// <summary>
    /// 判断输入是否像口令：非空、不超长、且<b>不含链接</b>。
    /// 含链接的文本应该走上游的链接解析，不该发到这里。
    /// </summary>
    public static bool LooksLikeCommand(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || t.Length > MaxCommandLength) return false;
        if (t.Contains("http://", StringComparison.OrdinalIgnoreCase)) return false;
        if (t.Contains("https://", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>
    /// 口令 → 分享链接。失败返回 null（并把原因写进 <paramref name="error"/>）。
    /// </summary>
    public async Task<ShareLink?> ResolveAsync(string command, Action<string>? log = null,
        CancellationToken ct = default)
    {
        if (!LooksLikeCommand(command)) return null;

        try
        {
            var body = new JsonObject
            {
                ["clipboard"] = command.Trim(),
                // auto 让服务端自己判定是夸克还是 UC
                ["platform"] = "auto",
                ["step"] = "parse",
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, ResolveUrl)
            {
                Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
            };
            req.Headers.TryAddWithoutValidation("X-API-Key", ApiKey);
            req.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var resp = await _client.SendAsync(req, ct).ConfigureAwait(false);

            // 业务失败与 HTTP 状态码是分离的：非 2xx 也可能带合法 JSON，所以先读 body
            string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            JsonNode? json = null;
            try
            {
                json = JsonNode.Parse(text);
            }
            catch
            {
                log?.Invoke($"口令解析服务返回非 JSON（HTTP {(int)resp.StatusCode}）");
                return null;
            }

            if ((bool?)json?["success"] != true)
            {
                string msg = Str(json, "msg");
                log?.Invoke(msg.Length > 0
                    ? $"口令解析失败：{msg}"
                    : $"口令解析失败：{DescribeFailure((int)resp.StatusCode)}");
                return null;
            }

            var data = json["data"] as JsonObject;
            if (data == null)
            {
                log?.Invoke("口令解析失败：响应缺少 data");
                return null;
            }

            string pwdId = Str(data, "pwdId");
            if (pwdId.Length == 0)
            {
                log?.Invoke("口令解析失败：未取到分享 ID");
                return null;
            }

            string platform = Str(data, "platform");
            string url = ShareLinkFor(platform, pwdId);
            string cloud = platform.Equals("uc", StringComparison.OrdinalIgnoreCase) ? "UC" : "夸克";
            log?.Invoke($"口令已解析为{cloud}分享：{pwdId}");

            return new ShareLink
            {
                Platform = cloud == "UC" ? CloudPlatforms.UC : CloudPlatforms.Quark,
                ShareId = pwdId,
                Passcode = null,
                RawInput = url,
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            // 只聊失败类型，不落口令本体
            log?.Invoke($"口令解析请求失败：{e.GetType().Name}");
            return null;
        }
    }

    /// <summary>合成分享链接。未知平台一律兜底成夸克。</summary>
    public static string ShareLinkFor(string platform, string pwdId) =>
        platform.Equals("uc", StringComparison.OrdinalIgnoreCase)
            ? $"https://drive.uc.cn/s/{pwdId}"
            : $"https://pan.quark.cn/s/{pwdId}";

    private static string DescribeFailure(int status) => status switch
    {
        400 => "口令为空或格式不对",
        401 => "口令解析服务鉴权失败（Key 无效或已轮换）",
        413 => "口令过长",
        429 => "口令解析服务限流，请稍后再试",
        404 => "口令解析服务地址不可用",
        _ => $"口令解析失败（HTTP {status}）",
    };

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
}
