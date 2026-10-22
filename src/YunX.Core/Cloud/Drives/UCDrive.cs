using System.Text.RegularExpressions;
using YunX.Core.Cloud;

namespace YunX.Core.Cloud.Drives;

/// <summary>
/// UC 网盘适配器。
///
/// <para>UC 与夸克是**同一套 clouddrive API**（字节系云盘），只有域名、<c>pr</c> 参数、
/// 列表接口版本号不同。所以这里继承 <see cref="QuarkDrive"/>，只覆盖差异项 ——
/// 原项目为此写了两份约 700/840 行的独立实现，其中大部分是重复逻辑。</para>
///
/// <para>差异清单：</para>
/// <list type="bullet">
///   <item>API 域：<c>pc-api.uc.cn</c>（夸克是 drive-pc.quark.cn）</item>
///   <item>pr 参数：<c>pr=UCBrowser</c>（夸克是 pr=ucpro）</item>
///   <item>列表接口走 <c>v2/detail</c>（夸克是 detail）</item>
///   <item>网页域：<c>drive.uc.cn</c></item>
/// </list>
/// </summary>
public sealed partial class UCDrive : QuarkDrive
{
    public UCDrive(HttpClient client, Func<string?> cookieProvider)
        : base(client, cookieProvider) { }

    public override string Platform => CloudPlatforms.UC;
    public override string DisplayName => "UC 网盘";

    protected override string ApiBase => "https://pc-api.uc.cn";
    protected override string PrParam => "pr=UCBrowser&fr=pc";
    protected override string DetailPath => "/1/clouddrive/share/sharepage/v2/detail";
    protected override string Referer => "https://drive.uc.cn/";
    protected override string OriginHost => "https://drive.uc.cn";

    public override LoginDescriptor? Login => new()
    {
        Kind = LoginKind.Browser,
        LoginUrl = "https://drive.uc.cn/",
        CookieDomains = new[] { "uc.cn" },
        Hint = "在内嵌浏览器里登录 UC 网盘后会自动保存 Cookie",
    };

    public override bool CanHandle(string input) =>
        !string.IsNullOrWhiteSpace(input) &&
        (input.Contains("drive.uc.cn", StringComparison.OrdinalIgnoreCase) ||
         input.Contains("uc.cn/s/", StringComparison.OrdinalIgnoreCase));

    public override ShareLink? ParseShareLink(string input)
    {
        var (url, passcode) = ShareTextParser.Parse(input);
        if (url == null) return null;

        // https://drive.uc.cn/s/{shareId}
        var m = UcShareIdPattern().Match(url);
        if (!m.Success) return null;

        return new ShareLink
        {
            Platform = Platform,
            ShareId = m.Groups[1].Value,
            Passcode = passcode,
            RawInput = input,
        };
    }

    [GeneratedRegex(@"/s/([A-Za-z0-9]{6,32})")]
    private static partial Regex UcShareIdPattern();
}
