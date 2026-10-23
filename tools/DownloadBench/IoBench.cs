using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// 直接对比「IO 提交方式」本身的开销，排除引擎、HTTP、分片逻辑的干扰。
///
/// <para>假设：net8 的 RandomAccess.WriteAsync 每次都走 IOCP 投递 + 完成通知，
/// 而 net48 的同步 WriteFile 直接命中页缓存返回。在本地回环（服务器不缺数据流）时，
/// 前者会成为瓶颈，后者不会。</para>
///
/// <para>如果这个测试也显示 net8 慢好几倍，就证明差距来自 IO 提交方式本身，
/// 而不是分片策略或锁。</para>
/// </summary>
internal static class IoBench
{
    private const int BufferSize = 16 * 1024;
    private const int TotalWrites = 8192;      // 8192 × 16KB = 128MB

    public static async Task RunAsync()
    {
        Console.WriteLine("============================================================");
        Console.WriteLine("  IO 提交方式对比（排除引擎干扰）");
        Console.WriteLine("============================================================");
        Console.WriteLine($"  16KB × {TotalWrites} 次 = {TotalWrites * BufferSize / 1024 / 1024} MB");
        Console.WriteLine();

        string dir = Path.Combine(Path.GetTempPath(), "yunx_iobench_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);

        try
        {
            var payload = new byte[BufferSize];
            new Random(1).NextBytes(payload);
            long total = (long)TotalWrites * BufferSize;

            // ---- A. 单线程顺序写 ----
            await MeasureAsync("A. 单线程顺序写", Path.Combine(dir, "a.bin"), total,
                async (w, ct) =>
                {
                    for (int i = 0; i < TotalWrites; i++)
                        await w(payload, (long)i * BufferSize, ct).ConfigureAwait(false);
                }).ConfigureAwait(false);

            // ---- B. 8 线程并发写不同区间 ----
            await MeasureAsync("B. 8 线程并发写", Path.Combine(dir, "b.bin"), total,
                async (w, ct) =>
                {
                    var tasks = new Task[8];
                    int perThread = TotalWrites / 8;
                    for (int t = 0; t < 8; t++)
                    {
                        int tid = t;
                        tasks[t] = Task.Run(async () =>
                        {
                            for (int i = 0; i < perThread; i++)
                            {
                                long pos = ((long)tid * perThread + i) * BufferSize;
                                await w(payload, pos, ct).ConfigureAwait(false);
                            }
                        }, ct);
                    }
                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }).ConfigureAwait(false);

            // ---- C. 同步 FileStream.Write（两框架都有的基准线）----
            await MeasureSyncAsync("C. 同步 FileStream.Write（基准线）",
                Path.Combine(dir, "c.bin"), payload).ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private static async Task MeasureAsync(string name, string path, long total,
        Func<Func<byte[], long, CancellationToken, Task>, CancellationToken, Task> body)
    {
        using var ct = new CancellationTokenSource();

        // 预热：让 JIT 和页缓存进入稳态
        using (var warm = YunX.Core.Download.PositionalFile.Open(path, createNew: true))
        {
            warm.SetLength(total);
            var buf = new byte[BufferSize];
            for (int i = 0; i < 64; i++)
                await warm.WriteAsync(buf, 0, BufferSize, (long)i * BufferSize, ct.Token)
                    .ConfigureAwait(false);
            warm.FlushToDisk();
        }

        var times = new double[3];
        for (int r = 0; r < 3; r++)
        {
            using (var f = YunX.Core.Download.PositionalFile.Open(path, createNew: true))
            {
                f.SetLength(total);
                var sw = Stopwatch.StartNew();
                await body(async (buf, pos, token) =>
                {
                    await f.WriteAsync(buf, 0, BufferSize, pos, token).ConfigureAwait(false);
                }, ct.Token).ConfigureAwait(false);
                f.FlushToDisk();
                sw.Stop();
                times[r] = sw.Elapsed.TotalSeconds;
            }
        }

        Array.Sort(times);
        double med = times[1];
        Console.WriteLine($"  {name}: {med:F3}s  {total / 1024.0 / 1024.0 / med:F1} MB/s");
    }

    private static async Task MeasureSyncAsync(string name, string path, byte[] payload)
    {
        long total = (long)TotalWrites * BufferSize;
        var times = new double[3];

        for (int r = 0; r < 3; r++)
        {
            var sw = Stopwatch.StartNew();
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read,
                4096, FileOptions.None))
            {
                fs.SetLength(total);
                for (int i = 0; i < TotalWrites; i++)
                    fs.Write(payload, 0, BufferSize);
            }
            sw.Stop();
            times[r] = sw.Elapsed.TotalSeconds;
        }

        Array.Sort(times);
        double med = times[1];
        Console.WriteLine($"  {name}: {med:F3}s  {total / 1024.0 / 1024.0 / med:F1} MB/s");
    }
}
