using System.Collections.Generic;
using System.Diagnostics;

namespace YunX.Core.Download;

/// <summary>
/// 一个在飞分片的运行时状态。
/// </summary>
public sealed class ChunkState : IDisposable
{
    public long Start { get; }
    public long End { get; }
    public int WorkerId { get; }

    /// <summary>
    /// 整个资源的字节数（0 = 未知）。分片请求用它核对 <c>Content-Range</c> 的总长，
    /// 发现「线路上的资源和探测时不是同一版本」。
    /// </summary>
    public long TotalSize { get; init; }

    private long _bytes;
    private long _lastBytes;
    private long _lastSampleTicks;
    private long _lastPreemptTicks;

    /// <summary>
    /// 本分片被抢占时所在的线路名。
    ///
    /// <para><b>为什么要记它</b>：抢占原本只是"断开重连"，但重连仍然用排在最前的
    /// 那条线路 —— 慢线路上断十次还是慢线路。要让抢占真正有意义，
    /// 必须让它<b>换一条线</b>，而"要避开哪条"只能由抢占那一刻的现场决定。</para>
    /// </summary>
    public string? PreemptedFrom { get; private set; }

    /// <summary>
    /// 已确认有问题的线路（本分片不再使用）。
    ///
    /// <para>实测背景：<c>cdn.gh-proxy.org</c> 的 8 条连接里有 5 条只有 20~44 KB/s，
    /// 而 <c>gh-proxy.com</c> 同期是 190~250 KB/s。这类连接<b>不会失败</b>，
    /// 只会一直慢，所以必须显式记住"这条线在这个分片上不行"。</para>
    /// </summary>
    private readonly List<string> _avoid = new();

    /// <summary>当前线路不可用：下次重连换别的线。</summary>
    public void MarkLineBad(string lineName)
    {
        if (string.IsNullOrEmpty(lineName)) return;
        lock (_avoid)
        {
            if (!_avoid.Contains(lineName)) _avoid.Add(lineName);
        }
        PreemptedFrom = lineName;
    }

    /// <summary>本分片应避开的线路（尝试次数用尽时会被清空，避免全线被拉黑后卡死）。</summary>
    public string[] AvoidLines()
    {
        lock (_avoid) return _avoid.ToArray();
    }

    /// <summary>清空避开列表（所有线路都试过了，只能重头再来）。</summary>
    public void ClearAvoid()
    {
        lock (_avoid) _avoid.Clear();
    }

    /// <summary>
    /// 本分片当前这条连接用的是哪条线路（由 worker 在每次请求前写入）。
    ///
    /// <para>抢占判定时拿不到 URL（它只看到速度），所以要靠这个字段
    /// 才能知道"该拉黑谁"。</para>
    /// </summary>
    public string CurrentLine { get; set; } = "";

    public ChunkState(long start, long end, int workerId)
    {
        Start = start;
        End = end;
        WorkerId = workerId;
        _lastSampleTicks = Stopwatch.GetTimestamp();
        _lastPreemptTicks = 0;
    }

    public long Size => End - Start + 1;
    public long Bytes => Interlocked.Read(ref _bytes);
    public long Remaining => Size - Bytes;

    /// <summary>最近一次采样算出的瞬时速度（字节/秒）。</summary>
    public double LastBps { get; private set; }

    /// <summary>已存活时长（毫秒）。</summary>
    public double AgeMs =>
        (Stopwatch.GetTimestamp() - _startTicks) * 1000.0 / Stopwatch.Frequency;

    private readonly long _startTicks = Stopwatch.GetTimestamp();

    public void AddBytes(int n) => Interlocked.Add(ref _bytes, n);

    // ===== 分片级校验：首尾哨兵摘要（写入时算，校验时回读比对）=====

