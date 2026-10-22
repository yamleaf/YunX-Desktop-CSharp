using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace YunX.Desktop.Views;

/// <summary>
/// 无边框弹窗的窗口外壳。
///
/// <para><b>为什么要自绘</b>：原先对话框用 <c>WindowStyle.ToolWindow</c>（系统标题栏），
/// 客户区在 WPF 首帧生效之前由系统用 <c>COLOR_WINDOW</c>（白色）填充 ——
/// 那一帧白不受我们控制，就是用户反复看到的闪白。
/// 之前试过屏幕外渲染、Opacity、DwmFlush、CompositionTarget 计数…全是时序赌博，
/// 实测同一脚本有时 0/40、有时 28/40，根本不可靠。</para>
///
/// <para>改成自绘后，整个窗口（含标题栏和客户区）都是 WPF 内容，
/// 不存在"系统先填白"这一层，从结构上不可能闪白。</para>
///
/// <para><b>做法借鉴 worktime 项目的 RoundWindow</b>，两个关键点：</para>
/// <list type="number">
/// <item><b>用 ControlTemplate 做外壳</b>，不碰内容树。早先试图在 OnContentChanged 里
/// 把内容摘下来塞进 Border，会抛「在指定子级连接到新的父 Visual 之前必须断开连接」；
/// 换模板是 WPF 官方做法，完全不碰内容树。</item>
/// <item><b>WindowStyle=None + AllowsTransparency=false</b>：分层窗口会禁用 ClearType，
/// 文字退化成灰度抗锯齿。圆角改用 Win32 <c>SetWindowRgn</c> 裁剪，
/// 走普通窗口绘制路径，ClearType 照常可用。</item>
/// </list>
/// </summary>
public class DialogShell : Window
{
    /// <summary>圆角半径。</summary>
    protected const double ShellRadius = 10;

    private Border? _shell;
    private Brush _shellBg;
    private Brush _shellEdge;

    /// <summary>外壳底色。赋值时同步刷新，InitializeComponent 前后设都有效。</summary>
    protected Brush ShellBackground
    {
        get => _shellBg;
        set
        {
            _shellBg = value;
            if (_shell != null) SyncShellBackground();
            else ApplyShellTemplate();
        }
    }

