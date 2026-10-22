using System.Diagnostics;
using System.IO;
using YunX.Core.Download;
using YunX.Core.Mirrors;
using YunX.Core.Net;
using YunX.Core.Util;
using YunX.Desktop.Mvvm;

namespace YunX.Desktop.ViewModels;

/// <summary>任务状态。</summary>
public enum TaskState
{
    Queued,
    Running,
    /// <summary>已暂停：保留断点，可恢复（与 Cancelled 的区别在这里）。</summary>
    Paused,
    Done,
    Failed,
    Cancelled,
}

/// <summary>
/// 一个下载任务的行视图模型。
/// </summary>
public sealed class DownloadTaskViewModel : ObservableObject
{
    private long _size;
    private long _done;
    private double _speed;
    private string _phase = "等待中";
    private string _error = "";
    private TaskState _state = TaskState.Queued;

    public required string Url { get; init; }
    private string _fileName = "";

    /// <summary>
    /// 任务显示名。
    ///
    /// <para><b>必须可写</b>：磁力链接在拿到种子元数据之前只有 <c>dn</c> 或 info hash 前缀，
    /// 引擎连上 peers 之后才知道真实文件名，那时要用真名覆盖占位名。
    /// 用 <c>init</c> 会让磁力任务永远显示「磁力_C12FE1C0」这种占位符。</para>
    /// </summary>
    public required string FileName
    {
        get => _fileName;
        set => SetProperty(ref _fileName, value);
    }

    /// <summary>进程内临时编号（调试事件流索引，与数据库无关）。</summary>
    public long TaskId { get; init; }

    private long _dbId;

    /// <summary>
    /// 数据库主键（0 = 尚未写入过）。
    ///
    /// <para><b>为什么不复用 <see cref="TaskId"/></b>：<c>TaskId</c> 是进程内编号
    /// （<c>_nextTaskId++</c> 从 1 开始），数据库主键是另一套自增序列。
    /// 早期两者混用，`PersistTask` 传 `Id = TaskId > 0` → 永远走 UPDATE 分支 →
    /// 而表里没有这行 → 持久化从未真正写入过。</para>
    ///
    /// <para>语义：首次持久化保持 0，由 <c>TaskRepository.Upsert</c> 走 INSERT
    /// 并用 <c>last_insert_rowid()</c> 回写；之后为真实主键，走 UPDATE。</para>
    /// </summary>
    public long DbId { get => _dbId; set => SetProperty(ref _dbId, value); }

    public DateTime StartedAt { get; } = DateTime.Now;

    /// <summary>
    /// 本任务实际的下载目录（入队时的设置快照）。
    ///
    /// <para>不能总是用「当前设置」：用户在任务跑到一半时改了下载目录，
    /// 续传/恢复必须回到**原来那个目录**去找断点，否则会显示 100% 却拿不出文件，
    /// 或者把新文件写到别处。</para>
    /// </summary>
    public string SaveDir { get; set; } = "";

    /// <summary>来源平台标识（github / quark / baidu…），用于按平台套用线程数等策略。</summary>
    public string? Platform { get; set; }

    /// <summary>
    /// 已被用户取消（**显式状态**，不依赖 CTS）。
    ///
    /// <para><b>为什么不能只看 CTS</b>：排队中的任务还没有 CTS（要等拿到并发槽位才创建），
    /// 光靠 <c>Cts?.Cancel()</c> 对排队任务完全无效 —— 表现为「点了取消没反应」，
    /// 轮到它时照常开始下载。取消意图必须能独立于 CTS 表达。</para>
    /// </summary>
    public bool Cancelled { get; set; }

    /// <summary>
    /// 已从任务列表移除（用户点了删除）。
    ///
    /// <para>删除排队中的任务时，<see cref="MainViewModel.RunTaskAsync"/> 可能已经在
    /// <c>AcquireSlotAsync</c> 里等着了。等它拿到槽位时必须能发现「这个任务已经不在列表里」，
    /// 否则会为一个界面上已不存在的任务下载文件。</para>
    /// </summary>
    public bool RemoveRequested { get; set; }

    /// <summary>
    /// 该任务来自哪个网盘分享。非空时任务结束后要按分享做清理。
    ///
    /// <para>百度/迅雷必须先「转存到自己网盘」才给直链，用完不删就会在用户网盘里
    /// 永久堆积垃圾文件（下载 20 个文件 = 自己盘里多 20 个）。清理必须按分享聚合：
    /// 同一个分享的多个文件共用一个临时目录，单个文件下完就删会打断其它在跑的任务。</para>
    /// </summary>
    public YunX.Core.Cloud.CloudShareContext? CloudContext { get; init; }

    /// <summary>
    /// 该任务当前是否「持有」一次分享引用。
    ///
    /// <para>必须严格一一对应：任务暂停后再「继续」会再跑一次 RunTaskAsync，
    /// 如果只在 finally 里减计数、不重新加，计数会被多减一次 → 提前触发清理 →
    /// 同一分享里还在下的其它文件直链立刻失效。</para>
    /// </summary>
    private int _shareRefHeld;

    public bool TryHoldShareRef() => Interlocked.CompareExchange(ref _shareRefHeld, 1, 0) == 0;