    /// <summary>
    /// 哨兵长度：分片首、尾各取这么多字节算摘要。
    ///
    /// <para><b>为什么不是整片摘要</b>：整片校验要把每个分片从磁盘读回来一遍，
    /// 等于把下载的 IO 翻倍 —— 50GB 的任务在机械盘上要多读十几分钟，而
    /// <see cref="DownloadOptions.VerifyChunks"/> 默认是开的。用首尾各 16KB 抽查，
    /// 开销约 6%（512KB 块）到 0.06%（24MB 块），却能抓住这类校验真正要防的问题：</para>
    /// <list type="bullet">
    ///   <item><b>pwrite 偏移算错</b>——数据整体落到别的位置，首尾必然对不上；</item>
    ///   <item><b>两个分片区间意外重叠</b>——重叠发生在边界，正好被首尾采样覆盖；</item>
    ///   <item><b>数据根本没落盘</b>（部分写入 / 磁盘满）——读出来是 0 或旧内容。</item>
    /// </list>
    /// <para>中段被单个比特翻转这类静默损坏只能概率性发现，那由整文件
    /// <see cref="DownloadOptions.VerifyHash"/>（下载完算一次全局 SHA-256）兜底。</para>
    /// </summary>
    public const int ProbeBytes = 16 * 1024;

    private System.Security.Cryptography.IncrementalHash? _headHash;
    private System.Security.Cryptography.IncrementalHash? _tailHash;
    private int _headLen;
    private int _tailLen;
    private string? _headHex;
    private string? _tailHex;

    /// <summary>开始摘要。仅在 <c>VerifyChunks</c> 开启时调用。</summary>
    public void BeginHash()
    {
        if (_headHash != null) return;
        _headHash = System.Security.Cryptography.IncrementalHash
            .CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        _tailHash = System.Security.Cryptography.IncrementalHash
            .CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);

