namespace YunX.Core.Download;

/// <summary>
/// 分片规划：决定切多少块、每块多大。
///
/// <para><b>这里修掉了原 Python 版一个实测暴露的算术缺陷。</b></para>
///
/// <para>原实现是「先定块大小、再算块数」：</para>
/// <code>
/// chunk = clamp(total // (threads * 2), 1MB, 24MB)   # 注意是整除（向下取整）
/// n_chunks = ceil(total / chunk)
/// </code>
/// <para>
/// 17,840,399 字节 / 8 线程 → chunk = 1,115,024（floor），n = 17 块，
/// 于是最后一块只剩 <b>15 字节</b> —— 实测日志里真的出现过 `块=15 B`。
/// 更糟的是"最后一块"往往是收尾时唯一在跑的，一个 15 字节的块也要走一次完整的
/// HTTP 请求 + 握手。
/// </para>
///
/// <para><b>正确做法是反过来：先定目标块数，再用 ceil 反算块大小</b>。
/// 这样所有块近似等长，尾块不会退化成碎片。</para>
/// </summary>
public static class ChunkPlanner
{
    /// <summary>常态块下限：太小的块会让 Range 请求数暴涨。</summary>
    public const long MinChunk = 256 * 1024;

    /// <summary>常态块上限：太大的块会降低动态性，收尾时一个巨块拖垮总耗时。</summary>
    public const long MaxChunk = 4 * 1024 * 1024;

    /// <summary>尾部收缩的块下限：再细就得不偿失。</summary>
    public const long MinTailChunk = 64 * 1024;

    /// <summary>自适应支点：期望单个 worker 下载一块耗时约这么多秒。</summary>
    public const int TargetSeconds = 5;

    /// <summary>目标块数 = 线程数 × 该系数（略大于线程数，保证动态负载均衡有余量）。</summary>
    public const int ChunksPerWorker = 2;

    /// <summary>
    /// 计算块大小。<b>用 ceil 反算，避免产生极小的尾块。</b>
    /// </summary>
    public static long ComputeChunkSize(long total, int threads)
    {
        if (total <= 0) return MinChunk;
        int workers = Math.Max(1, threads);
        int targetChunks = Math.Max(1, workers * ChunksPerWorker);

        // 关键：Math.Ceiling 而不是整除
        long chunk = (long)Math.Ceiling((double)total / targetChunks);

        // 小于 MinChunk 的文件不做"分组"：直接一块装下。
        // 否则 13 字节的文件也会被显示成"1 块 × 256KB"，看起来像 bug（实际无害）。
        if (chunk < MinChunk) return total;
        return Math.Clamp(chunk, MinChunk, MaxChunk);
    }

    /// <summary>按块大小推算块数。</summary>
    public static int ComputeChunkCount(long total, long chunkSize)
    {
        if (total <= 0 || chunkSize <= 0) return 0;
        return (int)((total + chunkSize - 1) / chunkSize);
    }

    /// <summary>
    /// 自适应块大小：块 ≈ 单 worker 在 <see cref="TargetSeconds"/> 秒内能下完的量。
    /// 快连接给大块（省 Range 开销），慢连接给小块（充分并行、摊平拖尾）。
    /// </summary>
    public static long AdaptiveBlockSize(long recentSpeedBps, int workers, long fallbackChunk)
    {
        int w = Math.Max(1, workers);
        long perWorker = recentSpeedBps / w;
        if (perWorker <= 0) return Math.Clamp(fallbackChunk, MinChunk, MaxChunk);
        return Math.Clamp(perWorker * TargetSeconds, MinChunk, MaxChunk);
    }

    /// <summary>尾部收缩后的块大小：剩余不足 workers 个整块时，均分到 workers 份。</summary>
    public static long TailBlockSize(long remaining, int workers, long baseBlock)
    {
        int w = Math.Max(1, workers);
        long fair = remaining / w;
        return Math.Clamp(fair, MinTailChunk, Math.Max(MinTailChunk, baseBlock));
    }
}
