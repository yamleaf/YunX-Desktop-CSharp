using System.Text;
using System.Text.RegularExpressions;

namespace YunX.GitHub;

/// <summary>Markdown 渲染结果的块类型。</summary>
public enum MdBlockKind
{
    Heading,
    Paragraph,
    CodeBlock,
    Quote,
    List,
    Table,
    Rule,
    Image,
}

/// <summary>一个渲染块。</summary>
public sealed class MdBlock
{
    public MdBlockKind Kind { get; init; }
    public string Text { get; init; } = "";
    /// <summary>标题级别 / 列表缩进级别。</summary>
    public int Level { get; init; }
    /// <summary>代码块语言。</summary>
    public string Language { get; init; } = "";
    /// <summary>表格行。</summary>
    public List<List<string>> Rows { get; init; } = new();
    /// <summary>表格表头是否左/中/右对齐。</summary>
    public List<string> Alignments { get; init; } = new();
    public bool OrderedList { get; init; }
    /// <summary>图片 URL（Kind == Image 时）。</summary>
    public string ImageUrl { get; init; } = "";
    public string ImageAlt { get; init; } = "";
}

/// <summary>行内片段。</summary>
public enum MdInlineKind
{
    Text,
    Bold,
    Italic,
    Code,
    Link,
    Image,
}

/// <summary>行内片段（用于富文本渲染）。</summary>
public sealed record MdInline(MdInlineKind Kind, string Text, string Url = "");

