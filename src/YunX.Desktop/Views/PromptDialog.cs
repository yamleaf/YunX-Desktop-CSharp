using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace YunX.Desktop.Views;

/// <summary>
/// 轻量输入对话框（替代系统 MessageBox 的输入场景）。
///
/// <para>自己实现而不是用 <c>Microsoft.VisualBasic.Interaction.InputBox</c>：
/// 后者外观是 Win9x 风格，与深色界面完全不搭，而且无法做密码掩码。</para>
/// </summary>
public sealed class PromptDialog : DialogShell
{
    private readonly TextBox _input;
    private readonly PasswordBox? _secret;
    private bool _ok;

    private PromptDialog(string title, string message, string? secondLabel, bool secret)
    {
        Title = title;
        Width = 460;
        // 自绘外壳：不设 SizeToContent（由 DialogShell 决定窗口尺寸），
        // 高度在 LockSizeBeforeShow 里量出来写死，避免"先小后大"的跳动。
        SizeToContent = SizeToContent.Manual;

        var root = new StackPanel { Margin = new Thickness(20) };

        root.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextBrush"],
            Margin = new Thickness(0, 0, 0, 10),
        });

        if (secret)
        {
            _secret = new PasswordBox { Height = 34 };
            if (Application.Current.Resources["InputPassword"] is Style ps)
                _secret.Style = ps;
            root.Children.Add(_secret);
            _input = new TextBox();   // 占位，未使用
        }
        else
        {
            _input = new TextBox { Height = 34 };
            if (Application.Current.Resources["InputBox"] is Style ts)
                _input.Style = ts;
            root.Children.Add(_input);
            _secret = null;
        }

        if (secondLabel != null)
        {
            // 双输入模式（账号 + 密码）：把第一个框换成普通输入，第二个用密码框
            root.Children.Clear();
            root.Children.Add(new TextBlock
            {
                Text = message, TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextBrush"],
                Margin = new Thickness(0, 0, 0, 10),
            });

            root.Children.Add(new TextBlock
            {
                Text = "账号", Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
                Margin = new Thickness(0, 0, 0, 4),
            });
            _input = new TextBox { Height = 34 };
            if (Application.Current.Resources["InputBox"] is Style ts2) _input.Style = ts2;
            root.Children.Add(_input);

            root.Children.Add(new TextBlock
            {
                Text = secondLabel,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
                Margin = new Thickness(0, 10, 0, 4),
            });
            _secret = new PasswordBox { Height = 34 };
            if (Application.Current.Resources["InputPassword"] is Style ps2) _secret.Style = ps2;
            root.Children.Add(_secret);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        var cancel = new Button { Content = "取消", MinWidth = 80 };
        if (Application.Current.Resources["GhostButton"] is Style gb) cancel.Style = gb;
        cancel.Click += (_, _) => { _ok = false; Close(); };

        var ok = new Button { Content = "确定", MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        if (Application.Current.Resources["PrimaryButton"] is Style pb) ok.Style = pb;
        ok.Click += (_, _) => { _ok = true; Close(); };
        ok.IsDefault = true;

        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);

        // 自绘外壳：标题区 + 内容。标题区空白处可拖动，右上角是关闭按钮。
        var outer = new StackPanel();
        outer.Children.Add(BuildHeader(title));
        var sep = new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["BorderBrush"],
        };
        outer.Children.Add(sep);
        outer.Children.Add(root);

        Content = outer;

        Loaded += (_, _) =>
        {
            if (_secret != null) _secret.Focus();
            else _input.Focus();
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _ok = false; Close(); }
        };

        // Content 已就位，此时量出的高度才是最终高度
        LockSizeBeforeShow(this);
        CenterOnScreen(this);
    }

    /// <summary>
    /// 给对话框设置 Owner（父窗口）。
    ///
    /// <para><b>为什么要判断</b>：WPF 不允许把窗口的 Owner 设为它自己，
    /// 会抛 <c>ArgumentException: 无法将 Owner 属性设置为它本身</c>。
    /// 而从主窗口触发的对话框，<c>Application.Current.MainWindow</c> 有可能就是
    /// 发起调用的那个窗口 —— 直接赋值就会崩。</para>
    ///
    /// <para>另外 MainWindow 在启动早期可能还是 null，也要跳过。</para>
    /// </summary>
    private static void AttachOwner(Window dialog, Window? owner = null)
    {
        owner ??= Application.Current?.MainWindow;
        if (owner == null) return;          // 没有主窗口（启动期），不设 Owner
        if (ReferenceEquals(owner, dialog)) return;   // 不能设成自己
        dialog.Owner = owner;
    }

    /// <summary>
    /// 在 ShowDialog 之前把尺寸算准，让窗口**第一帧就是最终大小**。
    ///
    /// <para><see cref="Window.SizeToContent"/>=Height 的窗口如果直接 ShowDialog，
    /// 系统会先用默认尺寸画一帧，等布局完再撑大 —— 那一帧就是白闪的来源。</para>
    /// </summary>
    private static void LockSizeBeforeShow(Window dialog)
    {
        dialog.SizeToContent = SizeToContent.Manual;

        // 宽度已知时，只需量出高度。用无限高约束测一次，拿到内容真实高度。
        dialog.Measure(new Size(dialog.Width > 0 ? dialog.Width : 520, double.PositiveInfinity));
        var desired = dialog.DesiredSize;

        double h = desired.Height;
        if (double.IsNaN(h) || h <= 0) h = 200;
        dialog.Height = h;
    }

    /// <summary>
    /// 把对话框放到**屏幕正中**（考虑任务栏与多显示器）。
    ///
    /// <para><b>为什么不用 <see cref="WindowStartupLocation.CenterOwner"/></b>：
    /// 它依赖 Owner 是否设置成功。而我们为了避免「Owner 不能是自己」的异常，
    /// 在某些情况下会跳过设置 Owner —— 这时 CenterOwner 会退化成默认的左上角
    /// （0,0），对话框就跑到屏幕左上角去了。</para>
    ///
    /// <para>直接按显示器工作区算坐标，不依赖 Owner 状态，也不会压到任务栏。
    /// 用纯 Win32（MONITORINFO）而不是 WinForms 的 Screen ——
    /// 为了取个屏幕尺寸就打开 UseWindowsForms 不划算。</para>
    /// </summary>
    private static void CenterOnScreen(Window dialog)
    {
        // 优先用实际渲染尺寸（ContentRendered 时已确定），
        // 退而用 Width/Height（Choose 走这条，构造时就设了）。
        double w = dialog.ActualWidth > 0 ? dialog.ActualWidth : dialog.Width;
        double h = dialog.ActualHeight > 0 ? dialog.ActualHeight : dialog.Height;
        if (double.IsNaN(w) || w <= 0) w = 520;
        if (double.IsNaN(h) || h <= 0) h = 200;

        // 基准窗口：优先主窗口（对话框跟随主窗口所在那块屏），否则用对话框自己
        var basis = Application.Current?.MainWindow;
        IntPtr basisHandle = basis != null
            ? new System.Windows.Interop.WindowInteropHelper(basis).Handle
            : IntPtr.Zero;

        var work = GetWorkArea(basisHandle);
        int workW = work.Right - work.Left;
        int workH = work.Bottom - work.Top;

        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Left = work.Left + (workW - w) / 2;
        dialog.Top = work.Top + (workH - h) / 2;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;      // 工作区：已排除任务栏
        public uint dwFlags;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    /// <summary>取指定窗口所在显示器的工作区；拿不到就退回主显示器尺寸。</summary>
    private static RECT GetWorkArea(IntPtr hwnd)
    {
        const uint MonitorDefaultToNearest = 2;

        try
        {
            IntPtr mon = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (mon != IntPtr.Zero)
            {
                var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(mon, ref mi)) return mi.rcWork;
            }
        }
        catch { /* 落到下面的兜底 */ }

        // 兜底：用 WPF 的主屏尺寸，扣掉大致任务栏高度
        return new RECT
        {
            Left = 0,
            Top = 0,
            Right = (int)SystemParameters.PrimaryScreenWidth,
            Bottom = (int)SystemParameters.PrimaryScreenHeight,
        };
    }

    /// <summary>单输入。</summary>
    /// <param name="initial">预填内容（重命名时传入当前名字，用户在上面改）。</param>
    public static string? Ask(string title, string message, bool secret = false, string? initial = null)
    {
        var dlg = new PromptDialog(title, message, null, secret);
        AttachOwner(dlg);

        // 密码框不支持预填（没有对应语义），普通输入才填
        if (!secret && !string.IsNullOrEmpty(initial))
        {
            dlg._input.Text = initial;
            // 预填后全选：用户想改就直接输入覆盖，想保留就直接回车
            dlg.Loaded += (_, _) => dlg._input.SelectAll();
        }

        dlg.ShowDialog();
        if (!dlg._ok) return null;
        return secret ? dlg._secret?.Password : dlg._input.Text;
    }

    /// <summary>双输入（账号 + 密码）。</summary>
    public static (string? Account, string? Password) AskTwo(string title, string firstLabel,
        string secondLabel, bool secret = true)
    {
        var dlg = new PromptDialog(title, firstLabel, secondLabel, secret: false);
        AttachOwner(dlg);
        dlg.ShowDialog();
        if (!dlg._ok) return (null, null);
        return (dlg._input.Text, dlg._secret?.Password);
    }

    /// <summary>
    /// 底栏里的一个按钮：撑满自己那一格（配合外层的等分 Grid）。
    /// </summary>
    private static Button MakeButton(string text, int column, bool isPrimary,
        Action onClick, bool isDefault)
    {
        var btn = new Button
        {
            Content = text,
            Height = 36,
            // Stretch：在等分列里撑满整格；在 Auto 列里由内容决定宽度，不会被拉伸影响
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            // 间距由外层（缝隙列 / Margin）负责，这里不加，否则等分会失真
            Margin = new Thickness(0),
            IsDefault = isDefault,
        };

        string key = isPrimary ? "PrimaryButton" : "GhostButton";
        if (Application.Current.Resources[key] is Style s) btn.Style = s;

        btn.Click += (_, _) => onClick();
        Grid.SetColumn(btn, column);
        return btn;
    }

    /// <summary>
    /// 多选一（登录方式选择 / 危险操作确认）。
    /// 返回**选项下标**；取消返回 -1。
    ///
    /// <para>为什么不用 MessageBox：它只支持「是/否/取消」三种固定文案，
    /// 而这里要「内嵌登录 / 从浏览器导入 / 手动粘贴」三个业务动作。</para>
    ///
    /// <para><b>注意</b>：取消按钮由本方法自动追加，调用方**不要**再把「取消」
    /// 当成一个 option 传进来 —— 那会出现「清空 / 取消 / 取消」三个按钮。</para>
    /// </summary>
    public static int Choose(string title, string message, params string[] options)
    {
        // 宽度按按钮数量放大：4 个按钮挤在 520px 里每格只有 ~120px，
        // 像「从本机浏览器导入（推荐）」这种长文案必然被切。
        // 中文 13px 左右一个按钮文案约 14 字 = ~185px，留 190px/个足够。
        int buttonCount = options.Length + 1;
        double minWidth = Math.Max(520, buttonCount * 190 + 56);

        // 用自绘外壳，不用系统标题栏 —— 系统标题栏的客户区会在 WPF 首帧之前
        // 被系统填成白色，那正是闪白的来源。
        var win = new DialogShell
        {
            Title = title,
            Width = minWidth,
            SizeToContent = SizeToContent.Manual,
        };

        // Owner 单独设置：不能写进初始化器（要判断"是不是自己"，避免
        // ArgumentException: 无法将 Owner 属性设置为它本身）
        AttachOwner(win);

        int result = -1;
        var root = new StackPanel { Margin = new Thickness(20) };

        root.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextBrush"],
            Margin = new Thickness(0, 0, 0, 16),
        });

        // 单个选项 = 确认类对话框（如"清空收藏"），按钮横排在右下角，符合 Windows 惯例。
        // 多个选项 = 选择类对话框（如登录方式），竖排整行更好点，也放得下更长的文案。
        // ── 底部按钮栏：所有选项 + 取消，等宽均分整行 ──
        // 统一的底栏让不同对话框长得一致，也避免"选项竖排像菜单、取消孤零零在右下"的割裂感。
        // 分隔线把正文和操作区分开，是标准对话框的做法。
        root.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["BorderBrush"],
            Margin = new Thickness(-20, 18, -20, 0),   // 负边距抵消 root 的 20px，让线通到窗口两侧
        });

        var bar = new Grid { Margin = new Thickness(0, 14, 0, 0) };

        // ── 全部等分 ──
        // 每个按钮占一列 *，列之间插 8px 固定缝隙列（用 Margin 做间隔会让各格宽度不等）。
        // 按钮多的时候窗口已经按数量加宽了（见上面的 minWidth），所以每格仍然够放文案。
        //
        // 顺序：单选项（确认类）「取消」在左、主操作在右，符合 Windows 习惯；
        //       多选项「取消」放最后，扫一遍就是"选一个，或者不选"。
        int buttonTotal = options.Length + 1;
        for (int i = 0; i < buttonTotal; i++)
        {
            if (i > 0) bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        if (options.Length == 1)
        {
            bar.Children.Add(MakeButton("取消", 0, isPrimary: false,
                onClick: () => { result = -1; win.Close(); }, isDefault: false));

            bar.Children.Add(MakeButton(options[0], 2, isPrimary: true,
                onClick: () => { result = 0; win.Close(); }, isDefault: true));
        }
        else
        {
            for (int i = 0; i < options.Length; i++)
            {
                int idx = i;
                bar.Children.Add(MakeButton(options[i], i * 2, isPrimary: false,
                    onClick: () => { result = idx; win.Close(); }, isDefault: false));
            }

            bar.Children.Add(MakeButton("取消", options.Length * 2, isPrimary: false,
                onClick: () => { result = -1; win.Close(); }, isDefault: false));
        }

        root.Children.Add(bar);

        // 自绘外壳：标题 + 分隔线 + 内容
        var outer = new StackPanel();
        outer.Children.Add(win.BuildHeader(title));
        outer.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["BorderBrush"],
        });
        outer.Children.Add(root);

        win.Content = outer;

        // 内容就位后锁尺寸，再居中
        LockSizeBeforeShow(win);
        CenterOnScreen(win);

        win.ShowDialog();
        return result;
    }
}
