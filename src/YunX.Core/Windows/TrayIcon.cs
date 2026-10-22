using System.Runtime.InteropServices;

namespace YunX.Core.Windows;

/// <summary>
/// 系统托盘图标 + 原生右键菜单。
///
/// <para>用 Win32 <c>Shell_NotifyIcon</c> 而不是 WinForms 的 <c>NotifyIcon</c>，
/// 原因：<c>NotifyIcon</c> 会连带引入 WinForms 程序集（单文件发布体积 +5MB），
/// 且它的菜单是 WinForms 自绘，深色模式下与系统风格不一致。</para>
///
/// <para>原生 <c>TrackPopupMenu</c> 会自动跟随系统的深色/浅色主题，不需要像原项目那样
/// 额外编一个 <c>darkmode.dll</c> 桥接层。</para>
/// </summary>
public sealed class TrayIcon : IDisposable
{
    // ================= Win32 声明 =================

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public int uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public int dwState;
        public int dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public int dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem,
        string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y,
        int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint PrivateExtractIcons(string szFileName, int nIconIndex,
        int cxIcon, int cyIcon, IntPtr[] phicon, uint[]? piconid, uint nIcons, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex,
        IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    private const int NIM_ADD = 0;
    private const int NIM_MODIFY = 1;
    private const int NIM_DELETE = 2;

    private const int NIF_MESSAGE = 0x01;
    private const int NIF_ICON = 0x02;
    private const int NIF_TIP = 0x04;
    private const int NIF_INFO = 0x10;

    private const int NIIF_INFO = 0x01;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_BOTTOMALIGN = 0x0020;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private const int WM_TRAY_CALLBACK = 0x0400 + 1024;   // WM_USER + 1024
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;

    // ================= 状态 =================

    private NOTIFYICONDATA _data;
    private IntPtr _hIcon;
    private bool _added;
    private readonly IntPtr _ownerHwnd;

    /// <summary>左键点击（恢复主窗口）。</summary>
    public event Action? LeftClick;

    /// <summary>菜单项点击，参数为菜单项 ID。</summary>
    public event Action<int>? MenuClicked;

    /// <summary>菜单项定义：ID → 文本；null 表示分隔线。</summary>
    public List<(int Id, string? Text)> MenuItems { get; } = new();

    public TrayIcon(IntPtr ownerHwnd, string tooltip = "YunX Desktop")
    {
        _ownerHwnd = ownerHwnd;
        _data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = ownerHwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAY_CALLBACK,
            szTip = tooltip,
            szInfo = "",
            szInfoTitle = "",
        };
    }

    /// <summary>
    /// 设置图标（从可执行文件提取，避免额外资源文件）。
    /// </summary>
    public void SetIcon(IntPtr hIcon)
    {
        _hIcon = hIcon;
        _data.hIcon = hIcon;
        _data.uFlags |= NIF_ICON;
        // 已经注册过就立即刷新，否则要等到下次 Install
        if (_added) Shell_NotifyIcon(NIM_MODIFY, ref _data);
    }

    /// <summary>
    /// 从可执行文件里取第一个图标，作为托盘图标句柄。
    ///
    /// <para><b>为什么必须调这个</b>：构造后 <c>_data.hIcon</c> 是 <c>IntPtr.Zero</c>，
    /// 而 <c>NIF_ICON</c> 标志是置位的 —— 注册出来就是一个<b>没有图案的托盘占位</b>，
    /// 用户在右下角只能看到一块空白，点也不好点。</para>
    ///
    /// <para>用 <c>PrivateExtractIcons</c> 而不是 <c>ExtractIconEx</c>：前者可以指定
    /// 像素尺寸，高 DPI（150%/200%）下托盘要 24/32px，直接给 16px 会被系统拉糊。
    /// 取不到时回退到 <c>ExtractIconEx</c> 的系统度量尺寸。</para>
    /// </summary>
    /// <param name="size">期望边长（像素）。</param>
    /// <returns>HICON；失败返回 <see cref="IntPtr.Zero"/>（调用方应跳过 SetIcon）。</returns>
    public static IntPtr LoadAppIcon(int size = 16)
    {
        string exe;
        try
        {
            exe = Environment.ProcessPath ?? "";
            if (exe.Length == 0) return IntPtr.Zero;
        }
        catch
        {
            return IntPtr.Zero;
        }

        try
        {
            var icons = new IntPtr[1];
            uint got = PrivateExtractIcons(exe, 0, Math.Max(8, size), Math.Max(8, size),
                icons, null, 1, 0);
            if (got > 0 && icons[0] != IntPtr.Zero) return icons[0];
        }
        catch { /* 落到下面的回退 */ }

        try
        {
            var small = new IntPtr[1];
            uint n = ExtractIconEx(exe, 0, null, small, 1);
            if (n > 0 && small[0] != IntPtr.Zero) return small[0];
        }
        catch { /* 没有图标资源 */ }

        return IntPtr.Zero;
    }

    /// <summary>注册托盘图标。</summary>
    public bool Install()
    {
        if (_added) return true;
        _added = Shell_NotifyIcon(NIM_ADD, ref _data);
        return _added;
    }

    /// <summary>更新提示文字（如显示当前速度）。</summary>
    public void SetTooltip(string text)
    {
        if (!_added) return;
        _data.szTip = text.Length > 120 ? text[..120] : text;
        Shell_NotifyIcon(NIM_MODIFY, ref _data);
    }

    /// <summary>弹出气泡通知（完成提示）。</summary>
    public void ShowBalloon(string title, string message)
    {
        if (!_added) return;
        _data.uFlags |= NIF_INFO;
        _data.szInfoTitle = title.Length > 60 ? title[..60] : title;
        _data.szInfo = message.Length > 250 ? message[..250] : message;
        _data.dwInfoFlags = NIIF_INFO;
        Shell_NotifyIcon(NIM_MODIFY, ref _data);
        _data.uFlags &= ~NIF_INFO;
    }

    /// <summary>
    /// 处理窗口收到的托盘消息（由 WndProc 转发）。
    /// </summary>
    /// <returns>true 表示已处理。</returns>
    public bool HandleMessage(int msg, IntPtr lParam)
    {
        if (msg != WM_TRAY_CALLBACK) return false;

        int mouse = lParam.ToInt32() & 0xFFFF;
        if (mouse == WM_LBUTTONUP)
        {
            LeftClick?.Invoke();
            return true;
        }
        if (mouse == WM_RBUTTONUP)
        {
            ShowMenu();
            return true;
        }
        return true;
    }

    private void ShowMenu()
    {
        if (MenuItems.Count == 0) return;

        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            foreach (var (id, text) in MenuItems)
            {
                if (text == null)
                    AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, "");
                else
                    AppendMenu(menu, MF_STRING, (IntPtr)id, text);
            }

            GetCursorPos(out var pt);

            // 关键：菜单要能自动消失，必须先 SetForegroundWindow，
            // 否则点击菜单外部时菜单不关闭（Win32 的经典坑）
            SetForegroundWindow(_ownerHwnd);

            int cmd = TrackPopupMenu(menu,
                TPM_LEFTALIGN | TPM_BOTTOMALIGN | TPM_RIGHTBUTTON,
                pt.X, pt.Y, 0, _ownerHwnd, IntPtr.Zero);

            if (cmd > 0) MenuClicked?.Invoke(cmd);

            // 再发一次空消息让菜单正确释放
            PostMessage(_ownerHwnd, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            Shell_NotifyIcon(NIM_DELETE, ref _data);
            _added = false;
        }
        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }
}
