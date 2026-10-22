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
    public const string Probing = "探测中";
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