    public DialogShell()
    {
        _shellBg = TryFindBrush("SurfaceBrush", new SolidColorBrush(Color.FromRgb(0x14, 0x17, 0x1E)));
        _shellEdge = TryFindBrush("BorderBrush", new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x38)));

        WindowStyle = WindowStyle.None;
        // 不分层：ClearType 才能生效，圆角交给 SetWindowRgn 裁
        AllowsTransparency = false;
        // Window 默认有 1px 边框内缩，会和 SetWindowRgn 的区域内露出未绘制的缝
        BorderThickness = new Thickness(0);
        Padding = new Thickness(0);
        Background = _shellBg;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;

        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);

        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        ApplyShellTemplate();

        SourceInitialized += (_, _) => ApplyRegionCorner();
        SizeChanged += (_, _) => ApplyRegionCorner();
    }

    // ---------------------------------------------------------------- 圆角

    /// <summary>
    /// 用 Win32 窗口区域裁圆角，而不是分层窗口。
    ///
    /// <para>分层窗口（AllowsTransparency=true）的 DWM 逐像素合成会禁用 ClearType，
    /// 文字只能退化成灰阶抗锯齿。SetWindowRgn 是 GDI 区域裁剪，
    /// 走普通窗口绘制路径，ClearType 照常可用。</para>
    /// </summary>
    private void ApplyRegionCorner()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        // 用客户区物理尺寸而不是 WPF 的 ActualWidth：
        // WindowStyle=None 且不分层时窗口仍是标准 Win32 窗口，客户区才是要裁的范围
        if (GetClientRect(hwnd, out RECT rc) == 0) return;
        int w = rc.Right - rc.Left, h = rc.Bottom - rc.Top;
        if (w <= 0 || h <= 0) return;

        double scale = 1.0;
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget != null)
            scale = source.CompositionTarget.TransformToDevice.M11;

        int d = Math.Max(2, (int)Math.Round(ShellRadius * 2 * scale));

        // 右下边界 +1：CreateRoundRectRgn 的区域是半开区间
        IntPtr rgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, d, d);
        if (rgn == IntPtr.Zero) return;
        // SetWindowRgn 成功后区域归系统所有，不能再 DeleteObject
        if (SetWindowRgn(hwnd, rgn, true) == 0) DeleteObject(rgn);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern int GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr hRgn, bool redraw);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    // ---------------------------------------------------------------- 外壳模板

    /// <summary>
    /// 用窗口模板做外壳（底色 + 圆角 + 1px 描边）。
    ///
    /// <para><b>不挂 DropShadowEffect</b>：位图特效会把整个子树先渲进离屏位图再卷积，
    /// 而位图表面不支持 ClearType —— 哪怕窗口不分层，挂在内容上的 Effect 也会让字变糊。</para>
    ///
    /// <para><b>非分层窗口画不出外投影</b>（没有窗口外的像素），
    /// 所以用一圈 1px 描边把窗口轮廓从背景里"切"出来。</para>
    /// </summary>
    private void ApplyShellTemplate()
    {
        var shell = new FrameworkElementFactory(typeof(Border));
        shell.Name = "ShellBorder";
        // CornerRadius 必须等于 SetWindowRgn 的半径，描边才会顺着圆角走；
        // 写 0 的话四角是方的，被区域一裁就断了，看起来像缺角
        shell.SetValue(Border.CornerRadiusProperty, new CornerRadius(ShellRadius));
        shell.SetValue(Border.BackgroundProperty, _shellBg);
        shell.SetValue(Border.BorderBrushProperty, _shellEdge);
        shell.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        shell.SetValue(Border.SnapsToDevicePixelsProperty, true);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        // AdornerDecorator 保证 FocusVisual / 校验提示等装饰层正常
        var adorner = new FrameworkElementFactory(typeof(System.Windows.Documents.AdornerDecorator));
        adorner.AppendChild(presenter);
        shell.AppendChild(adorner);

        var host = new FrameworkElementFactory(typeof(Grid));
        host.AppendChild(shell);

        Template = new ControlTemplate(typeof(DialogShell)) { VisualTree = host };

        Loaded += (_, _) =>
        {
            _shell = Template.FindName("ShellBorder", this) as Border;
            SyncShellBackground();
        };
    }

    /// <summary>底色同时刷到外壳与窗口本体，否则边缘会露出色差。</summary>
    private void SyncShellBackground()
    {
        Background = _shellBg;
        if (_shell != null) _shell.Background = _shellBg;
    }

    // ---------------------------------------------------------------- 交互

    private static Brush TryFindBrush(string key, Brush fallback)
    {
        try
        {
            var app = Application.Current;
            if (app != null && app.Resources.Contains(key)) return (Brush)app.Resources[key];
        }
        catch { }
        return fallback;
    }

    /// <summary>把某个元素作为窗口拖拽把手。</summary>
    protected void EnableDrag(UIElement handle)
    {
        handle.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != MouseButtonState.Pressed) return;
            try { DragMove(); }
            catch (InvalidOperationException) { /* 鼠标已释放 */ }
        };
    }

    /// <summary>
    /// 右上角关闭按钮。
    ///
    /// <para>用红色而不是中性灰：× 是"关闭/退出"的通用语义色。
    /// 常态略降饱和，悬停加深 + 淡红底，给出三级反馈。</para>
    /// </summary>
    protected Button CreateCloseButton()
    {
        var normal = ParseColor("#FFE14B4F");
        var hover = ParseColor("#FFC0343A");
        var hoverBg = ParseColor("#1FE0464A");

        var btn = new Button
        {
            Content = "✕",
            Width = 28,
            Height = 28,
            FontSize = 12,
            Cursor = Cursors.Hand,
            Foreground = normal,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            ToolTip = "关闭 (Esc)",
            Focusable = false,
        };
        btn.Template = BuildCloseTemplate(hoverBg);
        btn.MouseEnter += (_, _) => btn.Foreground = hover;
        btn.MouseLeave += (_, _) => btn.Foreground = normal;
        btn.Click += (_, _) => Close();
        return btn;
    }

    private static Brush ParseColor(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    private static ControlTemplate BuildCloseTemplate(Brush hoverBackground)
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(14));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.Name = "Bd";

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        var tpl = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var trigger = new Trigger { Property = IsMouseOverProperty, Value = true };
        trigger.Setters.Add(new Setter(Border.BackgroundProperty, hoverBackground, "Bd"));
        tpl.Triggers.Add(trigger);
        return tpl;
    }

    /// <summary>
    /// 统一的标题区：左侧标题，右侧关闭按钮。空白处可拖动窗口。
    /// </summary>
    public Grid BuildHeader(string title)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 0), Height = 38 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16, 0, 0, 0),
            Foreground = TryFindBrush("TextBrush", Brushes.White),
        };
        Grid.SetColumn(text, 0);
        grid.Children.Add(text);

        var close = CreateCloseButton();
        close.VerticalAlignment = VerticalAlignment.Center;
        close.Margin = new Thickness(0, 0, 6, 0);
        Grid.SetColumn(close, 1);
        grid.Children.Add(close);

        EnableDrag(grid);
        return grid;
    }
}
