using System.Collections.Concurrent;

namespace YunX.Core.Diagnostics;

/// <summary>下载调试事件。</summary>
public sealed record DownloadDebugEvent(DateTimeOffset At, string Category, string Detail)
{
    public string Text => $"[{At:HH:mm:ss.fff}] {Category} · {Detail}";
}

/// <summary>
/// 按任务记录下载事件流（对应原项目「下载调试」）。
///
/// <para><b>与原项目一致的两条语义</b>：</para>
/// <list type="bullet">
///   <item>数据<b>只活在内存</b>，不落盘：调试页关闭时不产生任何 IO；</item>
///   <item><b>拉取式</b>：调用方主动 <see cref="Snapshot"/> 取，不是定时推送。
///         没打开调试页时零额外开销。</item>
/// </list>
///
/// <para>记录的事件类别与引擎阶段一一对应：
/// 入队 / 断点失效 / 分片规划 / 抢占慢连接 / 换连接结果 / 合并与校验 / 完成 / 失败 / 暂停。</para>
/// </summary>
public sealed class DownloadDebugLog
{
    private const int MaxEventsPerTask = 500;
    private const int MaxTasks = 50;

    private readonly ConcurrentDictionary<long, ConcurrentQueue<DownloadDebugEvent>> _events = new();
    private readonly ConcurrentQueue<long> _order = new();

    /// <summary>开关：关闭时 <see cref="Record"/> 只做一次 bool 判断。</summary>
    public bool Enabled { get; set; }

    /// <summary>记录一条事件。</summary>
    public void Record(long taskId, string category, string detail)
    {
        if (!Enabled) return;

        // 只有真正新建队列时才入序：GetOrAdd 的工厂可能在并发下被调用多次（返回值虽同），
        // 每次都入序会让 _order 里堆满重复 id，进而提前淘汰仍在下载的任务。
        if (!_events.TryGetValue(taskId, out var queue))
        {
            var fresh = new ConcurrentQueue<DownloadDebugEvent>();
            queue = _events.GetOrAdd(taskId, fresh);
            if (ReferenceEquals(queue, fresh)) _order.Enqueue(taskId);
        }

        queue.Enqueue(new DownloadDebugEvent(DateTimeOffset.Now, category, detail));

        // 单任务上限：超出后丢最旧的（滚动窗口）
        while (queue.Count > MaxEventsPerTask && queue.TryDequeue(out _)) { }

        // 任务数上限
        while (_order.Count > MaxTasks && _order.TryDequeue(out long old))
            _events.TryRemove(old, out _);
    }

    /// <summary>取某个任务的事件快照（拉取式）。</summary>
    public List<DownloadDebugEvent> Snapshot(long taskId) =>
        _events.TryGetValue(taskId, out var q) ? q.ToList() : new List<DownloadDebugEvent>();

    /// <summary>取全部任务的事件（导出用）。</summary>
    public Dictionary<long, List<DownloadDebugEvent>> SnapshotAll()
    {
        var result = new Dictionary<long, List<DownloadDebugEvent>>();
        foreach (var id in _order.Distinct())
            result[id] = Snapshot(id);
        return result;
    }

    /// <summary>
    /// 任务结束（成功或删除）时清理。
    ///
    /// <para>必须顺带把 <c>_order</c> 里的 id 也清掉。只删 <c>_events</c> 会留下「幽灵 id」：
    /// 它的存在让 <c>_order.Count</c> 虚高，达到上限后每次淘汰都在删一个已经不存在的条目，
    /// 真正还在下载的任务反而被提前淘汰 —— 调试页面上会看到「正在跑的任务没有事件」。</para>
    /// </summary>
    public void Forget(long taskId)
    {
        _events.TryRemove(taskId, out _);
        PruneOrder();
    }

    /// <summary>剔除 _order 里已不在 _events 中的 id（数量上限只有几十，代价可忽略）。</summary>
    private void PruneOrder()
    {
        int n = _order.Count;
        for (int i = 0; i < n; i++)
        {
            if (!_order.TryDequeue(out long id)) break;
            if (_events.ContainsKey(id)) _order.Enqueue(id);
        }
    }

    /// <summary>关闭开关时清空全部（原项目语义：关闭会清空，需二次确认）。</summary>
    public void ClearAll()
    {
        _events.Clear();
        while (_order.TryDequeue(out _)) { }
    }

    /// <summary>把某个任务的事件导出为文本（调试页「复制快照」）。</summary>
    public string ExportText(long taskId)
    {
        var list = Snapshot(taskId);
        if (list.Count == 0) return "(无事件)";
        return string.Join(Environment.NewLine, list.Select(e => e.Text));
    }
}

/// <summary>
/// 下载调试页所需的全局资源快照（在飞配额 / 活跃任务 / 线程池 / 堆内存）。
/// </summary>
public sealed record ResourceSnapshot(
    int InflightChunks,
    int InflightLimit,
    int ActiveTasks,
    int ThreadPoolBusy,
    int ThreadPoolTotal,
    long ManagedHeapBytes,
    long WorkingSetBytes)
{
    public string Describe()
    {
        double heapMb = ManagedHeapBytes / 1024.0 / 1024.0;
        double wsMb = WorkingSetBytes / 1024.0 / 1024.0;
        return $"在飞分片 {InflightChunks}/{InflightLimit} · 活跃任务 {ActiveTasks} · " +
               $"线程池 {ThreadPoolBusy}/{ThreadPoolTotal} · " +
               $"堆 {heapMb:0.0}MB · 工作集 {wsMb:0.0}MB";
    }

    public static ResourceSnapshot Capture(int inflight, int limit, int activeTasks)
    {
        ThreadPool.GetAvailableThreads(out int workerAvail, out int ioAvail);
        ThreadPool.GetMaxThreads(out int workerMax, out _);

        var proc = System.Diagnostics.Process.GetCurrentProcess();
        return new ResourceSnapshot(
            inflight, limit, activeTasks,
            workerMax - workerAvail, workerMax,
            GC.GetTotalMemory(false),
            proc.WorkingSet64);
    }
}
