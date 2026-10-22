using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using YunX.Core.Windows;
using YunX.Desktop.ViewModels;

namespace YunX.Desktop;

public partial class MainWindow : Window
{
    private TrayIcon? _tray;
    private SleepBlocker? _sleepBlocker;
    private TaskbarProgress? _taskbar;
    private bool _exiting;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    /// <summary>
    /// 下载完成提示。
    ///
    /// <para><b>只在窗口不活跃时弹</b>：用户正盯着进度条时再弹一个气泡是噪音，
    /// 而且托盘气泡在 Windows 上会抢占焦点提示区。切到后台/最小化时才值得提醒。</para>
    /// </summary>
    private void OnTaskCompleted(string fileName, string sizeText)
    {
        // 前台窗口就是自己 → 用户看得见，不打扰
        if (IsActive && WindowState != WindowState.Minimized) return;

        try
        {
            _tray?.ShowBalloon("下载完成", $"{fileName} · {sizeText}");
        }
        catch
        {
            // 通知失败不该影响任何东西
        }
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // PasswordBox 不支持双向绑定，需要手动同步
        if (!string.IsNullOrEmpty(Vm.GitHubToken))
            TokenBox.Password = Vm.GitHubToken;
        TokenBox.PasswordChanged += OnTokenPasswordChanged;

        InputBox.Focus();

        // 日志抽屉展开时把箭头翻转，给一个明确的状态指示
        Vm.PropertyChanged += OnVmPropertyChanged;

        // 剪贴板提示卡片滑入
        Vm.ClipboardHintShown += AnimateClipboardHint;
        Vm.TaskCompleted += OnTaskCompleted;

        // 「回到顶部」：VM 不碰 ScrollViewer，只发事件，由窗口执行滚动
        Vm.ScrollParseToTopRequested += ScrollParseToTop;

        SetupWindowsIntegration();

        // 淡入：避免窗口「啪」地出现。150ms 足够快，长于 250ms 会显得拖沓。
        Opacity = 0;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    // ================= 系统集成 =================

    /// <summary>
    /// 托盘 + 任务栏进度。
    ///
    /// <para>托盘用原生 <c>Shell_NotifyIcon</c> + <c>TrackPopupMenu</c> 而不是 WPF 的
    /// <c>NotifyIcon</c>（后者在 WinForms 里，为了一个图标引入 WinForms 引用不值得），
    /// 而且原生菜单会自动跟随系统深浅色 —— 这台机器上就不需要额外的 darkmode 处理。</para>
    /// </summary>
    private void SetupWindowsIntegration()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            _tray = new TrayIcon(hwnd, "YunX Desktop")
            {
                MenuItems =
                {
                    (1, "显示主窗口"),
                    (2, "复制当前速度"),
                    (3, null),                      // 分隔线
                    (4, "清空已完成任务"),
                    (5, "打开下载目录"),
                    (6, "全部终止"),
                    (7, null),
                    (8, "退出"),
                },
            };

            // 托盘图标必须显式给：不给就是一块空白占位（NIF_ICON 置位但 hIcon 为空）。
            // 尺寸按当前 DPI 算，高 DPI 下不会糊。
            int iconSize = (int)Math.Round(16 * VisualTreeHelper.GetDpi(this).DpiScaleX);
            var hIcon = TrayIcon.LoadAppIcon(iconSize);
            if (hIcon != IntPtr.Zero) _tray.SetIcon(hIcon);

            _tray.LeftClick += RestoreFromTray;
            _tray.MenuClicked += OnTrayMenu;
            _tray.Install();
        }
        catch
        {
            // 托盘装不上不该影响主功能
            _tray = null;
        }

