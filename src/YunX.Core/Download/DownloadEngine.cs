// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 yamleaf
//
// YunX-Desktop-CSharp —— 基于 YunX-Desktop（AGPL-3.0）的 C# / WPF 重构版。
// 本程序是自由软件：你可以依据 GNU AGPL-3.0 的条款重新分发或修改它。
// 完整协议见仓库根目录 LICENSE。

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Microsoft.Win32.SafeHandles;
using YunX.Core.Mirrors;
using YunX.Core.Net;
using YunX.Core.Util;

namespace YunX.Core.Download;

/// <summary>
/// 高速下载引擎。
///
/// <para><b>能力总览</b>：</para>
/// <list type="number">
///   <item>多线路择优（<see cref="MirrorPool"/>）：并发测速、按稳态吞吐排序、失败冷却；</item>
///   <item>分块多线程：块数反算 + 尾部收缩（<see cref="ChunkPlanner"/>），消除尾块长尾；</item>
///   <item>慢连接抢占（<see cref="SlowChunkWatchdog"/>）：慢连接断开换新的续传，不丢数据；</item>
///   <item>断点续传：区间级记录 + ETag/If-Range 校验，内容变了自动丢弃重下；</item>
///   <item>Range 降级容忍：服务器偶发忽略 Range 不立即失败，容忍后整任务降级单流；</item>
///   <item>无锁并发写：<see cref="RandomAccess"/> 基于 pwrite，多线程写同一文件无需加锁；</item>
///   <item>令牌桶限速、错峰建连、可选 SHA-256 校验。</item>
/// </list>
/// </summary>
public sealed class DownloadEngine
{
    /// <summary>默认读缓冲（96KB）。实际值由 <see cref="DownloadTuning.ReadBufferBytes"/> 决定。</summary>
    private const int DefaultBufferSize = 96 * 1024;
    private const int ProgressThrottleMs = 120;

    /// <summary>断点元数据的最小落盘间隔（= 1 秒），见 <c>PersistMeta</c> 的注释。</summary>
    private static readonly long MetaSaveIntervalTicks = Stopwatch.Frequency;

    /// <summary>
    /// 同一区间允许的连续失败次数（<see cref="FetchOutcome.Failed"/>，即已把所有线路
    /// 各试了两轮）的**默认值**。实际值取 <see cref="DownloadOptions.ChunkRetries"/>，
    /// 这样设置项才真正生效；这里只作为兜底默认。
    /// </summary>
    private const int DefaultMaxChunkFailures = 3;

    private readonly HttpClientProvider _provider;
    private readonly MirrorPool _pool;

    /// <summary>
    /// 引擎级取消代际。
    ///
    /// <para><b>为什么不是直接持有一个 CTS 然后 Cancel</b>：<see cref="CancellationTokenSource"/>
    /// 一旦取消就<b>永远处于已取消状态</b>，无法复用。若字段长期持有同一个实例，
    /// 用户点过一次「全部取消」之后，这个引擎上<b>之后所有</b>下载都会在起跑线上
    /// 立刻被取消（表现：任务瞬间变「已取消」，日志只有一行「开始」）。
    /// 当前每个任务各建一个引擎，所以缺陷被掩盖了；但只要将来把引擎改成共享单例，
    /// 这就是一个必然踩中的坑。这里按代际换新实例，语义变成「取消此刻在飞的」。</para>
    /// </summary>
    private CancellationTokenSource _engineCts = new();

    public DownloadEngine(HttpClientProvider provider, MirrorPool pool)
    {
        _provider = provider;
        _pool = pool;
    }

    /// <summary>中止本引擎正在进行的下载（保留断点）。</summary>
    public void CancelAll()
    {
        CancellationTokenSource old;
        lock (_engineCtsGate)
        {
            old = _engineCts;
            // 换新实例：已拿到旧 token 的在飞下载立即停止，新下载不再受影响
            _engineCts = new CancellationTokenSource();
        }

        try { old.Cancel(); } catch (ObjectDisposedException) { /* 已释放 */ }
        // 旧实例可能仍有下载持有着它的 token（Cancel 后不再有人注册），交给 GC；
        // 这里不 Dispose，避免在飞任务访问已释放的 CTS 抛 ObjectDisposedException。
    }

    private readonly object _engineCtsGate = new();

    private CancellationToken EngineToken
    {
        get { lock (_engineCtsGate) return _engineCts.Token; }
    }

    /// <summary>
    /// 下载一个 URL 到目标目录。
    /// </summary>
    public async Task<DownloadResult> DownloadAsync(
        string url,
        string outDir,
        string? fileName = null,
        DownloadOptions? options = null,
        IProgress<DownloadProgress>? progress = null,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        options ??= new DownloadOptions();
        Directory.CreateDirectory(outDir);

        // 外部 token 与引擎级 token 一起生效：任一取消都中止
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, EngineToken);
        ct = linkedCts.Token;

        var sw = Stopwatch.StartNew();
        log?.Invoke($"开始：{FileNamePolicy.RedactUrl(url)}");

        // ---- 1. 探测：选线路、拿大小、判断 Range 与 ETag ----
        using var client = _provider.CreateClient(forDownload: true);
        var probe = new RemoteProbe(client);
        // headers 必须一起传：网盘直链的 Referer / Cookie 在探测阶段同样是硬校验
        var info = await probe.ProbeAsync(_pool, url, fileName ?? "download.bin", log, ct,
                options.Headers)
            .ConfigureAwait(false);

        string desiredName = FileNamePolicy.SanitizeFileName(fileName ?? info.FileName);

        // ---- 2. 决定目标路径：能续传就复用既有 part ----
        var (target, part, meta) = ResolvePaths(outDir, desiredName, info, url, options);
        bool resumed = meta != null && meta.Done.Count > 0;

        // 把断点落点告诉上层：取消/失败后 part 会保留，用户删任务时需要一个路径来回收它
        try { options.OnPartPathResolved?.Invoke(part); } catch { /* 回调失败不影响下载 */ }

