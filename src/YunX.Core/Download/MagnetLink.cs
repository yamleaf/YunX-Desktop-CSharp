using System.Text;

namespace YunX.Core.Download;

/// <summary>
/// 磁力链接（<c>magnet:?xt=urn:btih:...</c>）的识别与信息提取。
///
/// <para><b>为什么不用 <see cref="Uri"/> 解析</b>：磁力链接不是标准 URL ——
/// 它没有 authority 段，<c>?</c> 后面是一串 <c>&amp;</c> 连接的参数，
/// 而 <c>dn</c>（显示名）里还可能带空格和中文。用 URI 解析会把它拆得七零八落，
/// 甚至直接抛异常。这里只做字符串处理，纯函数、可单测。</para>
///
/// <para><b>只做「够用」的解析</b>：识别 scheme、取 <c>dn</c> 显示名、取 <c>xt=urn:btih:</c>
/// 的 info hash。真正的种子名与文件列表必须由 BT 引擎解析元数据后给出 ——
/// 在拿到元数据之前，磁力里就只包含这些信息，在这里"猜"文件名是错误的。</para>
/// </summary>
public static class MagnetLink
{
    private const string Scheme = "magnet:";

    /// <summary>info hash：<c>xt=urn:btih:</c> 后面跟 40 位 hex 或 32 位 base32。</summary>
    private static readonly System.Text.RegularExpressions.Regex Btih =
        new(@"(?i)xt=urn:btih:([A-Za-z0-9]+)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary><c>dn</c> 里的换行/制表符：显示名要进列表和通知，先压成空格。</summary>
    private static readonly System.Text.RegularExpressions.Regex BlankChars =
        new(@"[\r\n\t]+", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>显示名上限：<c>dn</c> 有时会把整段简介塞进来，截断后界面才不会被撑爆。</summary>
    private const int MaxNameChars = 80;

    /// <summary>
    /// 是不是磁力链接。
    /// <para>大小写不敏感、允许前后空白；只有 <c>magnet:</c> 光杆（没有内容）不算。</para>
    /// </summary>
    public static bool IsMagnet(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        string text = url.Trim();
        return text.Length > Scheme.Length
               && text.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>info hash（小写 hex，或原始 base32）；取不到返回空串。</summary>
    public static string InfoHash(string? url)
    {
        if (!IsMagnet(url)) return "";
        var m = Btih.Match(url!.Trim());
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>
    /// 任务显示名：优先 <c>dn=</c>（磁力自带的显示名），没有就用 info hash 前 8 位占位。
    ///
    /// <para>只用于「元数据到手前」的界面显示 —— 磁力在解析出元数据之前拿不到真正的种子名，
    /// 引擎给出真实文件名后应当用它覆盖。</para>
    /// </summary>
    public static string DisplayName(string? url)
    {
        if (!IsMagnet(url)) return "磁力任务";

        string dn = GetParam(url!, "dn").Replace("\n", " ").Replace("\r", " ");
        dn = BlankChars.Replace(dn, " ").Trim();
        if (dn.Length > 0)
            return dn.Length <= MaxNameChars ? dn : dn[..MaxNameChars];

        string hash = InfoHash(url);
        return hash.Length >= 8 ? $"磁力_{hash[..8]}" : "磁力任务";
    }

    /// <summary>
    /// info hash 是否**结构上可用**（不只是格式对，还要能真的交给 BT 库）。
    ///
    /// <para><b>为什么必须单独校验</b>：Gopeed 底层的 anacrolix/torrent 遇到
    /// 全零或不完整的 info hash 会直接 <c>panic: is zero</c> —— 那是 Go 的 panic，
    /// <b>整个引擎进程会退出</b>，连带着把这个引擎上正在跑的其它任务一起弄死。
    /// 实测就发生过：一条非法磁力让引擎崩掉，后续所有 API 调用都变成"连接被拒绝"。
    /// 所以宁可在这里拒绝并给出人话提示，也不能把非法输入透传给引擎。</para>
    /// </summary>
    public static bool IsStructurallyValid(string? url)
    {
        string hash = InfoHash(url);
        if (hash.Length == 0) return false;

        // 40 位 hex：不能全 0（BT 库视作零值并 panic）
        if (hash.Length == 40 && hash.All(Uri.IsHexDigit))
            return hash.Any(c => c != '0');

        // 32 位 base32：A-Z 与 2-7
        if (hash.Length == 32
            && hash.All(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')
                             || (c >= '2' && c <= '7')))
            return hash.Any(c => c != 'A' && c != 'a');

        return false;
    }

    /// <summary>
    /// 规范化：只保留 <c>xt=urn:btih:</c>（必需）与 <c>dn</c>（可读），
    /// 丢掉 <c>tr</c>（tracker）之外的一堆冗余参数会让链接变得不可用，所以**不裁剪**，
    /// 只做空白清理。这个方法存在的目的是给「同一磁力去重」提供一个稳定 key。
    /// </summary>
    public static string Normalize(string? url)
    {
        if (!IsMagnet(url)) return "";
        string hash = InfoHash(url).ToLowerInvariant();
        return hash.Length == 0 ? "" : "magnet:" + hash;
    }

    /// <summary>取某个查询参数（已被 URL 解码）。</summary>
    private static string GetParam(string url, string key)
    {
        int q = url.IndexOf('?');
        if (q < 0) return "";

        foreach (string part in url[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;

            string k = part[..eq];
            if (!k.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;

            string v = part[(eq + 1)..];
            try { return Uri.UnescapeDataString(v.Replace('+', ' ')); }
            catch { return v; }        // 编码坏了就原样返回，不该因此崩掉
        }
        return "";
    }
}
