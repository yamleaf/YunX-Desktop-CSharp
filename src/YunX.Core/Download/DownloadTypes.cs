namespace YunX.Core.Download;

/// <summary>下载行为选项。</summary>
public sealed class DownloadOptions
{
    /// <summary>并发分片数（实际会被块数与内容大小钳制）。</summary>
    public int Threads { get; set; } = 8;

    /// <summary>是否启用断点续传。</summary>
    public bool Resume { get; set; } = true;

    /// <summary>全局限速（字节/秒）；0 = 不限速。</summary>
    public long SpeedLimitBytesPerSec { get; set; }

    /// <summary>是否启用慢连接抢占。</summary>
    public bool PreemptSlowChunks { get; set; } = true;

    /// <summary>Range 被忽略的容忍次数，超过则整任务降级单流。</summary>
    public int RangeIgnoredTolerance { get; set; } = 3;

    /// <summary>
    /// 单个区间允许的**连续失败**次数（已把所有线路各试两轮仍失败才算一次）。
    ///
    /// <para>超过就判定任务失败。<b>不能无限重排队</b>：直链过期 / 403 / 416 时
    /// 每个 worker 都会「失败 → 放回队列 → 睡 500ms → 再失败」，任务永远停在
    /// 「下载中」，既不报错也不前进，用户只能手动取消。</para>
    ///
    /// <para>早期这个属性写了却没被引擎读取（引擎用了一个私有常量），
    /// 改设置完全没效果 —— 现在引擎直接用它。3 次 ≈ 6 轮全线路尝试，
    /// 对「直链真的过期」足够快报错，对网络抖动又足够宽容。</para>
    /// </summary>
    public int ChunkRetries { get; set; } = 3;

    /// <summary>下载完成后是否计算文件 SHA-256（大文件会多花几秒）。</summary>
    public bool VerifyHash { get; set; }

    /// <summary>
    /// 分片级 SHA-256 校验（对应原项目「分片级 SHA-256」）。
    /// 写入时边写边算摘要，合并前逐片重算比对 —— 不额外读盘，只多一点 CPU。
    /// 与 <see cref="VerifyHash"/> 的区别：这个能在**合并阶段**就发现某一片坏了，
    /// 而不是等到整个文件算完才发现。
    /// </summary>
    public bool VerifyChunks { get; set; } = true;

    /// <summary>可调参数（读缓冲、抢占阈值等）。</summary>
    public DownloadTuning Tuning { get; set; } = new();

    /// <summary>平台标识（用于按平台取线程数，如 "github"、"quark"）。</summary>
    public string Platform { get; set; } = "github";

    /// <summary>
    /// 临时（分片）文件目录。空 = 与目标文件同目录。
    ///
    /// <para>分开的理由：机械盘/网络盘上的目标目录可能很慢，而下载过程有大量随机写；
    /// 把 part 放到 SSD 缓存盘、完成后一次搬过去，整体体验明显更好。
    /// 跨盘搬家走 File.Move 的「复制 + 删除」路径，因此不会有残留。</para>
    /// </summary>
    public string? CacheDir { get; set; }

    /// <summary>额外请求头（网盘直链常需要 Referer / UA / Cookie）。</summary>
    public Dictionary<string, string> Headers { get; set; } = new();

    /// <summary>
    /// 暂停请求：与取消不同 —— 暂停保留断点且状态可恢复，取消则视为放弃。
    /// 引擎两者都停止传输，区别在于上层如何记录状态。
    /// </summary>
    public Action? OnPaused { get; set; }

    /// <summary>错峰建连：第 i 路首请求前延迟 i*25ms（上限 ~200ms），降低触发镜像限流的概率。</summary>
    public bool StaggerConnect { get; set; } = true;

    /// <summary>
    /// 诊断钩子：单个分片完成时回调 (起, 块大小, 该任务已运行秒数)。
    /// 用于验证「收尾塌缩」是否被尾部收缩 + 慢连接抢占消除。
    /// </summary>
    public Action<long, long, double>? OnChunkCompleted { get; set; }

    /// <summary>
    /// 断点文件路径回调：引擎在确定 part 落点后回调一次（可能在缓存目录）。
    ///
    /// <para>上层需要它才能在「用户删除任务」时回收孤儿断点。若不给，
    /// 取消/失败留下的 <c>.part</c> 就再没有入口能清理了。</para>
    /// </summary>
    public Action<string>? OnPartPathResolved { get; set; }

    /// <summary>
    /// 实际落盘文件名确定时回调（<b>可能和请求的名字不同</b>）。
    ///
    /// <para><b>为什么需要</b>：同目录已有同名文件时，<c>FileNamePolicy.EnsureUnique</c>
    /// 会改成 <c>name (1).ext</c>。界面若继续显示请求时的名字，就会出现
    /// 「两个任务都叫 X」而磁盘上是 <c>X</c> 和 <c>X (1)</c> —— 用户无法分辨哪个是新的。</para>
    /// </summary>
    public Action<string>? OnFileNameResolved { get; set; }

