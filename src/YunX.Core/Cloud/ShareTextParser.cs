using System.Text.RegularExpressions;

namespace YunX.Core.Cloud;

/// <summary>
/// 从用户粘贴的整段文本中提取分享链接与提取码。
///
/// <para><b>这是原项目一个很实用、但容易被忽视的细节</b>：用户从网盘 App「复制链接」得到的往往是
/// 一整段话，例如：</para>
/// <code>
/// 「我用夸克网盘分享了「某资源」，点击链接即可保存。
///   链接：https://pan.quark.cn/s/abc123def  提取码：k9m2」
/// </code>
/// <para>要求用户自己切出链接和提取码是很差的体验。这里统一处理。</para>
/// </summary>
public static partial class ShareTextParser
{
    // 提取码：支持「提取码：abcd」「密码: abcd」「pwd=abcd」「提取碼 abcd」等写法
    [GeneratedRegex(
        @"(?:提取码|提取碼|访问码|訪問碼|密码|密碼|口令|pwd|passcode|password)\s*[:：=]?\s*([A-Za-z0-9]{3,8})",
        RegexOptions.IgnoreCase)]
    private static partial Regex PasscodePattern();

    // 单独的 4 位提取码（很多平台固定 4 位，出现在链接后面）
    [GeneratedRegex(@"(?:^|\s)([A-Za-z0-9]{4})(?:\s|$)", RegexOptions.IgnoreCase)]
    private static partial Regex BareCodePattern();

    // URL 提取
    [GeneratedRegex(@"https?://[^\s<>""'\u4e00-\u9fff，。、；：！？]+",
        RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    /// <summary>取出文本中的第一个 URL；没有则返回 null。</summary>
    public static string? ExtractUrl(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var m = UrlPattern().Match(input.Trim());
        if (!m.Success) return null;

        string url = m.Value.TrimEnd('.', ',', ';', ')', ']', '。', '，', '、', '；', '：');
        return url;
    }

    /// <summary>
    /// 提取码。优先匹配带关键字的形式，其次才尝试裸 4 位码
    /// （裸码误判率高，只在明确存在分享链接时才有意义）。
    /// </summary>
    public static string? ExtractPasscode(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var m = PasscodePattern().Match(input);
        if (m.Success) return m.Groups[1].Value;

        // 裸 4 位：只在文本里没有疑似年份/数量时采用
        var bare = BareCodePattern().Match(input);
        if (bare.Success)
        {
            string code = bare.Groups[1].Value;
            // 纯数字且看起来像年份（19xx/20xx）的不要
            if (code.Length == 4 && code.All(char.IsDigit))
            {
                if (int.TryParse(code, out int n) && n is >= 1900 and <= 2100) return null;
            }
            return code;
        }
        return null;
    }

    /// <summary>同时取出 URL 与提取码。</summary>
    public static (string? Url, string? Passcode) Parse(string input)
        => (ExtractUrl(input), ExtractPasscode(input));

    /// <summary>
    /// 判断输入是否像「中文口令」而非链接。
    /// 原项目支持把这类文本发往第三方服务换取分享 ID —— 本版不内置该服务，
    /// 但保留识别能力，让上层能给出准确提示。
    /// </summary>
    public static bool LooksLikeChineseCommand(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        var t = input.Trim();
        if (t.Contains("http", StringComparison.OrdinalIgnoreCase)) return false;

        // 全是中文（含少量标点）且长度在合理区间
        int cjk = t.Count(c => c >= 0x4E00 && c <= 0x9FFF);
        return cjk >= t.Length * 0.6 && t.Length is >= 4 and <= 60;
    }
}