/// <summary>
/// 轻量 GFM Markdown 解析器。
///
/// <para><b>为什么不引第三方库</b>：Markdig 之类功能完整但会把单文件发布体积推高，
/// 而 README 渲染实际只需要「标题 / 段落 / 代码 / 列表 / 引用 / 表格 / 图片 / 链接」这些子集。
/// 自己写可以精确控制输出结构，也避免第三方库对 XAML 渲染的不适配。</para>
///
/// <para><b>支持范围</b>（覆盖 GitHub README 的绝大多数用法）：
/// ATX 标题、围栏代码块（带语言）、引用、有序/无序列表（含嵌套缩进）、
/// 分割线、GFM 表格（含对齐）、行内粗体/斜体/行内代码/链接/图片、
/// <c>&lt;details&gt;</c> 折叠块（降级为引用样式）。</para>
/// </summary>
public sealed partial class MarkdownParser
{
    public List<MdBlock> Parse(string markdown)
    {
        var blocks = new List<MdBlock>();
        if (string.IsNullOrEmpty(markdown)) return blocks;

        var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        int i = 0;

        while (i < lines.Length)
        {
            string line = lines[i];

            // 空行
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

            // 围栏代码块
            var fence = FencePattern().Match(line);
            if (fence.Success)
            {
                string lang = fence.Groups[1].Value;
                string closer = fence.Groups[0].Value[..3];
                var sb = new StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith(closer, StringComparison.Ordinal))
                {
                    sb.AppendLine(lines[i]);
                    i++;
                }
                i++;   // 跳过闭合围栏
                blocks.Add(new MdBlock
                {
                    Kind = MdBlockKind.CodeBlock,
                    Text = sb.ToString().TrimEnd(),
                    Language = lang,
                });
                continue;
            }

            // ATX 标题
            var heading = HeadingPattern().Match(line);
            if (heading.Success)
            {
                blocks.Add(new MdBlock
                {
                    Kind = MdBlockKind.Heading,
                    Level = heading.Groups[1].Length,
                    Text = heading.Groups[2].Value.Trim(),
                });
                i++;
                continue;
            }

            // 分割线
            if (RulePattern().IsMatch(line))
            {
                blocks.Add(new MdBlock { Kind = MdBlockKind.Rule });
                i++;
                continue;
            }

            // 表格：当前行含 | 且下一行是分隔行
            if (line.Contains('|') && i + 1 < lines.Length && TableSeparatorPattern().IsMatch(lines[i + 1]))
            {
                var (table, consumed) = ParseTable(lines, i);
                blocks.Add(table);
                i += consumed;
                continue;
            }

            // 引用
            if (line.TrimStart().StartsWith(">", StringComparison.Ordinal))
            {
                var sb = new StringBuilder();
                while (i < lines.Length && lines[i].TrimStart().StartsWith(">", StringComparison.Ordinal))
                {
                    sb.AppendLine(lines[i].TrimStart().TrimStart('>').TrimStart());
                    i++;
                }
                blocks.Add(new MdBlock
                {
                    Kind = MdBlockKind.Quote,
                    Text = sb.ToString().TrimEnd(),
                });
                continue;
            }

            // HTML 折叠块 <details>：降级为引用样式，但保留内部文本
            if (line.TrimStart().StartsWith("<details", StringComparison.OrdinalIgnoreCase))
            {
                var sb = new StringBuilder();
                i++;
                while (i < lines.Length &&
                       !lines[i].TrimStart().StartsWith("</details", StringComparison.OrdinalIgnoreCase))
                {
                    string inner = lines[i];
                    // 去掉 <summary> 标签但保留文字
                    inner = SummaryTagPattern().Replace(inner, "");
                    inner = HtmlTagPattern().Replace(inner, "");
                    if (!string.IsNullOrWhiteSpace(inner)) sb.AppendLine(inner);
                    i++;
                }
                i++;   // 跳过 </details>
                blocks.Add(new MdBlock
                {
                    Kind = MdBlockKind.Quote,
                    Text = sb.ToString().TrimEnd(),
                });
                continue;
            }

            // 列表
            var listItem = ListPattern().Match(line);
            if (listItem.Success)
            {
                var (list, consumed) = ParseList(lines, i);
                blocks.Add(list);
                i += consumed;
                continue;
            }

            // 独占一行的图片
            var img = StandaloneImagePattern().Match(line.Trim());
            if (img.Success)
            {
                blocks.Add(new MdBlock
                {
                    Kind = MdBlockKind.Image,
                    ImageAlt = img.Groups[1].Value,
                    ImageUrl = img.Groups[2].Value,
                });
                i++;
                continue;
            }

            // 普通段落：连续非空行合并
            {
                var sb = new StringBuilder();
                while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i]) &&
                       !IsBlockStart(lines, i))
                {
                    if (sb.Length > 0) sb.Append(' ');
                    sb.Append(lines[i].Trim());
                    i++;
                }
                if (sb.Length > 0)
                {
                    blocks.Add(new MdBlock
                    {
                        Kind = MdBlockKind.Paragraph,
                        Text = sb.ToString(),
                    });
                }
                else
                {
                    i++;   // 防止死循环
                }
            }
        }

        return blocks;
    }

    private static bool IsBlockStart(string[] lines, int i)
    {
        string line = lines[i];
        if (FencePattern().IsMatch(line)) return true;
        if (HeadingPattern().IsMatch(line)) return true;
        if (RulePattern().IsMatch(line)) return true;
        if (line.TrimStart().StartsWith(">", StringComparison.Ordinal)) return true;
        if (ListPattern().IsMatch(line)) return true;
        if (line.TrimStart().StartsWith("<details", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.Contains('|') && i + 1 < lines.Length && TableSeparatorPattern().IsMatch(lines[i + 1]))
            return true;
        return false;
    }

    private static (MdBlock Table, int Consumed) ParseTable(string[] lines, int start)
    {
        // 表头
        var header = SplitRow(lines[start]);
        var aligns = SplitRow(lines[start + 1])
            .Select(c => c.Trim() switch
            {
                var s when s.StartsWith(":", StringComparison.Ordinal) &&
                   s.EndsWith(":", StringComparison.Ordinal) => "center",
                var s when s.EndsWith(":", StringComparison.Ordinal) => "right",
                _ => "left",
            })
            .ToList();

        var rows = new List<List<string>> { header };
        int i = start + 2;
        while (i < lines.Length && lines[i].Contains('|') && !string.IsNullOrWhiteSpace(lines[i]))
        {
            rows.Add(SplitRow(lines[i]));
            i++;
        }
        return (new MdBlock { Kind = MdBlockKind.Table, Rows = rows, Alignments = aligns }, i - start);
    }

    private static List<string> SplitRow(string line)
    {
        string t = line.Trim().Trim('|');
        return t.Split('|').Select(c => c.Trim()).ToList();
    }

    private static (MdBlock List, int Consumed) ParseList(string[] lines, int start)
    {
        var items = new List<string>();
        bool ordered = false;
        int i = start;

        var first = ListPattern().Match(lines[start]);
        ordered = first.Groups[1].Value.Length > 0;

        while (i < lines.Length)
        {
            var m = ListPattern().Match(lines[i]);
            if (!m.Success)
            {
                // 缩进的续行并入上一条
                if (lines[i].StartsWith("  ") && items.Count > 0 && !string.IsNullOrWhiteSpace(lines[i]))
                {
                    items[^1] += " " + lines[i].Trim();
                    i++;
                    continue;
                }
                break;
            }
            items.Add(m.Groups[2].Value.Trim());
            i++;
        }

        return (new MdBlock
        {
            Kind = MdBlockKind.List,
            OrderedList = ordered,
            Text = string.Join("\n", items),
            Rows = items.Select(x => new List<string> { x }).ToList(),
        }, i - start);
    }

    /// <summary>
    /// 解析行内格式（粗体 / 斜体 / 行内代码 / 链接 / 图片）。
    /// </summary>
    public static List<MdInline> ParseInline(string text)
    {
        var result = new List<MdInline>();
        if (string.IsNullOrEmpty(text)) return result;

        int i = 0;
        var plain = new StringBuilder();

        void FlushPlain()
        {
            if (plain.Length == 0) return;
            result.Add(new MdInline(MdInlineKind.Text, plain.ToString()));
            plain.Clear();
        }

        while (i < text.Length)
        {
            // 行内代码 `code`
            if (text[i] == '`')
            {
                int end = text.IndexOf('`', i + 1);
                if (end > i)
                {
                    FlushPlain();
                    result.Add(new MdInline(MdInlineKind.Code, text[(i + 1)..end]));
                    i = end + 1;
                    continue;
                }
            }

            // 图片 ![alt](url)
            if (text[i] == '!' && i + 1 < text.Length && text[i + 1] == '[')
            {
                var m = InlineImagePattern().Match(text, i);
                if (m.Success)
                {
                    FlushPlain();
                    result.Add(new MdInline(MdInlineKind.Image, m.Groups[1].Value,
                        m.Groups[2].Value));
                    i = m.Index + m.Length;
                    continue;
                }
            }

            // 链接 [text](url)
            if (text[i] == '[')
            {
                var m = InlineLinkPattern().Match(text, i);
                if (m.Success)
                {
                    FlushPlain();
                    result.Add(new MdInline(MdInlineKind.Link, m.Groups[1].Value,
                        m.Groups[2].Value));
                    i = m.Index + m.Length;
                    continue;
                }
            }

            // 粗体 **text**
            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                int end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    FlushPlain();
                    result.Add(new MdInline(MdInlineKind.Bold, text[(i + 2)..end]));
                    i = end + 2;
                    continue;
                }
            }

            // 斜体 *text* / _text_
            if (text[i] is '*' or '_')
            {
                char c = text[i];
                int end = text.IndexOf(c, i + 1);
                if (end > i + 1 && end - i < 200)
                {
                    FlushPlain();
                    result.Add(new MdInline(MdInlineKind.Italic, text[(i + 1)..end]));
                    i = end + 1;
                    continue;
                }
            }

            plain.Append(text[i]);
            i++;
        }

        FlushPlain();
        return result;
    }

    /// <summary>
    /// 把 README 里的相对图片地址改写成绝对 raw 地址。
    /// GitHub README 常用 <c>![](./docs/a.png)</c> 这种相对路径，不改写就加载不出来。
    /// </summary>
    public static string RewriteRelativeUrls(string markdown, string owner, string repo,
        string gitRef)
    {
        if (string.IsNullOrEmpty(markdown)) return markdown;

        string baseRaw = $"https://raw.githubusercontent.com/{owner}/{repo}/{gitRef}/";

        return RelativeImagePattern().Replace(markdown, m =>
        {
            string alt = m.Groups[1].Value;
            string url = m.Groups[2].Value;
            if (url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("//", StringComparison.Ordinal))
                return m.Value;
            string clean = url.TrimStart('.', '/');
            return $"![{alt}]({baseRaw}{clean})";
        });
    }

    // ================= 正则 =================

    
    #if NET48
    // 源生成器在 net48 上不可用，改用惰性初始化的运行时 Regex
    private static Regex? _fencePatternCache;
    private static Regex FencePattern() =>
        _fencePatternCache ??= new Regex(@"^```(\w*)|^~~~(\w*)", RegexOptions.Compiled);
    #else
    [GeneratedRegex(@"^```(\w*)|^~~~(\w*)")]
        private static partial Regex FencePattern();
    #endif

    
    #if NET48
    // 源生成器在 net48 上不可用，改用惰性初始化的运行时 Regex
    private static Regex? _headingPatternCache;
    private static Regex HeadingPattern() =>
        _headingPatternCache ??= new Regex(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
    #else
    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
        private static partial Regex HeadingPattern();
    #endif

    
    #if NET48
    // 源生成器在 net48 上不可用，改用惰性初始化的运行时 Regex
    private static Regex? _rulePatternCache;
    private static Regex RulePattern() =>
        _rulePatternCache ??= new Regex(@"^\s*([-*_])\s*\1\s*\1[\s\1]*$", RegexOptions.Compiled);
    #else
    [GeneratedRegex(@"^\s*([-*_])\s*\1\s*\1[\s\1]*$")]
        private static partial Regex RulePattern();
    #endif

    
    #if NET48
    // 源生成器在 net48 上不可用，改用惰性初始化的运行时 Regex
    private static Regex? _tableSeparatorPatternCache;
    private static Regex TableSeparatorPattern() =>
        _tableSeparatorPatternCache ??= new Regex(@"^\s*\|?[\s:|-]+\|[\s:|-]*$", RegexOptions.Compiled);
    #else
    [GeneratedRegex(@"^\s*\|?[\s:|-]+\|[\s:|-]*$")]
        private static partial Regex TableSeparatorPattern();
    #endif

    
    #if NET48
    // 源生成器在 net48 上不可用，改用惰性初始化的运行时 Regex
    private static Regex? _listPatternCache;
    private static Regex ListPattern() =>
        _listPatternCache ??= new Regex(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$", RegexOptions.Compiled);
    #else
    [GeneratedRegex(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$")]
        private static partial Regex ListPattern();
    #endif

    
    #if NET48
    // 源生成器在 net48 上不可用，改用惰性初始化的运行时 Regex
    private static Regex? _standaloneImagePatternCache;
    private static Regex StandaloneImagePattern() =>
        _standaloneImagePatternCache ??= new Regex(@"^!\[([^\]]*)\]\(([^)\s]+)\)$", RegexOptions.Compiled);
    #else
    [GeneratedRegex(@"^!\[([^\]]*)\]\(([^)\s]+)\)$")]
        private static partial Regex StandaloneImagePattern();
    #endif

    
    #if NET48
    // 源生成器在 net48 上不可用，改用惰性初始化的运行时 Regex
    private static Regex? _inlineImagePatternCache;
    private static Regex InlineImagePattern() =>
        _inlineImagePatternCache ??= new Regex(@"!\[([^\]]*)\]\(([^)\s]+)\)", RegexOptions.Compiled);
    #else
    [GeneratedRegex(@"!\[([^\]]*)\]\(([^)\s]+)\)")]
        private static partial Regex InlineImagePattern();
    #endif

    
    #if NET48
    // 源生成器在 net48 上不可用，改用惰性初始化的运行时 Regex
    private static Regex? _inlineLinkPatternCache;
    private static Regex InlineLinkPattern() =>
        _inlineLinkPatternCache ??= new Regex(@"\[([^\]]+)\]\(([^)\s]+)\)", RegexOptions.Compiled);
    #else
    [GeneratedRegex(@"\[([^\]]+)\]\(([^)\s]+)\)")]
        private static partial Regex InlineLinkPattern();
    #endif

    
    #if NET48
    // 源生成器在 net48 上不可用，改用惰性初始化的运行时 Regex
    private static Regex? _summaryTagPatternCache;
    private static Regex SummaryTagPattern() =>
        _summaryTagPatternCache ??= new Regex(@"<summary>(.*?)</summary>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    #else
    [GeneratedRegex(@"<summary>(.*?)</summary>", RegexOptions.IgnoreCase)]
        private static partial Regex SummaryTagPattern();
    #endif

    
    #if NET48
    // 源生成器在 net48 上不可用，改用惰性初始化的运行时 Regex
    private static Regex? _htmlTagPatternCache;
    private static Regex HtmlTagPattern() =>
        _htmlTagPatternCache ??= new Regex(@"</?(?:details|summary|div|p|br|img|a|b|i|code|pre|table|tr|td|th)[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    #else
    [GeneratedRegex(@"</?(?:details|summary|div|p|br|img|a|b|i|code|pre|table|tr|td|th)[^>]*>",
            RegexOptions.IgnoreCase)]
        private static partial Regex HtmlTagPattern();
    #endif

    
    #if NET48
    // 源生成器在 net48 上不可用，改用惰性初始化的运行时 Regex
    private static Regex? _relativeImagePatternCache;
    private static Regex RelativeImagePattern() =>
        _relativeImagePatternCache ??= new Regex(@"!\[([^\]]*)\]\((?!https?:|data:|//)([^)\s]+)\)", RegexOptions.Compiled);
    #else
    [GeneratedRegex(@"!\[([^\]]*)\]\((?!https?:|data:|//)([^)\s]+)\)")]
        private static partial Regex RelativeImagePattern();
    #endif
}
