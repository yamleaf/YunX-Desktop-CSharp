using System.Runtime.InteropServices;

namespace YunX.Core.Windows;

/// <summary>
/// 阻止系统睡眠。
///
/// <para>下载期间必须阻止睡眠，否则用户离开后系统休眠会中断传输。
/// 不改用户的电源计划，进程退出后自动失效。</para>
///
/// <para><b>为什么不用 <c>SetThreadExecutionState</c></b>：它是<b>线程级</b> API，
/// <c>ES_CONTINUOUS</c> 的声明只对调用线程有效，<b>线程一结束声明就消失</b>。
/// 而这个类的调用方是 <c>PropertyChanged</c> → 任务栏进度刷新，属性完全可能在
/// 线程池线程上被赋值（下载进度就是从 IO 线程报上来的），于是声明落在池线程上，
/// 该线程返回后立刻失效 —— 表现为「下载中系统照样睡」，而且几乎无法复现。</para>
///
/// <para>改用 <c>PowerCreateRequest</c> / <c>PowerSetRequest</c>：这是<b>进程级</b>电源请求，
/// 句柄可以由任意线程创建和清除，不依赖调用线程存活。这也是微软对长时间任务的推荐做法。</para>
/// </summary>
public sealed class SleepBlocker : IDisposable
{
    // ================= Win32 =================

    [StructLayout(LayoutKind.Sequential)]
    private struct REASON_CONTEXT
    {
        public uint Version;                    // POWER_REQUEST_CONTEXT_VERSION = 0
        public uint Flags;                      // POWER_REQUEST_CONTEXT_SIMPLE_STRING = 1
        public IntPtr SimpleReasonString;       // union 的简单字符串分支
        // union 的 Detailed 分支更大（LocalizedReasonModule + Id + Count + 指针 = 24 字节），
        // 留出等长填充，避免系统按 Detailed 布局读取时越界。
        private long _pad0;
        private long _pad1;
    }

    private const uint POWER_REQUEST_CONTEXT_SIMPLE_STRING = 0x00000001;

    private enum PowerRequestType
    {
        /// <summary>系统保持唤醒（可关屏）。</summary>
        SystemRequired = 1,
    }

    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        Continuous = 0x80000000,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr PowerCreateRequest(ref REASON_CONTEXT context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerSetRequest(IntPtr powerRequest, PowerRequestType type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerClearRequest(IntPtr powerRequest, PowerRequestType type);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>回退路径（老系统 PowerCreateRequest 不可用时）。</summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint SetThreadExecutionState(ExecutionState esFlags);

    // ================= 状态 =================

    private readonly object _gate = new();
    private IntPtr _request;          // 非零 = 用电源请求
    private bool _legacy;             // true = 走 SetThreadExecutionState 回退
    private bool _active;

    public bool IsActive
    {
        get { lock (_gate) return _active; }
    }

    /// <summary>开始阻止睡眠。可重复调用（幂等）。</summary>
    public void Acquire()
    {
        lock (_gate)
        {
            if (_active) return;

            if (TryCreatePowerRequest())
            {
                _active = true;
                return;
            }

            // 回退：线程级声明。仍然比什么都不做好，但只在「创建电源请求失败」时用。
            _legacy = SetThreadExecutionState(
                ExecutionState.SystemRequired | ExecutionState.Continuous) != 0;
            _active = _legacy;
        }
    }

    private bool TryCreatePowerRequest()
    {
        IntPtr reason = IntPtr.Zero;
        try
        {
            reason = Marshal.StringToHGlobalUni("YunX Desktop 正在下载文件");
            var ctx = new REASON_CONTEXT
            {
                Version = 0,
                Flags = POWER_REQUEST_CONTEXT_SIMPLE_STRING,
                SimpleReasonString = reason,
            };

            var handle = PowerCreateRequest(ref ctx);
            if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;

            if (!PowerSetRequest(handle, PowerRequestType.SystemRequired))
            {
                CloseHandle(handle);
                return false;
            }

            _request = handle;
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (reason != IntPtr.Zero) Marshal.FreeHGlobal(reason);
        }
    }

    /// <summary>解除阻止。</summary>
    public void Release()
    {
        lock (_gate)
        {
            if (!_active) return;

            if (_request != IntPtr.Zero)
            {
                PowerClearRequest(_request, PowerRequestType.SystemRequired);
                CloseHandle(_request);
                _request = IntPtr.Zero;
            }

            if (_legacy)
            {
                SetThreadExecutionState(ExecutionState.Continuous);
                _legacy = false;
            }

            _active = false;
        }
    }

    public void Dispose() => Release();
}