        // 分片很小（< 2 个哨兵）时首尾会重叠：退化成整片摘要，
        // 此时 probe 取一半，保证两段不覆盖同一批字节（否则尾段会拿到重复数据）
        int probe = (int)Math.Min(ProbeBytes, Math.Max(1, Size / 2));
        _headLen = probe;
        _tailLen = probe;
    }

    /// <summary>
    /// 把一段**从文件偏移 <paramref name="offset"/> 开始**的数据喂给摘要器。
    ///
    /// <para>必须带偏移：只有知道这段数据落在分片的哪个位置，才能判断它是否属于
    /// 首/尾哨兵区间。按内容长度累加是不行的 —— 抢占后续传会从中间开始写。</para>
    /// </summary>
    public void HashBytes(long offset, ReadOnlySpan<byte> data)
    {
        if (_headHash == null) return;

        long rel = offset - Start;                  // 相对分片起点的偏移
        long end = rel + data.Length;

        // 头部区间 [0, _headLen)
        if (rel < _headLen)
        {
            int take = (int)Math.Min(data.Length, _headLen - rel);
            if (take > 0) _headHash.AppendData(data[..take]);
        }

        // 尾部区间 [Size - _tailLen, Size)
        long tailFrom = Size - _tailLen;
        if (end > tailFrom)
        {
            int skip = (int)Math.Max(0, tailFrom - rel);
            // 裁到 _tailLen：一段数据可能整段落在尾部区间内且比哨兵长，
            // 不裁的话写入侧摘要会覆盖多余字节，校验侧只读 _tailLen 必然对不上。
            int take = (int)Math.Min(data.Length - skip, _tailLen);
            if (take > 0) _tailHash!.AppendData(data.Slice(skip, take));
        }
    }

    /// <summary>产出首尾摘要。未启用时返回 null。</summary>
    public (string Head, string Tail, int HeadLen, int TailLen)? FinishHash()
    {
        if (_headHash == null) return null;
        if (_headHex == null)
        {
            _headHex = Convert.ToHexString(_headHash.GetHashAndReset()).ToLowerInvariant();
            _tailHex = Convert.ToHexString(_tailHash!.GetHashAndReset()).ToLowerInvariant();
        }

        // IncrementalHash 持有 SafeHashHandle：取完摘要必须立刻释放，
        // 否则每个分片泄漏两个加密句柄（32 线程 + 开校验时每秒几十个）。
        _headHash.Dispose();
        _headHash = null;
        _tailHash?.Dispose();
        _tailHash = null;

        return (_headHex, _tailHex!, _headLen, _tailLen);
    }

    /// <summary>
    /// 释放摘要器。分片以任何方式结束（完成 / 被抢占 / 失败）都必须调用，
    /// 不能只依赖 <see cref="FinishHash"/> —— 被抢占的分片永远走不到那一步。
    /// </summary>
    public void Dispose()
    {
        _headHash?.Dispose();
        _headHash = null;
        _tailHash?.Dispose();
        _tailHash = null;
    }

    /// <summary>
    /// 采样瞬时速度。返回 true 表示本次采样把统计窗口推进了。
    /// </summary>
    public bool Sample()
    {
        long now = Stopwatch.GetTimestamp();
        long cur = Bytes;
        double elapsed = (now - _lastSampleTicks) / (double)Stopwatch.Frequency;
        if (elapsed < 0.5) return false;

        // 把上一次的瞬时值挪到 PrevBps，再刷新 LastBps
        _prevBps = LastBps;
        LastBps = (cur - _lastBytes) / elapsed;
        _lastBytes = cur;
        _lastSampleTicks = now;
        SampleCount++;
        return true;
    }

    /// <summary>已完成的采样次数。</summary>
    public int SampleCount { get; private set; }

    /// <summary>上一次采样的瞬时速度（字节/秒）。</summary>
    private double _prevBps;

    /// <summary>
    /// 是否「连续两次采样都偏慢」。
    ///
    /// <para><b>为什么需要两次</b>：<see cref="LastBps"/> 是最近一个采样窗口的瞬时值。
    /// 单次偏低是常态 —— TCP 慢启动未完成、刚建连、镜像负载均衡把请求落到稍慢的节点，
    /// 都会让某一窗口偏低 10~20%。只看一次就抢，等于在惩罚正常抖动；
    /// 抢完还要重新握手，反而更慢。要求连续两次（约 10 秒）都低于阈值才算真的掉队。</para>
    ///
    /// <para>采样不足两次时一律返回 false（还没建立起可比基线）。</para>
    /// </summary>
    public bool IsConsistentlySlow(double floor) =>
        SampleCount >= 2 && LastBps < floor && _prevBps < floor;

    /// <summary>本次抢占标志。下载循环读到 <c>true</c> 就断开当前连接（已写字节保留）。</summary>
    public bool PreemptRequested => Volatile.Read(ref _preempt) != 0;

    private int _preempt;

    public void RequestPreempt()
    {
        // ★ 抢占 = 这条线在本分片上不行，下次必须换一条。
        //   不记录的话，"重连"只是回到同一条慢线上断一次再重连，
        //   白付一次 TCP+TLS 握手，速度一点没变。
        MarkLineBad(CurrentLine);
        Interlocked.Exchange(ref _preempt, 1);
        PreemptCount++;
        _lastPreemptTicks = Stopwatch.GetTimestamp();
    }

    public void ClearPreempt() => Interlocked.Exchange(ref _preempt, 0);

    public int PreemptCount { get; private set; }

    /// <summary>距上次被抢占的毫秒数（未抢过返回极大值）。</summary>
    public double SinceLastPreemptMs => _lastPreemptTicks == 0
        ? double.MaxValue
        : (Stopwatch.GetTimestamp() - _lastPreemptTicks) * 1000.0 / Stopwatch.Frequency;
}

