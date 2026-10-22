using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using YunX.GitHub;

namespace YunX.Desktop.Views;

/// <summary>
/// 把 <see cref="MdInline"/> 列表渲染进 <see cref="TextBlock.Inlines"/> 的附加属性。
///
/// <para><b>为什么要用附加属性</b>：<see cref="TextBlock.Inlines"/> 是只读集合，
/// 不能在 XAML 里绑定一个 ItemsSource；而粗体/斜体/行内代码/链接必须混排在同一个
/// TextBlock 里才能正常换行（拆成多个控件会把一行文字切断）。
/// 附加属性是 WPF 里把复杂富文本塞进 TextBlock 的标准做法。</para>
///
/// <para>用法：<c>&lt;TextBlock local:MdInlineText.Inlines="{Binding Inlines}" /&gt;</c></para>
/// </summary>
public static class MdInlineText
{
    public static readonly DependencyProperty InlinesProperty =
        DependencyProperty.RegisterAttached(
            "Inlines", typeof(IEnumerable<MdInline>), typeof(MdInlineText),
            new PropertyMetadata(null, OnInlinesChanged));

    public static void SetInlines(DependencyObject element, IEnumerable<MdInline>? value)
        => element.SetValue(InlinesProperty, value);

    public static IEnumerable<MdInline>? GetInlines(DependencyObject element)
        => (IEnumerable<MdInline>?)element.GetValue(InlinesProperty);

    private static void OnInlinesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;

        tb.Inlines.Clear();
        if (e.NewValue is not IEnumerable<MdInline> items) return;

        foreach (var inline in items)
        {
            switch (inline.Kind)
            {
                case MdInlineKind.Bold:
                    tb.Inlines.Add(new Bold(new Run(inline.Text)));
                    break;

                case MdInlineKind.Italic:
                    tb.Inlines.Add(new Italic(new Run(inline.Text)));
                    break;

                case MdInlineKind.Code:
                    // 行内代码：等宽字体 + 淡底色（Border 在 Inline 里不可用，用背景色代替）
                    tb.Inlines.Add(new Run(inline.Text)
                    {
                        FontFamily = MonoFont,
                        Background = CodeBg,
                        Foreground = CodeFg,
                    });
                    break;

                case MdInlineKind.Link:
                    var link = new Hyperlink(new Run(inline.Text))
                    {
                        NavigateUri = TryUri(inline.Url),
                        ToolTip = inline.Url,
                        Foreground = LinkFg,
                    };
                    // 外链走系统浏览器；内部链接（#anchor）忽略
                    link.RequestNavigate += (_, args) =>
                    {
                        if (args.Uri is null) return;
                        try
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = args.Uri.AbsoluteUri,
                                UseShellExecute = true,
                            });
                            args.Handled = true;
                        }
                        catch { /* 打不开就算了，不该因为一个链接崩界面 */ }
                    };
                    tb.Inlines.Add(link);
                    break;

                case MdInlineKind.Image:
                    // 行内图片降级为链接文字：真正的图片块走 MdBlockKind.Image
                    tb.Inlines.Add(new Run("🖼 " + (string.IsNullOrEmpty(inline.Text)
                        ? "图片" : inline.Text))
                    {
                        Foreground = MutedFg,
                    });
                    break;

                default:
                    tb.Inlines.Add(new Run(inline.Text));
                    break;
            }
        }
    }

    private static Uri? TryUri(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var u)
           && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? u
            : null;

    // 颜色从主题里取；取不到就退回一套安全默认值（不引资源字典依赖，便于单测）
    private static FontFamily MonoFont => new("Consolas, Microsoft YaHei UI");

    private static Brush CodeBg =>
        Application.Current?.TryFindResource("SurfaceTopBrush") as Brush ?? Brushes.Transparent;

    private static Brush CodeFg =>
        Application.Current?.TryFindResource("TextBrush") as Brush ?? Brushes.White;

    private static Brush LinkFg =>
        Application.Current?.TryFindResource("AccentBrush") as Brush ?? Brushes.DodgerBlue;

    private static Brush MutedFg =>
        Application.Current?.TryFindResource("TextMutedBrush") as Brush ?? Brushes.Gray;
}
