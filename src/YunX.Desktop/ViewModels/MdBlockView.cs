using System.Windows;
using System.Windows.Documents;
using YunX.GitHub;

namespace YunX.Desktop.ViewModels;

/// <summary>
/// 一个 README 块的**可绑定**视图模型。
///
/// <para><b>为什么不让 XAML 直接吃 <see cref="MdBlock"/></b>：块类型有 8 种，
/// 每种要呈现的控件都不同（标题是字号、代码是等宽带底色、表格是 Grid、引用带左边框）。
/// XAML 里用 DataTrigger 按 Kind 切换 Template 能写，但一个模板里塞 8 套分支会变得难以维护。
/// 这里把"这块该用什么样式"算好，XAML 只负责按字段渲染，模板就很薄。</para>
///
/// <para><b>行内格式用 <see cref="TextBlock.Inlines"/></b>：粗体/斜体/行内代码/链接混排
/// 必须落在同一个 TextBlock 里才能正常换行；拆成多个控件会把一行文字切断。</para>
/// </summary>
public sealed class MdBlockView
{
    public MdBlockKind Kind { get; init; }

    /// <summary>纯文本内容（段落/标题/引用/代码）。</summary>
    public string Text { get; init; } = "";

    /// <summary>行内片段（段落、标题、引用、列表项用）。</summary>
    public List<MdInline> Inlines { get; init; } = new();

    /// <summary>标题级别（1~6）。</summary>
    public int Level { get; init; }

    /// <summary>代码块语言（仅展示用）。</summary>
    public string Language { get; init; } = "";

    /// <summary>表格行（第一行是表头）。</summary>
    public List<List<string>> Rows { get; init; } = new();

    /// <summary>列表项（无序列表用）。</summary>
    public List<MdInline> Bullet { get; init; } = new();

    public bool OrderedList { get; init; }
    public string ImageUrl { get; init; } = "";
    public string ImageAlt { get; init; } = "";

    // ---------- 渲染用派生属性 ----------

    public Visibility Visibility => Visibility.Visible;

    /// <summary>标题字号：h1 20 → h6 13，逐级递减但不小于正文。</summary>
    public double HeadingFontSize => Level switch
    {
        1 => 20, 2 => 17.5, 3 => 15.5, 4 => 14.5, 5 => 14, _ => 13.5,
    };

    public FontWeight HeadingWeight =>
        Level <= 2 ? FontWeights.SemiBold : FontWeights.Medium;

    /// <summary>标题下面是否画一条细线（h1/h2 才画，与 GitHub 一致）。</summary>
    public Visibility HeadingRuleVisibility =>
        Level <= 2 ? Visibility.Visible : Visibility.Collapsed;

    public Thickness HeadingMargin => Level <= 2
        ? new Thickness(0, 18, 0, 8)
        : new Thickness(0, 14, 0, 6);

    /// <summary>块与块之间的间距。</summary>
    public Thickness BlockMargin => Kind switch
    {
        MdBlockKind.Heading => HeadingMargin,
        MdBlockKind.Rule => new Thickness(0, 12, 0, 12),
        MdBlockKind.CodeBlock => new Thickness(0, 8, 0, 10),
        MdBlockKind.Table => new Thickness(0, 8, 0, 10),
        _ => new Thickness(0, 0, 0, 10),
    };

    /// <summary>代码块语言标签（没有就不显示）。</summary>
    public Visibility LanguageVisibility =>
        string.IsNullOrEmpty(Language) ? Visibility.Collapsed : Visibility.Visible;

    public bool IsTable => Kind == MdBlockKind.Table;
    public bool IsCode => Kind == MdBlockKind.CodeBlock;
    public bool IsImage => Kind == MdBlockKind.Image;
    public bool IsRule => Kind == MdBlockKind.Rule;
    public bool IsHeading => Kind == MdBlockKind.Heading;
    public bool IsParagraph => Kind == MdBlockKind.Paragraph;
    public bool IsQuote => Kind == MdBlockKind.Quote;
    public bool IsList => Kind == MdBlockKind.List;

    public static MdBlockView From(MdBlock b) => new()
    {
        Kind = b.Kind,
        Text = b.Text,
        Inlines = b.Kind is MdBlockKind.Paragraph or MdBlockKind.Heading or MdBlockKind.Quote
            ? MarkdownParser.ParseInline(b.Text)
            : new List<MdInline>(),
        Level = b.Level,
        Language = b.Language,
        Rows = b.Rows,
        Bullet = b.Kind == MdBlockKind.List ? MarkdownParser.ParseInline(b.Text) : new List<MdInline>(),
        OrderedList = b.OrderedList,
        ImageUrl = b.ImageUrl,
        ImageAlt = b.ImageAlt,
    };
}