        _sleepBlocker = new SleepBlocker();
        _taskbar = new TaskbarProgress(hwnd);
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private void OnTrayMenu(int id)
    {
        switch (id)
        {
            case 1:
                RestoreFromTray();
                break;
            case 2:
                // 托盘里最常做的两件事之一：把当前速度拷给别人看 / 贴进聊天窗口
                try
                {
                    Clipboard.SetText(Vm.RunningCount > 0
                        ? $"{Vm.TotalSpeedText}（{Vm.TotalPercent:0}%）"
                        : "当前没有下载任务");
                }
                catch { /* 剪贴板被别的进程占用，忽略 */ }
                break;
            case 4:
                Vm.ClearFinishedCommand.Execute(null);
                break;
            case 5:
                Vm.OpenDownloadDirCommand.Execute(null);
                break;
            case 6:
                Vm.CancelAllCommand.Execute(null);
                break;
            case 8:
                _exiting = true;
                Close();
                break;
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.TotalPercent):
            case nameof(MainViewModel.RunningCount):
            // KeepAwake 也要触发刷新：否则用户在下载途中关掉「阻止睡眠」，
            // 已持有的系统阻塞不会释放，要等所有任务结束才生效。
            case nameof(MainViewModel.KeepAwake):
                UpdateTaskbarProgress();
                break;

            // 日志抽屉展开时把箭头翻转，给一个明确的状态指示
            case nameof(MainViewModel.ShowLog):
                if (LogChevron.RenderTransform is RotateTransform rt)
                    rt.Angle = Vm.ShowLog ? 180 : 0;
                break;
        }
    }

    private void OnTokenPasswordChanged(object sender, RoutedEventArgs e) =>
        Vm.GitHubToken = TokenBox.Password;

    private void UpdateTaskbarProgress()
    {
        if (_taskbar == null) return;

        bool running = Vm.RunningCount > 0;
        if (running)
        {
            _taskbar.SetProgress(Math.Clamp(Vm.TotalPercent / 100.0, 0, 1));

            // 有任务在跑就阻止系统睡眠；只阻睡眠、允许关屏，否则用户合盖都合不上。
            //
            // 这里必须成对处理：早期只在 KeepAwake 为真时 Acquire，从不在它为假时 Release，
            // 于是「下载中把开关关掉」不会立刻生效 —— 已经持有的阻塞要等所有任务结束才释放。
            // Acquire/Release 都是幂等的（内部有引用计数/标志），重复调用安全。
            if (Vm.KeepAwake) _sleepBlocker?.Acquire();
            else _sleepBlocker?.Release();
        }
        else
        {
            _taskbar.Clear();
            _sleepBlocker?.Release();
        }

        // 托盘提示跟着更新，鼠标悬停能看到当前状态
        _tray?.SetTooltip(running
            ? $"YunX Desktop · {Vm.TotalPercent:0}% · {Vm.TotalSpeedText}"
            : "YunX Desktop · 空闲");
    }

    /// <summary>
    /// 把解析页滚回顶部。
    ///
    /// <para><b>为什么要有这个按钮</b>：解析出几十个文件时，用户勾完选在列表底部，
    /// 顶部那个「开始下载」已经滚出视野。底部也放了下载按钮，但仍可能需要回顶部看题目/链接，
    /// 所以给一条明确的回路，而不是让用户自己拖滚动条。</para>
    ///
    /// <para>自己算缓动而不是用 <see cref="DoubleAnimation"/>：ScrollViewer 的
    /// <c>VerticalOffset</c> 不是依赖属性，无法直接对其做动画，
    /// 只能定时调用 <c>ScrollToVerticalOffset</c>。瞬间跳变会让人分不清是
    /// "页面重绘了"还是"滚上去了"，动画给出方向感。</para>
    /// </summary>
    private void ScrollParseToTop()
    {
        var sv = ParseScrollViewer;
        if (sv == null || sv.VerticalOffset <= 0) return;

        double from = sv.VerticalOffset;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(16),
        };
        var sw = System.Diagnostics.Stopwatch.StartNew();

        timer.Tick += (_, _) =>
        {
            double t = Math.Min(1.0, sw.Elapsed.TotalMilliseconds / 260.0);
            double eased = 1 - Math.Pow(1 - t, 3);   // ease-out cubic
            sv.ScrollToVerticalOffset(from * (1 - eased));
            if (t >= 1.0) timer.Stop();
        };
        timer.Start();
    }

    /// <summary>提示卡片滑入：从下方 12px 处淡入。</summary>
    private void AnimateClipboardHint()
    {
        // 绑定刚把 Visibility 改成 Visible，等布局跑完再动，否则动画会被第一次布局覆盖
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (HintTransform == null) return;

            HintTransform.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(220))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                });

            ClipboardHintCard.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        }), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    /// <summary>输入框内回车 = 解析。</summary>
    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (Vm.ParseCommand.CanExecute(null)) Vm.ParseCommand.Execute(null);
        e.Handled = true;
    }

    // ================= 自绘标题栏 =================

    private void OnMinimize(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>最大化状态变化时换图标（最大化 ↔ 还原）。</summary>
    private void OnStateChanged(object? sender, EventArgs e)
    {
        string key = WindowState == WindowState.Maximized ? "IconRestore" : "IconMaximize";
        if (TryFindResource(key) is Geometry geo) MaxIcon.Data = geo;
    }

    // ================= 关闭行为 =================

    /// <summary>
    /// 关闭时按设置三选一：询问 / 直接退出 / 最小化到托盘。
    ///
    /// <para>「最小化到托盘」这个选项必须有 —— 下载任务跑着的时候用户按 ×，
    /// 十有八九是想把窗口收起来，不是要中断下载。</para>
    /// </summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting) return;

        int running = Vm.Tasks.Count(t => t.CanCancel);

        if (running > 0 && Vm.CloseBehavior == "tray" && _tray != null)
        {
            e.Cancel = true;
            Hide();
            _tray.ShowBalloon("仍在下载", $"{running} 个任务继续在后台下载，双击托盘图标可恢复窗口");
            return;
        }

        if (running > 0)
        {
            // 用项目自己的深色对话框，而不是系统 MessageBox（后者在深色界面里是白底）
            int pick = Views.PromptDialog.Choose(
                "仍在下载",
                $"还有 {running} 个任务在下载，确定退出？\n\n未完成的部分会保留断点，下次可续传。",
                "退出");
            if (pick != 0)
            {
                e.Cancel = true;
                return;
            }
        }

        _exiting = true;
        Vm.Shutdown();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        // 全部退订：匿名 lambda 无法 -=，会把整个视觉树钉在长生命周期的 ViewModel 上
        Vm.PropertyChanged -= OnVmPropertyChanged;
        Vm.ClipboardHintShown -= AnimateClipboardHint;
        Vm.TaskCompleted -= OnTaskCompleted;
        TokenBox.PasswordChanged -= OnTokenPasswordChanged;

        _sleepBlocker?.Dispose();
        _taskbar?.Dispose();
        _tray?.Dispose();
    }
}