    /// <summary>
    /// 目标目录已有同名文件时怎么办（由上层询问用户后决定）。
    ///
    /// <para>null = 未提供决策，沿用默认策略（自动改名 <c>name (1).ext</c>）。</para>
    ///
    /// <para><b>为什么这个决策必须由上层来做</b>：Core 是纯逻辑层，弹不出窗口。
    /// 引擎能做的只是「发现冲突 → 问一下 → 按答复执行」，窗口由 Desktop 弹。</para>
    /// </summary>
    public Func<NameConflict, NameConflictDecision>? ResolveNameConflict { get; set; }
}

/// <summary>同名文件冲突的描述。</summary>
public sealed record NameConflict(string FileName, string Directory, long ExistingSize);

/// <summary>
/// 用户对同名冲突的处理结果。
///
/// <para><b>为什么不是只返回一个枚举</b>：「重命名文件」需要把用户输入的名字带回引擎，
/// 而 <see cref="DownloadAsync"/> 的 fileName 参数在调用前就已求值，回调改不了它。
/// 所以把名字装在这里返回，由引擎自己采用。</para>
/// </summary>
public readonly record struct NameConflictDecision(NameConflictChoice Choice, string? NewName = null)
{
    public static NameConflictDecision AutoRename => new(NameConflictChoice.AutoRename);
    public static NameConflictDecision Overwrite => new(NameConflictChoice.Overwrite);
    public static NameConflictDecision Cancel => new(NameConflictChoice.Cancel);
    public static NameConflictDecision Rename(string name) => new(NameConflictChoice.AutoRename, name);
}

/// <summary>用户对同名冲突的选择。</summary>
public enum NameConflictChoice
{
    /// <summary>自动改名（默认，最安全：不覆盖任何东西）。</summary>
    AutoRename,
    /// <summary>覆盖已有文件：删掉它，用原名重新下。</summary>
    Overwrite,
    /// <summary>用户取消本次下载。</summary>
    Cancel,
}

/// <summary>下载进度快照。</summary>
public readonly record struct DownloadProgress(
    long BytesDone,
    long TotalBytes,
    double SpeedBytesPerSec,
    string Phase)
{
    public double Percent => TotalBytes > 0
        ? Math.Min(100.0, BytesDone * 100.0 / TotalBytes)
        : 0;

    public double EtaSeconds => SpeedBytesPerSec > 1
        ? Math.Max(0, (TotalBytes - BytesDone) / SpeedBytesPerSec)
        : 0;
}

/// <summary>下载结果。</summary>
public sealed class DownloadResult
{
    public required string FilePath { get; init; }
    public long Size { get; init; }
    public string MirrorName { get; init; } = "";
    public double ElapsedSeconds { get; init; }
    /// <summary>multi = 分块多线程；single = 单流。</summary>
    public string Mode { get; init; } = "single";
    public int ChunkCount { get; init; }
    /// <summary>是否复用了既有断点。</summary>
    public bool Resumed { get; init; }
    /// <summary>被抢占（换连接）的总次数。</summary>
    public int Preemptions { get; init; }
    public string? Sha256 { get; init; }

    public double AverageSpeedBps => ElapsedSeconds > 0 ? Size / ElapsedSeconds : 0;
}

/// <summary>下载阶段（用于 UI 与日志）。</summary>
public static class DownloadPhase
{
    /// <summary>探测总称（兜底用；细分为下面两条，界面上会看到具体是哪一步）。</summary>
    public const string Probing = "探测中";

    /// <summary>正在测速各条线路（含镜像），挑最快的那条。</summary>
    public const string TestingMirrors = "测速线路中";

    /// <summary>已选定线路，正在取文件大小 / ETag / Range 支持情况。</summary>
    public const string Inspecting = "获取文件信息";

    /// <summary>正在规划分片（按总长和线程数切块）。</summary>
    public const string Planning = "规划分片中";

    public const string Downloading = "下载中";
    public const string Merging = "合并中";
    public const string Verifying = "校验中";
    public const string Completed = "已完成";
    public const string Cancelled = "已取消";
    public const string Failed = "失败";
}

/// <summary>取消请求。与「失败」区分开：取消保留断点，且不算错误。</summary>
public sealed class DownloadCancelledException : Exception
{
    public DownloadCancelledException() : base("下载已取消") { }
}

/// <summary>下载失败。</summary>
public sealed class DownloadFailedException : Exception
{
    public DownloadFailedException(string message) : base(message) { }
}
