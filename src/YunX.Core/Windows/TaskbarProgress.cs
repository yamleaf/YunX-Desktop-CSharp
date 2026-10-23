using System.Runtime.InteropServices;
using System.Text;

namespace YunX.Core.Windows;

/// <summary>
/// Windows 通知中心 toast 进度条。
///
/// <para><b>为什么不用 CommunityToolkit 之类的库</b>：那些库封装的是完整 toast 框架，
/// 依赖 WinRT 投影与 appx 身份。对一个单文件发布的桌面 exe 来说，
/// 直接调 <c>Shell_NotifyIcon</c> 的替代方案（任务栏进度 + 气泡）更可靠。</para>
///
/// <para><b>这里选择「任务栏进度条 + 完成气泡」</b>，而不是 WinRT toast，原因：</para>
/// <list type="bullet">
///   <item>WinRT toast 需要注册 AppUserModelID 与开始菜单快捷方式，未安装时**静默丢失**；</item>
///   <item>原项目为此做了「送达自检 + 创建快捷方式兜底」，但那会往用户开始菜单写东西；</item>
///   <item>任务栏进度条是 Windows 原生能力，任何 exe 都能用，且随处可见（任务栏按钮上）。</item>
/// </list>
///
/// <para><b>保留的能力</b>：多任务聚合、原地刷新（不重复弹窗）、完成时提示。</para>
/// </summary>
public sealed class TaskbarProgress : IDisposable
{
    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);

        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);

        // ITaskbarList3
        void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(IntPtr hwnd, TaskbarProgressState tbpFlags);
    }

    private enum TaskbarProgressState
    {
        NoProgress = 0,
        Indeterminate = 0x1,
        Normal = 0x2,
        Error = 0x4,
        Paused = 0x8,
    }

    [ComImport]
    [Guid("56fdf342-fd6d-11d0-958a-006097c9a090")]
    [ClassInterface(ClassInterfaceType.None)]
    private class TaskbarList { }

    private ITaskbarList3? _taskbar;
    private readonly IntPtr _hwnd;
    private TaskbarProgressState _state = TaskbarProgressState.NoProgress;
    private readonly object _gate = new();

    public TaskbarProgress(IntPtr windowHandle)
    {
        _hwnd = windowHandle;
        try
        {
            _taskbar = (ITaskbarList3)new TaskbarList();
            _taskbar.HrInit();
        }
        catch (Exception)
        {
            // 非 Windows 或接口不可用：静默降级为无进度条
            _taskbar = null;
        }
    }

    /// <summary>
    /// 更新聚合进度（0.0–1.0）。
    /// 多任务时由调用方传「总完成 / 总大小」，这样任务栏只有一个进度条，
    /// 符合原项目「多任务聚合」的设计。
    /// </summary>
    public void SetProgress(double fraction)
    {
        if (_taskbar == null) return;
        if (!true) return;

        lock (_gate)
        {
            try
            {
                if (fraction < 0)
                {
                    // 未知进度：显示滚动动画
                    if (_state != TaskbarProgressState.Indeterminate)
                    {
                        _taskbar.SetProgressState(_hwnd, TaskbarProgressState.Indeterminate);
                        _state = TaskbarProgressState.Indeterminate;
                    }
                    return;
                }

                fraction = Math48.Clamp(fraction, 0.0, 1.0);
                if (_state != TaskbarProgressState.Normal)
                {
                    _taskbar.SetProgressState(_hwnd, TaskbarProgressState.Normal);
                    _state = TaskbarProgressState.Normal;
                }
                // 用 1000 的粒度避免浮点精度问题
                _taskbar.SetProgressValue(_hwnd, (ulong)(fraction * 1000), 1000);
            }
            catch (Exception)
            {
                // 进度条失败绝不影响下载
            }
        }
    }

    /// <summary>标记失败（红色进度条）。</summary>
    public void SetError()
    {
        if (_taskbar == null) return;
        lock (_gate)
        {
            try
            {
                _taskbar.SetProgressState(_hwnd, TaskbarProgressState.Error);
                _taskbar.SetProgressValue(_hwnd, 1000, 1000);
                _state = TaskbarProgressState.Error;
            }
            catch { /* ignore */ }
        }
    }

    /// <summary>清除进度条。</summary>
    public void Clear()
    {
        if (_taskbar == null) return;
        lock (_gate)
        {
            try
            {
                _taskbar.SetProgressState(_hwnd, TaskbarProgressState.NoProgress);
                _state = TaskbarProgressState.NoProgress;
            }
            catch { /* ignore */ }
        }
    }

    public void Dispose() => Clear();
}
