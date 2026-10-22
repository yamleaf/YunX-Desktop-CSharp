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
        // ★ 高度交给 WPF 自己量，不要手动 Measure 再写死。
        //
        //   早期这里 `dialog.Measure(width, ∞)` 后把 DesiredSize.Height 赋给 Height。
        //   但窗口还没进视觉树时，内容根本没参与布局，量出来接近 0，
        //   于是落到兜底的 200 —— 而「删除下载记录」这类多行正文的弹窗实际要 230+，
        //   按钮就被挤到窗口底部之外，只露出上半截。
        //
        //   SizeToContent.Height 会在真正显示时按内容算高度，是这件事的权威做法。
        //   宽度仍然显式给（由 Choose 按按钮数量算好），不受影响。
        dialog.SizeToContent = SizeToContent.Height;

        // 高度上限：长正文不至于把窗口顶出屏幕。超过时正文区自己滚动。
        double maxH = SystemParameters.WorkArea.Height * 0.8;
        if (dialog.MaxHeight <= 0 || dialog.MaxHeight > maxH) dialog.MaxHeight = maxH;
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

        // ★ 高度未知时不能瞎猜。
        //   SizeToContent.Height 的窗口在显示前 Height 是 NaN，早期这里兜底成 200，
        //   结果多行正文的弹窗位置偏上（按钮还容易贴到屏幕边缘）。
        //   现在：等布局真正算完再摆位置。
        if (double.IsNaN(h) || h <= 0)
        {
            dialog.SizeToContent = SizeToContent.Height;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Loaded += (_, _) =>
            {
                double realH = dialog.ActualHeight > 0 ? dialog.ActualHeight : dialog.Height;
                PlaceCentered(dialog, dialog.ActualWidth > 0 ? dialog.ActualWidth : dialog.Width,
                    realH);
            };
            return;
        }

        PlaceCentered(dialog, w, h);
    }

    /// <summary>按显示器工作区把窗口摆到正中（w/h 必须是已知的真实尺寸）。</summary>
    private static void PlaceCentered(Window dialog, double w, double h)
    {
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
    /// 告警弹窗：只有一个「知道了」按钮。
    ///
    /// <para><b>与 <see cref="Choose"/> 的区别</b>：Choose 是"要你做决定"，
    /// Alert 是"告知你出事了"。后者不该让用户在一个选项里挑，
    /// 也不该有"取消/确定"双按钮 —— 那暗示了还有选择的余地。</para>
    ///
    /// <para>标题栏的 ✕ 与「知道了」等价（都只是关闭），不改变任何状态。</para>
    /// </summary>
    /// <param name="detail">补充说明（可能较长，正文区可滚动）</param>
    public static void Alert(string title, string message, string primaryText = "知道了")
    {
        var win = new DialogShell
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Manual,
        };
        AttachOwner(win);

        var root = new StackPanel { Margin = new Thickness(20) };

        // 与 Choose 一致：正文可滚动，避免长文案把按钮挤出窗口
        root.Children.Add(new ScrollViewer
        {
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextBrush"],
                LineHeight = 20,
            },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            MaxHeight = 360,
            Margin = new Thickness(0, 0, 0, 16),
        });

        root.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["BorderBrush"],
            Margin = new Thickness(-20, 18, -20, 0),
        });

        // 按钮右对齐、不用整行均分：确认类弹窗只该有一个动作，
        // 铺满整行会让它显得像"选择"而不是"确认"。
        var bar = new Grid { Margin = new Thickness(0, 14, 0, 0) };
        var btn = MakeButton(primaryText, 0, isPrimary: true,
            onClick: () => win.Close(), isDefault: true);
        btn.HorizontalAlignment = HorizontalAlignment.Right;
        btn.MinWidth = 96;
        bar.Children.Add(btn);
        root.Children.Add(bar);

        var outer = new StackPanel();
        outer.Children.Add(win.BuildHeader(title));
        outer.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)Application.Current.Resources["BorderBrush"],
        });
        outer.Children.Add(root);
        win.Content = outer;

        LockSizeBeforeShow(win);
        CenterOnScreen(win);
        win.ShowDialog();
    }

    /// <summary>
    /// 多选一（登录方式选择 / 危险操作确认）。
    /// 返回**选项下标**；取消返回 -1。
    ///
    /// <para>为什么不用 MessageBox：它只支持「是/否/取消」三种固定文案，
    /// 而这里要「内嵌登录 / 从浏览器导入 / 手动粘贴」三个业务动作。</para>
    ///
    /// <para><b>注意</b>：不再自动追加「取消」按钮 —— 标题栏右上角的 ✕ 就是取消
    /// （关窗返回 -1）。调用方也不必再传一个「取消」option。</para>
    /// </summary>
    /// <param name="primaryIndex">
    /// 用强调色显示的选项下标；<b>-1 表示没有主操作</b>（全部灰底）。
    ///
    /// <para><b>为什么必须显式指定而不是按位置猜</b>：选项顺序的语义并不一致 ——
    /// 「覆盖原文件 / 自定义文件名」里安全的是后者，
    /// 「仅删除记录 / 记录和文件一起删除」里危险的是后者，
    /// 而「内嵌登录 / 从浏览器导入 / 手动粘贴」根本没有主次。
    /// 靠「最后一个 / 第一个」去推断总会错一半，所以交给调用方说清楚。</para>
    /// </param>
    public static int Choose(string title, string message, params string[] options)
        => ChooseWithPrimary(title, message, -1, options);

    /// <summary>带主操作指示的 <see cref="Choose"/>。见 <c>primaryIndex</c> 的说明。</summary>
    public static int ChooseWithPrimary(string title, string message, int primaryIndex,
        params string[] options)
    {
        // 宽度按按钮数量放大：按钮挤在 520px 里每格太窄，长文案必然被切。
        // 中文 13px 左右一个按钮文案约 14 字 = ~185px，留 190px/个足够。
        // 取消了「取消」按钮，数量就是 options.Length（关窗 = 取消，见上）。
        int buttonCount = Math.Max(1, options.Length);
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

        // 正文放进可滚动容器：路径很长 / 行数很多时，窗口高度被 MaxHeight 限制，
        // 按钮栏会被挤出可视区 —— 那是最恶劣的情况（看得见提示、点不到按钮）。
        var messageBlock = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextBrush"],
        };
        root.Children.Add(new ScrollViewer
        {
            Content = messageBlock,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            // 上限按窗口高度的一半留给正文，其余给标题栏与按钮栏
            MaxHeight = 320,
            Margin = new Thickness(0, 0, 0, 16),
            // 正文里可能出现很长且无空格的路径，必须允许换行（默认已 Wrap）
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
        // ★ 不再放「取消」按钮：标题栏右上角的 ✕ 就是取消（关窗 = 返回 -1），
        //   再放一个纯属重复，还把有限宽度挤给主操作、导致文案被切。
        var buttons = options;
        int buttonTotal = buttons.Length;
        for (int i = 0; i < buttonTotal; i++)
        {
            if (i > 0) bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        for (int i = 0; i < buttons.Length; i++)
        {
            int idx = i;
            // 主操作由调用方指定；未指定（-1）时全部灰底，表示「纯选择，无主次」。
            bool primary = i == primaryIndex;
            bar.Children.Add(MakeButton(buttons[i], i * 2, isPrimary: primary,
                onClick: () => { result = idx; win.Close(); }, isDefault: primary));
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