        // 新建的目标名在 FileNamePolicy 里是「预留」过的（防止并发任务抢同一路径）。
        // 无条件释放：任务成功时文件已存在、失败/取消时 .part 还在，这两种情况
        // 后来者都会被 File.Exists 挡住；只有「用户删掉 .part 后重下」才需要预留表让路。
        try
        {

            if (resumed)
            {
                long doneBytes = meta!.Done.Sum(d => d.End - d.Start + 1);
                log?.Invoke($"断点续传：已有 {Humanize.Size(doneBytes)} / {info.SizeText}");
            }

            // ---- 3. 分块 or 单流 ----
            int preemptions = 0;
            string mode;
            int chunkCount = 0;

            if (info.RangeSupported && info.Total > 0)
            {
                var multi = await DownloadMultiThreadAsync(
                    info, url, part, options, meta, resumed, progress, log,
                    // 看门狗线程与 N 个 worker 会并发回调这里，普通 ++ 会丢计数
                    onPreempt: () => Interlocked.Increment(ref preemptions), ct).ConfigureAwait(false);

                // 注意：mode 不能无条件写 "multi" —— 中途 Range 被忽略而降级单流时，
                // 任务实际上走的是单流；写成 multi 会让界面显示「8 线程分片」而撒谎。
                chunkCount = multi.ChunkCount;
                mode = multi.Mode;
            }
            else
            {
                mode = "single";
                await DownloadSingleStreamAsync(
                    info, url, part, options, progress, log, ct).ConfigureAwait(false);
            }

            // ---- 4. 收尾：校验大小 → 落盘 → 原子改名 ----
            long size = new FileInfo(part).Length;

            // ★ 收尾阶段必须显式上报进度，否则界面会停在最后一次节流上报的状态。
            //   原因见 ProgressCounter.Add 的节流说明：节流窗口 + 「只在有字节流入时上报」
            //   这两条叠加，导致最后几十 KB 和整个收尾过程都没有上报点。
            //   这里先补一条「字节已满、阶段仍是下载中」的快照，把进度条补到 100%。
            progress?.Report(new DownloadProgress(
                info.Total > 0 ? Math.Min(size, info.Total) : size,
                info.Total > 0 ? info.Total : size, 0, DownloadPhase.Merging));

            if (info.Total > 0 && size != info.Total)
            {
                // 长度不符绝不落成正式文件，保留 part 供下次续传
                throw new DownloadFailedException(
                    $"文件长度不符（期望 {info.Total}，实际 {size}），已保留断点以便重试");
            }

            string? sha = null;
            if (options.VerifyHash)
            {
                progress?.Report(new DownloadProgress(size, size, 0, DownloadPhase.Verifying));
                sha = await FileNamePolicy.Sha256Async(part, ct).ConfigureAwait(false);
                log?.Invoke($"SHA-256：{sha}");
            }

            // ★ 跨盘搬家必须自己兜住「复制到一半失败」。
            //
            //   File.Move 在同一个卷内是原子 rename，但在**跨卷**时退化成
            //   「复制 + 删除源文件」，而且这个复制**不可取消、不回报进度**：
            //   - 目标盘（下载目录）会留下一个**半截的正式文件名**。它不是 .part，
            //     下次续传不会复用它，用户也认不出这是个坏文件 —— 纯粹的垃圾。
            //   - 用户在这段时间点取消是无效的，界面上看是「合并中卡住」。
            //   - 复制失败（磁盘满 / 权限）时 part 还在缓存盘，这次没白下，但目标盘
            //     那半截必须清掉，否则每次重试都会多留一份。
            //
            //   所以先删目标（EnsureUnique 已保证这个名字是本任务独占的），
            //   失败时清掉半截成品；part 保留，下次可以继续。
            if (File.Exists(target)) File.Delete(target);
            try
            {
                File.Move(part, target);
            }
            catch (Exception moveEx)
            {
                try
                {
                    // 半截的正式文件删掉：它不是有效断点，留着只会误导
                    if (File.Exists(target)) File.Delete(target);
                }
                catch { /* 删不掉也不能盖掉原始异常 */ }

                throw new DownloadFailedException(
                    "保存到下载目录失败（断点仍在，可重试）：" + moveEx.Message);
            }

            PartMetadata.Delete(part);

            sw.Stop();
            var result = new DownloadResult
            {
                FilePath = target,
                Size = size,
                MirrorName = info.MirrorName,
                ElapsedSeconds = sw.Elapsed.TotalSeconds,
                Mode = mode,
                ChunkCount = chunkCount,
                Resumed = resumed,
                Preemptions = preemptions,
                Sha256 = sha,
            };

            progress?.Report(new DownloadProgress(size, size, 0, DownloadPhase.Completed));
            log?.Invoke($"完成：{Path.GetFileName(target)} · {Humanize.Size(size)} · " +
                        $"{result.ElapsedSeconds:0.0}s · 线路 {info.MirrorName}" +
                        (resumed ? "（续传）" : "") +
                        (preemptions > 0 ? $" · 换连接 {preemptions} 次" : ""));
            return result;
        }
        finally
        {
            FileNamePolicy.Release(target);
        }
    }

    /// <summary>
    /// 解析目标路径。若既有 part 的元数据与本资源匹配，则复用它（续传），
    /// 否则另选一个不冲突的名字。
    /// </summary>
    private (string Target, string Part, PartMetadata? Meta) ResolvePaths(
        string outDir, string fileName, RemoteInfo info, string url, DownloadOptions options)
    {
        // part 落在缓存目录（若配置了），最终文件仍在 outDir
        string partDir = string.IsNullOrWhiteSpace(options.CacheDir) ? outDir : options.CacheDir!;
        Directory.CreateDirectory(partDir);

        string basePath = Path.Combine(outDir, fileName);
        string basePart = Path.Combine(partDir, fileName + ".part");

        if (options.Resume)
        {
            var existing = PartMetadata.TryLoad(basePart);
            if (existing != null
                && existing.UrlKey == PartMetadata.UrlKeyOf(url)
                && existing.Total == info.Total
                && File.Exists(basePart))
            {
                long onDisk = new FileInfo(basePart).Length;

                // ★ 复用条件不能要求「文件长度 == 总长」。
                //
                //   多线程路径会 RandomAccess.SetLength(total) 预分配到完整大小，
                //   所以长度恰好等于 total；但**单流路径不预分配**，未完成的 part
                //   长度必然 < total。早期统一按 `Length == Total` 判断，结果是：
                //   · 单流（GitHub 仓库 zip 走 codeload，不支持 Range，必然单流）
                //     永远不复用 → 中断一次就整包重下；
                //   · 而且旧 part 被 EnsureUnique 改名成 "name (1)"，成为永久孤儿。
                //
                //   正确条件：长度 **不超过** 总长（超出说明是脏数据，不能信），
                //   且元数据里记录的已完成区间与长度量级一致。
                if (onDisk <= info.Total)
                    return (basePath, basePart, existing);

                // 文件比总长还大：不可能来自这个资源，丢弃重下
                try { File.Delete(basePart); } catch { /* ignore */ }
                PartMetadata.Delete(basePart);
            }
        }

        // 不复用：选新名字，避免覆盖用户已有文件
        string target = FileNamePolicy.EnsureUnique(outDir, fileName);
        string part = Path.Combine(partDir, Path.GetFileName(target) + ".part");

        // 清掉同名残留
        try { if (File.Exists(part)) File.Delete(part); } catch { /* ignore */ }
        PartMetadata.Delete(part);

        return (target, part, null);
    }

    // =====================================================================
    //  多线程分块
    // =====================================================================

    private async Task<(int ChunkCount, string Mode)> DownloadMultiThreadAsync(
        RemoteInfo info,
        string originalUrl,
        string partPath,
        DownloadOptions options,
        PartMetadata? meta,
        bool resumed,
        IProgress<DownloadProgress>? progress,
        Action<string>? log,
        Action onPreempt,
        CancellationToken ct)
    {
        long total = info.Total;

        // ---- 分片规划 ----
        // 关键：ComputeChunkSize 用 ceil 反算，保证所有块近似等长、不产生 15 字节尾块
        int workers = Math.Clamp(options.Threads, 1, 32);
        long chunkSize = ChunkPlanner.ComputeChunkSize(total, workers);
        int chunkCount = ChunkPlanner.ComputeChunkCount(total, chunkSize);
        workers = Math.Min(workers, Math.Max(1, chunkCount));
        log?.Invoke($"分片规划：{chunkCount} 块 × {Humanize.Size(chunkSize)}，{workers} 线程");

        // ---- 准备 part 文件（预分配，便于 pwrite 定位）----
        //
        // handle 必须由 try/finally 兜住。原来的写法只在「正常结束」和「协作取消」两处
        // 手工 Dispose，而 SetLength、测速、建客户端任一处抛异常（用户点取消时走的正是
        // 测速之后那条路径）都会跳过释放 —— 句柄泄漏，且 .part 被 FileShare.Read 持锁，
        // 表现为「取消后删不掉 part 文件」。
        //
        // FileOptions.Asynchronous 也是必需的：不带它时 RandomAccess.WriteAsync 会退回
        // 线程池同步写，多 worker 并发下线程池排队会拖慢整体吞吐。
        var handle = File.OpenHandle(partPath, FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.Read, FileOptions.Asynchronous);

        var done = new IntervalSet();
        string? etagForIfRange = null;
        bool fallbackToSingle = false;
        Exception? fatal = null;

        // 分片级校验：每片写完立刻从磁盘回读该区间重算 SHA-256，与「边写边算」的
        // 摘要比对。它能抓住通常手段抓不到的问题：pwrite 偏移写错（数据落到别的分片
        // 位置）、同一区间被两个 worker 覆盖、磁盘静默损坏。
        // 代价是开启时多读一遍文件（opt-in），因此只统计失败次数，不保存全部摘要。
        int verifyFailures = 0;
        var verifyRetry = new ConcurrentDictionary<long, int>();
        const int MaxVerifyRetry = 2;

        // 每个区间连续失败次数（见 MaxChunkFailures）。上限来自设置项，让「重试次数」
        // 真正可调；夹到 1..10 防止误配成 0（一失败就报错）或过大（过期直链上白等）。
        int maxFails = Math.Clamp(options.ChunkRetries, 1, 10);
        var chunkFailures = new ConcurrentDictionary<long, int>();

        try
        {
            RandomAccess.SetLength(handle, total);


            if (resumed && meta != null)
            {
                done = IntervalSet.FromPairs(meta.Done.Select(d => (d.Start, d.End)));
                etagForIfRange = meta.ETag;
            }
            else
            {
                PartMetadata.Delete(partPath);
            }

            // 读缓冲来自设置（原项目「实验性功能 → 下载读缓冲」，默认 64KB，本版默认 96KB）
            int bufferSize = Math.Clamp(options.Tuning.ReadBufferBytes, 16 * 1024, 4096 * 1024);

            var pending = new ConcurrentQueue<(long Start, long End)>();
            var allocator = new Allocator(total, workers, chunkSize, done);

            var counter = new ProgressCounter(done.TotalBytes, total, progress);

            // ★ 线路列表必须基于**原始 URL** 展开。
            //   info.Url 已经是「某条线路拼好的地址」，若拿它再去 pool 展开，
            //   会生成 "gh-proxy.com/https://gh-proxy.com/..." 这种双重前缀（必然失败），
            //   并白跑一轮测速。这里用 originalUrl，并保证探测选中的线路排在最前。
            var urls = await _pool.OrderedUrlsAsync(originalUrl, force: false, log, ct)
                .ConfigureAwait(false);
            if (urls.Count == 0) urls.Add(("直连", originalUrl));
            else
            {
                // 把探测阶段已验证可用的那条线路提到首位
                int idx = urls.FindIndex(u => u.Name == info.MirrorName);
                if (idx > 0)
                {
                    var hit = urls[idx];
                    urls.RemoveAt(idx);
                    urls.Insert(0, hit);
                }
            }

            // ★ 全程共享一个 HttpClient：所有分片复用同一条连接池。
            //   若每个分片都 new 一个客户端，每次请求都要重新 TCP+TLS 握手，
            //   8 线程下握手开销会吃掉大部分带宽（这是很容易忽略但影响巨大的点）。
            using var sharedClient = _provider.CreateClient(forDownload: true);

            var watchdog = new SlowChunkWatchdog { Enabled = options.PreemptSlowChunks };
            var inflight = new ConcurrentDictionary<long, ChunkState>();
            var limiter = new SpeedLimiter(options.SpeedLimitBytesPerSec);

            var rangeIgnored = 0;
            var sw = Stopwatch.StartNew();

            // ---- 元数据落盘节流 ----
            // 每个分片完成都触发一次 SaveMeta，而 Save 内部是 fsync（Flush(flushToDisk:true)）。
            // 32 线程小块时这会把 worker 堵在磁盘上。这里按 1 秒合并；真正需要「立刻保住
            // 进度」的路径（取消 / 异常收尾）传 force:true。合并丢的是最多 1 秒的完成区间，
            // 方向是安全的（下次续传多下一个分片，不会重下已落盘之外的错误数据）。
            long lastMetaSaveTicks = 0;
            void PersistMeta(bool force)
            {
                long now = Stopwatch.GetTimestamp();
                if (!force)
                {
                    long last = Interlocked.Read(ref lastMetaSaveTicks);
                    if (now - last < MetaSaveIntervalTicks) return;
                    if (Interlocked.CompareExchange(ref lastMetaSaveTicks, now, last) != last) return;
                }
                else
                {
                    Interlocked.Exchange(ref lastMetaSaveTicks, now);
                }
                SaveMeta(partPath, info, chunkSize, done,
                    urls.Count > 0 ? urls[0].Name : info.MirrorName, "multi");
            }

            using var stopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            // ---- 看门狗：定时采样并抢占慢连接 ----
            var watchdogTask = Task.Run(async () =>
            {
                try
                {
                    while (!stopCts.IsCancellationRequested)
                    {
                        await Task.Delay(SlowChunkWatchdog.TickInterval, stopCts.Token)
                            .ConfigureAwait(false);

                        // 把实测总速度喂给分配器：块大小按「单 worker 能下 TargetSeconds 秒的量」自适应
                        allocator.RecentSpeedBps = (long)Math.Max(0, counter.Speed);

                        int taken = watchdog.Sweep(
                            inflight.Values.ToList(),
                            counter.Done, sw.Elapsed.TotalMilliseconds, workers,
                            m => log?.Invoke(m), options.Tuning);

                        for (int i = 0; i < taken; i++) onPreempt();
                    }
                }
                catch (OperationCanceledException) { /* 正常结束 */ }
            }, stopCts.Token);

            // ---- worker ----
            var workerTasks = new List<Task>();
            for (int w = 0; w < workers; w++)
            {
                int workerId = w;
                workerTasks.Add(Task.Run(async () =>
                {
                    // 错峰建连：最多 200ms，摊平 TCP/TLS 突发，降低触发镜像限流概率
                    if (options.StaggerConnect && workerId > 0)
                        await Task.Delay(Math.Min(workerId, 8) * 25, stopCts.Token).ConfigureAwait(false);

                    while (!stopCts.IsCancellationRequested)
                    {
                        (long Start, long End)? range;

                        if (pending.TryDequeue(out var p)) range = p;
                        else range = allocator.Take();

                        if (range == null)
                        {
                            // 队列空且已分配完：若所有区间都完成则收工
                            if (done.TotalBytes >= total) return;
                            await Task.Delay(50, stopCts.Token).ConfigureAwait(false);
                            continue;
                        }

                        var (start, end) = range.Value;
                        if (start > end) continue;
                        if (done.Contains(start) && done.EndOfContaining(start) >= end) continue;

                        var state = new ChunkState(start, end, workerId) { TotalSize = total };
                        if (options.VerifyChunks) state.BeginHash();
                        inflight[start] = state;

                        try
                        {
                            var outcome = await FetchRangeAsync(
                                sharedClient, handle, state, urls, etagForIfRange, limiter,
                                counter, stopCts.Token, log, bufferSize, options.Headers)
                                .ConfigureAwait(false);

                            switch (outcome)
                            {
                                case FetchOutcome.Completed:
                                    // 校验失败 → 该区间重新排队，并且**不**记为完成
                                    if (options.VerifyChunks && !await VerifyChunkAsync(
                                            handle, state, log, stopCts.Token)
                                        .ConfigureAwait(false))
                                    {
                                        Interlocked.Increment(ref verifyFailures);
                                        int tries = verifyRetry.AddOrUpdate(start, 1, (_, v) => v + 1);
                                        if (tries > MaxVerifyRetry)
                                        {
                                            // 同一区间反复校验不过：磁盘或引擎有问题，别再重试
                                            throw new DownloadFailedException(
                                                $"分片 {start}-{end} 连续 {tries} 次校验失败（写入数据与磁盘内容不一致）");
                                        }
                                        pending.Enqueue((start, end));
                                        break;
                                    }

                                    chunkFailures.TryRemove(start, out _);
                                    done.Add(start, end);
                                    PersistMeta(force: false);
                                    options.OnChunkCompleted?.Invoke(
                                        start, end - start + 1, sw.Elapsed.TotalSeconds);
                                    break;

                                case FetchOutcome.PreemptedOrPartial:
                                    // 慢连接被抢占：把**剩余部分**放回队列，已写字节保留。
                                    // 注意语义：这不是失败，不重试、不退避，立即由其他 worker 接手。
                                    state.ClearPreempt();
                                    long writtenEnd = start + state.Bytes - 1;
                                    if (writtenEnd < end)
                                        pending.Enqueue((writtenEnd + 1, end));
                                    else
                                        done.Add(start, end);
                                    PersistMeta(force: false);
                                    break;

                                case FetchOutcome.RangeIgnored:
                                    // 服务器忽略 Range：不能拿这个响应当分片写（它是整个文件）。
                                    // 容忍若干次（可能是 CDN 限流的中间态），超过则整任务降级单流。
                                    int n = Interlocked.Increment(ref rangeIgnored);
                                    log?.Invoke($"线路忽略 Range（{n}/{options.RangeIgnoredTolerance}）");
                                    if (n >= options.RangeIgnoredTolerance)
                                    {
                                        fallbackToSingle = true;
                                        stopCts.Cancel();
                                        return;
                                    }
                                    pending.Enqueue((start, end));
                                    break;

                                case FetchOutcome.Failed:
                                    int fails = chunkFailures.AddOrUpdate(start, 1, (_, v) => v + 1);
                                    if (fails > maxFails)
                                        throw new DownloadFailedException(
                                            $"分片 {start}-{end} 连续 {fails} 次失败（所有线路都试过了），" +
                                            "已保留断点");
                                    log?.Invoke($"分片 {start}-{end} 失败 {fails}/{maxFails}，稍后重试");
                                    pending.Enqueue((start, end));
                                    await Task.Delay(500, stopCts.Token).ConfigureAwait(false);
                                    break;
                            }
                        }
                        finally
                        {
                            inflight.TryRemove(start, out _);
                            // 释放分片摘要器（被抢占 / 失败的分片走不到 FinishHash）
                            state.Dispose();
                        }
                    }
                }, stopCts.Token));
            }

            try
            {
                await Task.WhenAll(workerTasks).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                stopCts.Cancel();
                PersistMeta(force: true);       // 取消必须立刻保住进度，不能被节流吞掉
                throw new DownloadCancelledException();
            }
            catch (Exception e)
            {
                fatal = e;
                try { PersistMeta(force: true); } catch { /* ignore */ }
            }
            finally
            {
                stopCts.Cancel();
                try { await watchdogTask.ConfigureAwait(false); } catch { /* ignore */ }
            }

            // 所有分片收工后补一次满额上报：最后一片的字节数很可能落在节流窗口内被丢掉，
            // 界面会停在「离 100% 差一点」的位置，而实际下载早已结束。
            if (fatal == null && !fallbackToSingle && !ct.IsCancellationRequested
                && done.TotalBytes >= total)
            {
                counter.ReportNow(DownloadPhase.Downloading,
                    doneOverride: total, speedOverride: 0);
            }


        }
        finally
        {
            // flush 失败不能掩盖原始异常（例如用户取消），
            // 否则界面显示的是 IOException 而不是"已取消"
            try { RandomAccess.FlushToDisk(handle); } catch { /* ignore */ }
            handle.Dispose();
        }

        if (fatal != null) throw new DownloadFailedException($"下载失败：{fatal.Message}");

        // ---- Range 降级：丢弃分片结果，改单流整文件重下 ----
        if (fallbackToSingle && !ct.IsCancellationRequested)
        {
            log?.Invoke("多条分片请求被忽略 Range，整任务降级为单流下载");
            File.Delete(partPath);
            PartMetadata.Delete(partPath);
            var singleInfo = new RemoteInfo
            {
                Url = info.Url, ResourceUrl = info.ResourceUrl,
                MirrorName = info.MirrorName, Total = info.Total,
                RangeSupported = false, FileName = info.FileName,
                ETag = info.ETag, LastModified = info.LastModified,
                ContentType = info.ContentType,
            };
            await DownloadSingleStreamAsync(singleInfo, originalUrl, partPath, options,
                progress, log, ct).ConfigureAwait(false);
            return (0, "single");
        }

        if (done.TotalBytes < total)
        {
            throw new DownloadFailedException(
                $"下载不完整：{Humanize.Size(done.TotalBytes)} / {Humanize.Size(total)}，已保留断点");
        }

        // 校验每个块的写入没有空洞（区间合并后应等于 total）
        var snapshot = done.Snapshot();
        if (snapshot.Count != 1 || snapshot[0].Start != 0 || snapshot[0].End != total - 1)
        {
            throw new DownloadFailedException("分片区间不连续，数据可能不完整，已保留断点");
        }

        if (verifyFailures > 0)
            log?.Invoke($"分片校验：{verifyFailures} 次回读不一致，已重下并全部通过");

        return (chunkCount, "multi");
    }

    // =====================================================================
    //  单分片抓取
    // =====================================================================

    private enum FetchOutcome
    {
        Completed,
        /// <summary>被抢占或因故中断，已写部分保留，剩余部分需要重新排队。</summary>
        PreemptedOrPartial,
        /// <summary>服务器返回 200（忽略 Range）。</summary>
        RangeIgnored,
        Failed,
    }

    /// <summary>
    /// 抓取一个区间写入 part 文件。
    ///
    /// <para>用 <see cref="RandomAccess.WriteAsync"/> 而不是 FileStream.Seek+Write：
    /// 前者基于 pwrite/WriteFile(OVERLAPPED)，<b>多线程写同一文件无需加锁</b>，
    /// 也不会移动共享的文件指针。这消除了原实现的写锁串行化开销。</para>
    /// </summary>
    /// <summary>
    /// 附加自定义请求头。
    /// 网盘直链普遍校验 Referer / Cookie / UA，缺一个就 403 —— 这是移植网盘支持的必要条件。
    /// 用 TryAddWithoutValidation：Cookie 里常含特殊字符，严格校验会抛异常。
    /// </summary>
    private static void ApplyCustomHeaders(HttpRequestMessage req,
        IReadOnlyDictionary<string, string> headers)
    {
        foreach (var (k, v) in headers)
        {
            if (string.IsNullOrEmpty(v)) continue;
            if (k.Equals("Range", StringComparison.OrdinalIgnoreCase) ||
                k.Equals("If-Range", StringComparison.OrdinalIgnoreCase))
                continue;   // 这两个由引擎自己控制，防止被覆盖导致区间错乱
            req.Headers.TryAddWithoutValidation(k, v);
        }
    }

    private async Task<FetchOutcome> FetchRangeAsync(
        HttpClient client,
        SafeFileHandle handle,
        ChunkState state,
        List<(string Name, string Url)> urls,
        string? ifRangeETag,
        SpeedLimiter limiter,
        ProgressCounter counter,
        CancellationToken ct,
        Action<string>? log,
        int bufferSize,
        IReadOnlyDictionary<string, string> headers)
    {
        long pos = state.Start;
        // 缓冲区按 chunk 剩余量裁剪，避免为一个小尾块也分配完整大缓冲
        var buffer = new byte[(int)Math.Min(bufferSize, Math.Max(4096, state.Size))];

        for (int attempt = 0; attempt < Math.Max(1, urls.Count) * 2; attempt++)
        {
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);

            var (name, baseUrl) = urls[attempt % urls.Count];

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, baseUrl);
                req.Headers.TryAddWithoutValidation("Range", $"bytes={pos}-{state.End}");
                ApplyCustomHeaders(req, headers);
                // ETag 未变才继续用断点：内容变了服务器会返回 200 全量，我们据此丢弃重下
                if (!string.IsNullOrEmpty(ifRangeETag))
                    req.Headers.TryAddWithoutValidation("If-Range", $"\"{ifRangeETag}\"");

                using var resp = await client
                    .SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                if (resp.StatusCode == HttpStatusCode.OK)
                    return FetchOutcome.RangeIgnored;

                if (resp.StatusCode != HttpStatusCode.PartialContent)
                {
                    if ((int)resp.StatusCode is 416 or 404 or 403)
                        log?.Invoke($"[{name}] HTTP {(int)resp.StatusCode}，换线路");

                    // ★ 429 / 503 是**限流**，不是资源问题：立刻换线路只会把每条线路
                    //   依次打成 429，最后一轮把 attempt 预算耗光 → 报「所有线路都试过了」，
                    //   而实际上等几百毫秒重试就能成功。这里按 Retry-After（有则用）退避。
                    if ((int)resp.StatusCode is 429 or 503)
                    {
                        TimeSpan wait = RetryDelayFor(resp, attempt);
                        log?.Invoke($"[{name}] HTTP {(int)resp.StatusCode} 限流，" +
                                    $"退避 {wait.TotalMilliseconds:0}ms 后重试");
                        await Task.Delay(wait, ct).ConfigureAwait(false);
                        continue;
                    }

                    continue;
                }

                // 校验服务器确实从我们要的位置开始（防镜像返回错位数据 = 静默损坏）
                var cr = resp.Content.Headers.ContentRange;

                // ★ 206 必须带 Content-Range，缺了就不能信。
                //
                //   早期写法是 `if (cr?.From is long from && from != pos)` —— 用可空模式匹配，
                //   **头缺失时整个判断为 false，直接放行**。而 206 响应不带 Content-Range
                //   本身就是协议违规：我们无从知道这段数据对应文件里的哪一段，
                //   却把它写到了 pos 处。一旦镜像返回的是别的区间，就是静默的数据损坏
                //   （文件长度对、哈希错）。
                //   按 RFC 7233，206 必须同时有 Content-Range 和正确的 Content-Length。
                if (cr == null)
                {
                    log?.Invoke($"[{name}] 206 响应缺少 Content-Range（协议违规），换线路");
                    continue;
                }

                if (cr.From is long from && from != pos)
                {
                    log?.Invoke($"[{name}] 区间不符（要 {pos}，给 {from}），换线路");
                    continue;
                }

                // 端点也要对：服务器可能给出一个「起点对、终点更短」的 206。
                // 那种情况下 while 循环会读到 EOF 而退出，把分片剩下的部分记成
                // PreemptedOrPartial 重新排队 —— 不至于损坏，但会白白多跑一趟；
                // 更糟的是总长不一致说明线路上的资源和探测时不是同一个版本。
                if (cr?.To is long crTo && crTo < state.End)
                {
                    log?.Invoke($"[{name}] 区间被截断（要至 {state.End}，只给到 {crTo}），换线路");
                    continue;
                }
                if (cr?.Length is long crLen && state.TotalSize > 0 && crLen != state.TotalSize)
                {
                    log?.Invoke($"[{name}] 资源总长变化（{crLen} ≠ {state.TotalSize}），换线路");
                    continue;
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct)
                    .ConfigureAwait(false);

                while (pos <= state.End)
                {
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);

                    // ★ 慢连接抢占：断开当前连接，已写字节保留，交回队列由别的连接续传
                    if (state.PreemptRequested) return FetchOutcome.PreemptedOrPartial;

                    int want = (int)Math.Min(buffer.Length, state.End - pos + 1);
                    int read = await stream.ReadAsync(buffer.AsMemory(0, want), ct)
                        .ConfigureAwait(false);
                    if (read <= 0) break;

                    await limiter.AcquireAsync(read, ct).ConfigureAwait(false);
                    await RandomAccess.WriteAsync(handle, buffer.AsMemory(0, read), pos, ct)
                        .ConfigureAwait(false);

                    // 边写边算首尾哨兵：不额外读盘，只多一点 CPU
                    state.HashBytes(pos, buffer.AsSpan(0, read));

                    pos += read;
                    state.AddBytes(read);
                    counter.Add(read);
                }

                return pos > state.End ? FetchOutcome.Completed : FetchOutcome.PreemptedOrPartial;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                continue;
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
                if (attempt % Math.Max(1, urls.Count) == 0)
                    log?.Invoke($"[{name}] 中断（{e.GetType().Name}），换线路重试");
            }
        }

        return FetchOutcome.Failed;
    }

    /// <summary>
    /// 限流退避时长：优先尊重服务器给的 <c>Retry-After</c>（秒数或 HTTP 日期），
    /// 否则按尝试次数指数退避。
    ///
    /// <para>上限夹在 10 秒：单个分片被限流时若睡太久，worker 会长时间空转，
    /// 反而拖慢整体；正确做法是短退避 + 让其它分片继续推进。</para>
    /// </summary>
    private static TimeSpan RetryDelayFor(HttpResponseMessage resp, int attempt)
    {
        var retryAfter = resp.Headers.RetryAfter;
        if (retryAfter?.Delta is TimeSpan delta && delta > TimeSpan.Zero)
            return delta > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : delta;

        if (retryAfter?.Date is DateTimeOffset date)
        {
            var wait = date - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
                return wait > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : wait;
        }

        // 400ms, 800ms, 1.6s … 上限 5s
        double ms = Math.Min(5000, 400 * Math.Pow(2, Math.Min(attempt, 6)));
        return TimeSpan.FromMilliseconds(ms);
    }

    // =====================================================================
    //  分片落盘校验
    // =====================================================================

    /// <summary>
    /// 把分片的<b>首尾哨兵区间</b>从磁盘读回来重算 SHA-256，与写入时的摘要比对。
    ///
    /// <para>为什么不整片读：整片校验等于把下载 IO 翻倍（50GB 任务在机械盘上要多读十几分钟），
    /// 而这个开关默认是开的。首尾抽查（各 16KB）开销约 0.06%~6%，足够抓住
    /// 「pwrite 偏移算错」「分片区间重叠」「数据没落盘」这三类最常见的错误 ——
    /// 它们都会让分片边界处的字节对不上。</para>
    ///
    /// <para>中段静默损坏由整文件 <see cref="DownloadOptions.VerifyHash"/> 兜底。</para>
    /// </summary>
    /// <returns>一致返回 true；不一致返回 false（调用方会重下这个区间）。</returns>
    private static async Task<bool> VerifyChunkAsync(
        SafeFileHandle handle, ChunkState state, Action<string>? log, CancellationToken ct)
    {
        var expected = state.FinishHash();
        if (expected == null) return true;      // 未启用摘要

        var (headHex, tailHex, headLen, tailLen) = expected.Value;

        if (!await ProbeMatchesAsync(handle, state.Start, headLen, headHex, ct).ConfigureAwait(false))
        {
            log?.Invoke($"分片 {state.Start}-{state.End} 头部校验不一致（偏移写错或落盘失败）");
            return false;
        }

        long tailStart = state.End - tailLen + 1;
        if (!await ProbeMatchesAsync(handle, tailStart, tailLen, tailHex, ct).ConfigureAwait(false))
        {
            log?.Invoke($"分片 {state.Start}-{state.End} 尾部校验不一致（与相邻分片重叠或被覆盖）");
            return false;
        }

        return true;
    }

    private static async Task<bool> ProbeMatchesAsync(SafeFileHandle handle, long offset,
        int length, string expectedHex, CancellationToken ct)
    {
        if (length <= 0) return true;

        var buffer = new byte[length];
        int total = 0;
        while (total < length)
        {
            int read = await RandomAccess.ReadAsync(handle,
                buffer.AsMemory(total, length - total), offset + total, ct).ConfigureAwait(false);
            if (read <= 0) return false;        // 提前 EOF = 这段没写进去
            total += read;
        }

        string actual = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(buffer)).ToLowerInvariant();
        return string.Equals(actual, expectedHex, StringComparison.Ordinal);
    }

    // =====================================================================
    //  单流下载
    // =====================================================================

    /// <summary>
    /// 单流下载（服务器不支持 Range 时的降级路径，或小文件）。
    ///
    /// <para><b>一个必须守住的正确性边界</b>：续传时若服务器忽略 Range 返回 200（全量），
    /// <b>绝对不能把全量响应追加到已有数据后面</b> —— 那会拼出一个"旧数据 + 完整文件"的
    /// 损坏文件。必须校验 <c>Content-Range</c> 起始位置，不匹配就从头覆盖。</para>
    /// </summary>
    private async Task DownloadSingleStreamAsync(
        RemoteInfo info,
        string originalUrl,
        string partPath,
        DownloadOptions options,
        IProgress<DownloadProgress>? progress,
        Action<string>? log,
        CancellationToken ct)
    {
        long total = info.Total;
        long existing = options.Resume && File.Exists(partPath)
            ? new FileInfo(partPath).Length : 0;

        // 注意：这里**不能**因为 existing >= total 就清零。
        // 那意味着「磁盘上已经是一个完整文件」，清零会让它从头重下。
        // 完整的情况在下面的校准段里直接返回（跳过传输）。

        var counter = new ProgressCounter(existing, total, progress);
        var limiter = new SpeedLimiter(options.SpeedLimitBytesPerSec);
        var urls = await _pool.OrderedUrlsAsync(originalUrl, force: false, log, ct)
            .ConfigureAwait(false);
        if (urls.Count == 0) urls.Add(("直连", originalUrl));

        // 单流的断点元数据写入（1 秒节流）。定义在循环外，让校准段也能用。
        long lastMetaTicks = 0;
        void SaveSingleProgress(long written, bool force = false)
        {
            if (written <= 0) return;
            long nowTicks = Stopwatch.GetTimestamp();
            if (!force && lastMetaTicks != 0
                && nowTicks - lastMetaTicks < Stopwatch.Frequency) return;   // 1 秒节流
            lastMetaTicks = nowTicks;
            try
            {
                new PartMetadata
                {
                    UrlKey = PartMetadata.UrlKeyOf(originalUrl),
                    Total = total,
                    ETag = info.ETag ?? "",
                    ChunkSize = 0,
                    Mode = "single",
                    Mirror = info.MirrorName,
                    Done = new List<IntervalDto> { new() { Start = 0, End = written - 1 } },
                }.Save(partPath);
            }
            catch { /* 元数据写失败不影响下载；最坏是下次从头下 */ }
        }

        string lastError = "未知";
        foreach (var (name, url) in urls)
        {
            bool append = false;
            try
            {
                // ★ 发请求之前，先把 existing 校准成**磁盘上的真实长度**。
                //
                //   早期 existing 来自元数据里记录的完成区间，而元数据按 1 秒节流写盘 ——
                //   异常退出后，磁盘上的 part 常常比元数据新。那时会出现
                //   「Range 从 existing 请求，却 Seek 到 onDisk 写入」的错位：
                //   数据整体后移、文件长度超标，直到最后长度校验才暴露，整轮白下。
                //
                //   在构造请求头之前就统一到 onDisk，请求起点与写入位置天然一致，
                //   不需要发第二次请求。onDisk < existing 说明文件被截短，断点不可信，从头来。
                if (existing > 0 && File.Exists(partPath))
                {
                    long onDisk = new FileInfo(partPath).Length;
                    if (onDisk != existing)
                    {
                        log?.Invoke($"断点长度校准：元数据 {existing} → 磁盘 {onDisk}");
                        existing = onDisk;
                        counter.Reset(existing);
                    }
                    if (existing <= 0 || (total > 0 && existing >= total))
                    {
                        // 磁盘已完整（或空）：无需续传
                        if (total > 0 && existing >= total)
                        {
                            log?.Invoke($"断点文件已达总长（{existing}/{total}），跳过传输");
                            SaveSingleProgress(existing, force: true);
                            counter.ReportNow(DownloadPhase.Downloading,
                                doneOverride: total, speedOverride: 0);
                            return;
                        }
                        existing = 0;
                        counter.Reset(0);
                    }
                }

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                ApplyCustomHeaders(req, options.Headers);
                if (existing > 0)
                {
                    req.Headers.TryAddWithoutValidation("Range", $"bytes={existing}-");
                    if (!string.IsNullOrEmpty(info.ETag))
                        req.Headers.TryAddWithoutValidation("If-Range", $"\"{info.ETag}\"");
                }

                using var client = _provider.CreateClient(forDownload: true);
                using var resp = await client.SendAsync(req,
                    HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if (resp.StatusCode >= HttpStatusCode.BadRequest)
                {
                    lastError = $"HTTP {(int)resp.StatusCode}";
                    continue;
                }

                if (existing > 0 && resp.StatusCode == HttpStatusCode.PartialContent)
                {
                    var from = resp.Content.Headers.ContentRange?.From;
                    append = from == existing;
                    if (!append)
                    {
                        log?.Invoke("该线路忽略 Range，放弃续传，从头下载");
                        existing = 0;
                        counter.Reset(0);
                    }
                }
                else if (existing > 0)
                {
                    // 200 = 全量。必须覆盖写，不能 append
                    log?.Invoke("服务器返回全量数据，从头覆盖（丢弃旧断点）");
                    existing = 0;
                    counter.Reset(0);
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(ct)
                    .ConfigureAwait(false);
                int singleBuf = Math.Clamp(options.Tuning.ReadBufferBytes,
                    DefaultBufferSize / 4, 4096 * 1024);

                // ★ 续传不能用 FileMode.Append。
                //   Append 只往文件末尾追加，而「existing」这个续传起点是在写之前
                //   读到的长度快照。两者一旦不一致 —— 例如上次异常退出留下了比
                //   existing 更长的 part、或用户手动改了文件 —— Append 就会把数据
                //   接到错误位置，落出一个长度超标、内容错位的文件（而且是静默的，
                //   只在最后「长度不符」时才发现，白白浪费一次全程下载）。
                //   改成 Open + Seek 到已有长度：写入位置由我们显式控制，天然幂等。
                await using var fs = append
                    ? new FileStream(partPath, FileMode.OpenOrCreate, FileAccess.Write,
                        FileShare.Read, singleBuf, FileOptions.Asynchronous)
                    : new FileStream(partPath, FileMode.Create, FileAccess.Write,
                        FileShare.Read, singleBuf, FileOptions.Asynchronous);

                if (append)
                {
                    long onDisk = fs.Length;
                    if (onDisk != existing)
                    {
                        // 打开文件后长度又变了（极罕见：另一个进程在写，或刚才校准后又被截断）。
                        // 此时请求起点与写入位置已经不一致，不能硬写 —— 放弃续传改从头，
                        // 宁可多下一次也不能产出错位文件。
                        log?.Invoke($"续传起点与磁盘不一致（请求 {existing}，磁盘 {onDisk}），改为从头下载");
                        append = false;
                        existing = 0;
                        fs.SetLength(0);
                        counter.Reset(0);
                    }
                    else
                    {
                        fs.Seek(existing, SeekOrigin.Begin);
                    }
                }

                var buffer = new byte[singleBuf];

                // 单流的断点元数据由方法级 SaveSingleProgress 处理（定义见方法开头）：
                // 单流是顺序写，区间永远是 [0, 已写字节-1]，1 秒节流即可。
                // 没有它的话复用判断拿不到元数据 → 单流永远无法续传。

                long writtenTotal = append ? existing : 0;
                while (true)
                {
                    int read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (read <= 0) break;

                    await limiter.AcquireAsync(read, ct).ConfigureAwait(false);
                    await fs.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    counter.Add(read);

                    writtenTotal += read;
                    SaveSingleProgress(writtenTotal);
                }

                await fs.FlushAsync(ct).ConfigureAwait(false);

                // 收尾再写一次（无视节流）：赶在 1 秒窗口内的最后一段字节也必须落盘，
                // 否则断点会永远落后最多 1 秒的进度。
                SaveSingleProgress(writtenTotal, force: true);

                // 单流写完必须补一次「满额 + 速度归零」的上报。
                // 否则最后那段不足 120ms 的字节数会被节流吞掉，界面停在
                // 「26.03 / 26.06 MB」不动，而文件其实已经完整 —— 用户会以为卡住了。
                counter.ReportNow(DownloadPhase.Downloading,
                    doneOverride: total > 0 ? total : counter.Done,
                    speedOverride: 0);

                log?.Invoke($"线路 [{name}] 单流下载完成{(append ? "（续传）" : "")}");
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw new DownloadCancelledException();
            }
            catch (Exception e)
            {
                lastError = $"{e.GetType().Name}: {e.Message}";
                log?.Invoke($"线路 [{name}] 失败：{lastError}");
                try
                {
                    if (options.Resume && File.Exists(partPath))
                    {
                        existing = new FileInfo(partPath).Length;
                        counter.Reset(existing);
                    }
                }
                catch { /* ignore */ }
            }
        }

        throw new DownloadFailedException($"下载失败：{lastError}");
    }

    private static void SaveMeta(string partPath, RemoteInfo info, long chunkSize,
        IntervalSet done, string mirror, string mode)
    {
        var meta = new PartMetadata
        {
            UrlKey = PartMetadata.UrlKeyOf(info.ResourceUrl),
            Total = info.Total,
            ETag = info.ETag,
            LastModified = info.LastModified,
            ChunkSize = chunkSize,
            Mode = mode,
            Mirror = mirror,
            Done = done.Snapshot().Select(x => new IntervalDto { Start = x.Start, End = x.End })
                .ToList(),
        };
        meta.Save(partPath);
    }

    // =====================================================================
    //  区间分配器（含尾部收缩）
    // =====================================================================

    /// <summary>
    /// 按字节顺序派发区间的分配器，带**尾部收缩**。
    ///
    /// <para>核心矛盾：块太大会让尾部只剩少数几个大块 → 空闲连接空转；
    /// 块太小会产生海量 Range 请求 → 拖慢快连接。</para>
    ///
    /// <para>解法：常态按自适应块派发；当剩余不足 workers 个整块时，
    /// 把块压到 <c>remaining / workers</c>，让全部连接并行冲完最后一段。</para>
    /// </summary>
    private sealed class Allocator
    {
        private readonly long _total;
        private readonly int _workers;
        private readonly long _baseChunk;
        private readonly IntervalSet _done;
        private readonly object _gate = new();
        private long _nextStart;

        /// <summary>
        /// 最近实测总速度（由看门狗每 5 秒注入；0 = 未探测）。
        ///
        /// <para><b>必须 volatile</b>：写入方是看门狗线程、读取方是 N 个 worker 线程。
        /// 非 volatile 的 long 在 32 位进程里可能被撕裂读到半写值（极小概率但后果是
        /// 块大小算错）。volatile 保证读到的是某个完整的历史值，这就够了 ——
        /// 块大小本来就允许滞后。</para>
        /// </summary>
        public long RecentSpeedBps
        {
            get => Volatile.Read(ref _recentSpeedBps);
            set => Volatile.Write(ref _recentSpeedBps, value);
        }

        private long _recentSpeedBps;

        public Allocator(long total, int workers, long baseChunk, IntervalSet done)
        {
            _total = total;
            _workers = workers;
            _baseChunk = baseChunk;
            _done = done;
            _nextStart = 0;
        }

        public (long Start, long End)? Take()
        {
            lock (_gate)
            {
                while (_nextStart < _total)
                {
                    // 跳过已完成的区间（断点续传时可能已有前缀）
                    long endOfDone = _done.EndOfContaining(_nextStart);
                    if (endOfDone >= 0)
                    {
                        _nextStart = endOfDone + 1;
                        continue;
                    }

                    long remaining = _total - _nextStart;
                    long block = ChunkPlanner.AdaptiveBlockSize(
                        RecentSpeedBps, _workers, _baseChunk);

                    // ★ 尾部收缩
                    if (remaining <= (long)_workers * block)
                        block = ChunkPlanner.TailBlockSize(remaining, _workers, block);

                    // 兜底：block 由外部注入的速度算得，理论上恒正；但若将来有人把
                    // 参数改成非法值导致 block <= 0，end 会小于 start，_nextStart 可能
                    // 原地踏步甚至回退 → 生成重叠区间。这里钳一下，让分配器永远前进。
                    if (block <= 0) block = Math.Max(1, _baseChunk);

                    long start = _nextStart;
                    long end = Math.Min(start + block - 1, _total - 1);
                    _nextStart = end + 1;
                    return (start, end);
                }
                return null;
            }
        }
    }

    // =====================================================================
    //  进度统计（带节流）
    // =====================================================================

    private sealed class ProgressCounter
    {
        private readonly IProgress<DownloadProgress>? _progress;
        private readonly long _total;
        private long _done;
        private long _lastReportTicks;
        private long _lastBytes;
        private long _lastSampleTicks;
        private double _speed;

        public ProgressCounter(long initial, long total, IProgress<DownloadProgress>? progress)
        {
            _done = initial;
            _total = total;
            _progress = progress;
            _lastSampleTicks = Stopwatch.GetTimestamp();
            _lastReportTicks = 0;
            _lastBytes = initial;
        }

        public long Done => Interlocked.Read(ref _done);

        /// <summary>平滑后的瞬时速度（字节/秒）。</summary>
        public double Speed => _speed;

        public void Reset(long value)
        {
            Interlocked.Exchange(ref _done, value);
            _lastBytes = value;
            _lastSampleTicks = Stopwatch.GetTimestamp();
        }

        /// <summary>
        /// 无视节流，立刻上报一次当前进度（可指定阶段）。
        ///
        /// <para><b>为什么必须有</b>：<see cref="Add"/> 有 120ms 节流，而且只在「有字节流入」时
        /// 才会被调用。下载停下之后（读缓冲收尾、合并、校验、改名）进度就再也没有上报点 ——
        /// 界面会永远停在最后一次节流上报的那一刻：字节数差几十 KB 到不了头、进度条差一格、
        /// 阶段名还写着「下载中」。实测最刺眼的是**单流全部写完但收尾较慢**时，
        /// 界面显示「26.03 / 26.06 MB　探测中」这种自相矛盾的状态。</para>
        /// </summary>
        public void ReportNow(string phase, long? doneOverride = null, double? speedOverride = null)
        {
            long done = doneOverride ?? Done;
            if (doneOverride.HasValue) Interlocked.Exchange(ref _done, doneOverride.Value);
            _progress?.Report(new DownloadProgress(done, _total,
                speedOverride ?? _speed, phase));
        }

        public void Add(int n)
        {
            Interlocked.Add(ref _done, n);

            long now = Stopwatch.GetTimestamp();
            long nowMs = now * 1000 / Stopwatch.Frequency;

            // 节流：多 worker 高频回调下，最多每 120ms 上报一次。
            // 用 Interlocked.Exchange 抢占上报权，保证同一时刻只有一个线程做计算与回调。
            long last = Interlocked.Read(ref _lastReportTicks);
            if (nowMs - last < ProgressThrottleMs) return;
            if (Interlocked.CompareExchange(ref _lastReportTicks, nowMs, last) != last) return;

            // 速度用 0.3s 以上的窗口算瞬时值，再做指数平滑避免数字乱跳
            double elapsed = (now - _lastSampleTicks) / (double)Stopwatch.Frequency;
            if (elapsed >= 0.3)
            {
                long cur = Done;
                double inst = (cur - _lastBytes) / elapsed;
                _speed = _speed <= 0 ? inst : _speed * 0.6 + inst * 0.4;
                _lastBytes = cur;
                _lastSampleTicks = now;
            }

            _progress?.Report(new DownloadProgress(Done, _total, _speed,
                DownloadPhase.Downloading));
        }
    }
}