    public bool TryDropShareRef() => Interlocked.CompareExchange(ref _shareRefHeld, 0, 1) == 1;

    public CancellationTokenSource? Cts { get; set; }
    public DownloadEngine? Engine { get; set; }

    public long Size { get => _size; set { if (SetProperty(ref _size, value)) NotifyDerived(); } }
    public long Done { get => _done; set { if (SetProperty(ref _done, value)) NotifyDerived(); } }
    public double Speed { get => _speed; set { if (SetProperty(ref _speed, value)) NotifyDerived(); } }
    /// <summary>
    /// 当前阶段文案（探测中 / 下载中 / 合并中 / 已完成…）。
    ///
    /// <para><b>必须连带通知 <see cref="StateText"/></b>：界面绑的是 <c>StateText</c>
    /// （只读计算属性，Running 时返回 Phase），不是 <c>Phase</c> 本身。
    /// 早期 setter 只通知了自己，于是「探测中」写死在界面上 —— 引擎明明一路在推进
    /// （合并中、已完成都报过），WPF 却不知道要重新读取 StateText，视觉上永远停在
    /// 第一个值。这类「计算属性忘了连带通知」的 bug 编译期查不出、日志里也看不出来，
    /// 只有盯着界面才会发现。</para>
    /// </summary>
    public string Phase
    {
        get => _phase;
        set
        {
            if (!SetProperty(ref _phase, value)) return;
            OnPropertyChanged(nameof(StateText));
        }
    }
    public string Error { get => _error; set => SetProperty(ref _error, value); }

    public TaskState State
    {
        get => _state;
        set
        {
            if (!SetProperty(ref _state, value)) return;
            OnPropertyChanged(nameof(IsRunning));
            OnPropertyChanged(nameof(IsFinished));
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanPause));
            OnPropertyChanged(nameof(CanResume));
        }
    }

    public bool IsRunning => State is TaskState.Running or TaskState.Queued;
    public bool IsFinished => State is TaskState.Done or TaskState.Failed or TaskState.Cancelled;
    public bool CanCancel => State is TaskState.Running or TaskState.Queued;

    /// <summary>可暂停（仅运行中）。</summary>
    public bool CanPause => State == TaskState.Running;
    /// <summary>可继续（暂停或失败）。</summary>
    public bool CanResume => State is TaskState.Paused or TaskState.Failed;
    /// <summary>已落盘（可打开/定位）。</summary>
    public bool HasResult => ResultPath is { Length: > 0 };

    /// <summary>用户主动暂停（不是取消）。</summary>
    public bool PauseRequested { get; set; }

    /// <summary>
    /// 下载该文件必须携带的请求头（网盘直链常需要 Referer / Cookie / UA）。
    /// 空表示走默认（GitHub 直链不需要）。
    /// </summary>
    public Dictionary<string, string>? RequestHeaders { get; set; }

    public string StateText => State switch
    {
        TaskState.Queued => "等待中",
        TaskState.Running => Phase,
        TaskState.Paused => "已暂停",
        TaskState.Done => "已完成",
        TaskState.Failed => "失败",
        TaskState.Cancelled => "已取消",
        _ => "",
    };

    public double Percent => _size > 0 ? Math.Min(100.0, _done * 100.0 / _size) : 0;

    public string SizeText => _size > 0 ? Humanize.Size(_size) : "—";
    public string ProgressText => _size > 0
        ? $"{Humanize.Size(_done)} / {Humanize.Size(_size)}"
        : Humanize.Size(_done);
    public string SpeedText => _state == TaskState.Running && _speed > 1
        ? Humanize.Speed(_speed) : "—";
    public string EtaText => _state == TaskState.Running && _speed > 1 && _size > 0
        ? Humanize.Eta((_size - _done) / _speed) : "—";

    private string? _resultPath;

    /// <summary>
    /// 完成后的结果路径（用于「打开文件 / 打开目录」）。
    ///
    /// <para>界面绑的是派生属性 <see cref="HasResult"/>，所以赋值时必须连带通知它 ——
    /// 否则下载完成后「打开」按钮不会出现，要等某个无关属性变化触发重绘才偶然显现。</para>
    /// </summary>
    public string? ResultPath
    {
        get => _resultPath;
        set
        {
            if (!SetProperty(ref _resultPath, value)) return;
            OnPropertyChanged(nameof(HasResult));
        }
    }

    /// <summary>
    /// 本次任务使用的断点文件路径（<c>xxx.part</c>，可能在缓存目录）。
    ///
    /// <para><b>为什么要在 VM 里留一份</b>：取消 / 失败后断点会保留（这是续传的基础），
    /// 但用户如果选择「删除任务」，这个 part 就成了没人认领的孤儿 —— 一个和文件同等
    /// 大小的垃圾留在磁盘上，界面上再也找不到入口去清它。删除任务时靠这个路径回收。</para>
    ///
    /// <para>任务成功后文件已被搬到 <see cref="ResultPath"/>，这里指向的 part 已不存在，
    /// 删除时 <c>File.Exists</c> 自然为 false，不需要额外判断状态。</para>
    /// </summary>
    public string? PartPath { get; set; }

    private void NotifyDerived()
    {
        OnPropertyChanged(nameof(Percent));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(SpeedText));
        OnPropertyChanged(nameof(EtaText));
    }
}