/// <summary>
/// 慢连接抢占的判定器（看门狗）。
///
/// <para><b>这是本项目从 YunX-Desktop 借鉴、并用实测数据确认必要的机制。</b></para>
///
/// <para>网盘 / CDN 普遍是<b>按连接</b>限速的，而且个别连接会落在慢节点上。
/// 实测本项目（Python 版）一次 17MB / 8 线程下载：</para>
/// <code>
/// #12 完成于 32.5s  单块速度 917 KB/s
/// #13 完成于 42.0s  单块速度  66 KB/s   ← 开始塌
/// #17 完成于 56.9s  单块速度  35 KB/s
/// #18 完成于 73.6s  单块速度  34 KB/s
///
/// 12 个块在 32.5s 就下完了，然后干等 41 秒；最后 2 块独占全程 31%
/// </code>
///
/// <para>根因：worker 绑定一条连接后，只要它慢就一路慢到底，没有任何换连接的机制。</para>
///
/// <para><b>语义约定（关键）</b>：抢占 ≠ 失败。它是"换一条连接继续传"，
/// 已写字节全部保留，因此<b>不退避、不判失败、不计入重试次数</b>。
/// 把它当失败处理会触发退避，反而更慢。</para>
/// </summary>
public sealed class SlowChunkWatchdog
{
    /// <summary>采样间隔。</summary>
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);

    /// <summary>绝对速度下限：低于它一定算慢（字节/秒）。</summary>
    public const double FloorBps = 12 * 1024;

    /// <summary>同一分片两次抢占的冷却期：给新连接留爬坡时间。</summary>
    public const double CooldownMs = 10_000;

    /// <summary>剩余不足这么多就不再折腾（换连接本身也有握手成本）。</summary>
    public const long MinRemainBytes = 128 * 1024;

    /// <summary>单个分片最多被抢占几次：全局都慢时避免重连风暴。</summary>
    public const int MaxPreemptPerChunk = 3;

    /// <summary>每轮最多抢占几路，避免同一时刻大批连接同时重建。</summary>
    public const int MaxPreemptPerTick = 2;

    /// <summary>收尾判定：在飞分片不超过这个数就认为其余已无事可做，放宽门槛。</summary>
    public const int EndgameInflight = 3;

    /// <summary>收尾时的最小存活时间（比常规门槛短，但要避开刚建连的爬坡期）。</summary>
    public const double EndgameMinAgeMs = 3_000;

    /// <summary>常规最小存活时间：太新的连接还没跑出稳定速度。</summary>
    public const double MinAgeMs = 15_000;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// 扫描一遍在飞分片，对判定为慢的置抢占标志。
    /// </summary>
    /// <param name="inflight">当前在飞分片集合。</param>
    /// <param name="totalDownloaded">
    /// 本任务累计下载字节。<b>已不参与判定</b>（历史上用来算平均单连接速度，
    /// 但那种算法会把「整体带宽不足」误判成「某条连接慢」，见 <see cref="Sweep"/> 内注释）。
    /// 保留参数是为了不改动调用方签名。
    /// </param>
    /// <param name="elapsedMs">本任务已运行毫秒。<b>已不参与判定</b>，同上。</param>
    /// <param name="workers">并发数（仅用于收尾阶段的判定宽松度）。</param>
    /// <param name="log">日志回调。</param>
    /// <returns>本次抢占的数量。</returns>
    public int Sweep(IReadOnlyCollection<ChunkState> inflight, long totalDownloaded,
        double elapsedMs, int workers, Action<string>? log = null,
        DownloadTuning? tuning = null)
    {
        if (!Enabled || inflight.Count == 0) return 0;

        // 阈值 / 判定时长来自设置（原项目「实验性功能」可调，默认 12KB/s · 15 秒）
        double userFloor = tuning?.PreemptFloorBps ?? FloorBps;
        double userMinAgeMs = tuning?.PreemptMinAgeMs ?? MinAgeMs;

        foreach (var s in inflight) s.Sample();

        // ★ 自适应阈值：用**在飞分片实际瞬时速度的中位数**作为参照，而不是
        //   「任务平均速度 / workers」。
        //
        //   旧公式 avgPerConn = totalDownloaded / elapsed / workers 有两个问题：
        //   1. 它把「已经完成的分片」的字节也摊到当前的 workers 上，任务越往后
        //      历史均值越失真；
        //   2. 它算的是**每个 worker 的平均**，而慢分片的存在本身会拉低这个平均，
        //      再取一半当下限，阈值就落到「正常分片的速度区间」里 ——
        //      实测出现过 95 KB/s 被判成慢连接（阈值 110 KB/s）而反复抢占，
        //      每次重连都要重新 TCP+TLS 握手，反而更慢。
        //
        //   中位数天然抗离群：8 路里 2 路慢，中位数仍然代表「正常连接有多快」。
        //   取中位数的 40% 作为「显著偏慢」的分界 —— 比它慢说明这条连接掉了队，
        //   不是整体带宽不够。
        double median = MedianBps(inflight);
        double adaptive = median > 0 ? median * 0.4 : 0;

        // 两条约束：
        //   - 不低于用户设的绝对下限（避免极慢网络下把正常连接当慢的）；
        //   - 不高于中位数的 40%（避免把「大家都在正常跑」误判成慢）。
        double floor = Math.Max(userFloor, adaptive);

        bool endgame = inflight.Count <= EndgameInflight;
        int taken = 0;

        foreach (var s in inflight.OrderBy(x => x.LastBps))
        {
            if (taken >= MaxPreemptPerTick) break;
            if (s.PreemptRequested) continue;                       // 已置位，等它自己退出
            if (s.PreemptCount >= MaxPreemptPerChunk) continue;      // 抢够次数了
            if (s.SinceLastPreemptMs < CooldownMs) continue;         // 冷却中

            if (endgame)
            {
                if (s.AgeMs < EndgameMinAgeMs) continue;
            }
            else
            {
                if (s.AgeMs < userMinAgeMs) continue;
                if (s.Remaining < MinRemainBytes) continue;
            }

            // 连续两次采样都低于阈值才算真的慢。
            // 只看一次会把「某一窗口略低」当成掉队（实测 95 vs 110 KB/s 就触发抢占），
            // 而抢一次要重新 TCP+TLS 握手，代价比那点速度损失大得多。
            if (!s.IsConsistentlySlow(floor)) continue;   // 不够慢（或样本还不够）

            s.RequestPreempt();
            taken++;
            log?.Invoke($"抢占慢连接：分片 {s.Start}-{s.End}（worker#{s.WorkerId}）" +
                        $"瞬时 {Util.Humanize.Speed(s.LastBps)} < 阈值 {Util.Humanize.Speed(floor)}，" +
                        $"第 {s.PreemptCount}/{MaxPreemptPerChunk} 次，换连接续传");
        }

        return taken;
    }

    /// <summary>
    /// 在飞分片瞬时速度的中位数（字节/秒）。
    ///
    /// <para>只在**已经采到样本**的分片上算（<see cref="ChunkState.LastBps"/> 由
    /// <see cref="ChunkState.Sample"/> 填写，采样窗口不足 0.5 秒时保持 0）。
    /// 全部为 0（刚起跑、还没人采到）时返回 0，调用方退回到绝对下限。</para>
    /// </summary>
    private static double MedianBps(IReadOnlyCollection<ChunkState> inflight)
    {
        var speeds = new List<double>(inflight.Count);
        foreach (var s in inflight)
        {
            double bps = s.LastBps;
            if (bps > 0) speeds.Add(bps);
        }
        if (speeds.Count == 0) return 0;
        if (speeds.Count == 1) return speeds[0];

        speeds.Sort();
        int mid = speeds.Count / 2;
        return speeds.Count % 2 == 1
            ? speeds[mid]
            : (speeds[mid - 1] + speeds[mid]) / 2;
    }
}
