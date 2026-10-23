using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using YunX.Core.Download;
using YunX.Core.Mirrors;
using YunX.Core.Net;

/// <summary>
/// 高频小分片压力测试。
///
/// <para><b>为什么需要这个</b>：net48 的 <c>PwriteStream</c> 用一把 <c>_ioGate</c>
/// 串行化「提交 + 等结果」（详见 PwriteStream 的注释）。锁本身是正确的，
/// 但要验证两件事：</para>
///
/// <list type="number">
/// <item><b>正确性</b>：32 线程 × 极小分片时，锁不会让数据错位或丢失字节</item>
/// <item><b>性能</b>：锁的竞争开销在真实分片规模下不会显著退化吞吐</item>
/// </list>
///
/// <para>分片越小、线程越多，锁竞争越激烈 —— 这是最坏情况下的压力测试。</para>
/// </summary>
internal static class StressTest
{
    /// <summary>32 线程 × 极小分片：锁竞争最坏情况下的正确性与吞吐。</summary>
    public static async Task<int> RunAsync(string tfm)
    {
        Console.WriteLine("============================================================");
        Console.WriteLine($"  分片压力测试（锁竞争最坏情况）  [{tfm}]");
        Console.WriteLine("============================================================");
        Console.WriteLine();

        int failed = 0;
        failed += await StressAsync(tfm, sizeMB: 16, threads: 32, rounds: 3).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "全部通过" : $"失败 {failed} 项");
        return failed;
    }

    private static async Task<int> StressAsync(string tfm, int sizeMB, int threads, int rounds)
    {
        Console.WriteLine($"--- {sizeMB} MB / {threads} 线程 / 256KB 分片 ---");
        int failed = 0;
        var speeds = new List<double>();

        for (int r = 0; r < rounds; r++)
        {
            using var server = new FastFileServer(sizeMB * 1024 * 1024);
            string dir = Path.Combine(Path.GetTempPath(),
                "yunx_stress_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);

            try
            {
                var provider = new HttpClientProvider(NetworkMode.Direct);
                try
                {
                    var pool = new MirrorPool(Array.Empty<Mirror>(),
                        provider.CreateClient(forDownload: true), provider);
                    var engine = new DownloadEngine(provider, pool);

                    // 关键：VerifyChunks = true。这是唯一能证明
                    // 「32 线程并发写没有互相覆盖」的检查 ——
                    // 它会把每个分片的首尾 16KB 从磁盘读回来重算 SHA-256。
                    var sw = Stopwatch.StartNew();
                    var result = await engine.DownloadAsync(server.Url, dir, "stress.bin",
                        new DownloadOptions
                        {
                            Threads = threads,
                            VerifyChunks = true,
                            PreemptSlowChunks = false,
                            StaggerConnect = false,
                        }, null, null, CancellationToken.None).ConfigureAwait(false);
                    sw.Stop();

                    long bytes = new FileInfo(result.FilePath).Length;
                    double mbps = bytes / 1024.0 / 1024.0 / sw.Elapsed.TotalSeconds;
                    speeds.Add(mbps);

                    // 全文件内容校验：分片校验只查首尾，这里查全部
                    byte[] onDisk = File.ReadAllBytes(result.FilePath);
                    bool contentOk = onDisk.Length == server.Payload.Length
                                     && onDisk.AsSpan().SequenceEqual(server.Payload);

                    Console.WriteLine($"  第 {r + 1} 轮: {sw.Elapsed.TotalSeconds:F2}s  " +
                                      $"{mbps:F1} MB/s  {result.ChunkCount} 分片  " +
                                      $"内容{(contentOk ? "一致 ✓" : "不一致 ✗")}");

                    if (!contentOk) failed++;
                    if (result.ChunkCount < 2)
                    {
                        Console.WriteLine("  [FAIL] 分片数 < 2，说明没真的并发");
                        failed++;
                    }
                }
                finally { provider.Dispose(); }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        speeds.Sort();
        Console.WriteLine($"  >> 中位: {speeds[speeds.Count / 2]:F1} MB/s");
        return failed;
    }
}

/// <summary>不限速的本地文件服务（压测用）。</summary>
internal sealed class FastFileServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _accept;

    public byte[] Payload { get; }
    public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/f.bin";

    public FastFileServer(int size)
    {
        Payload = new byte[size];
        new Random(20261023).NextBytes(Payload);
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _accept = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient c;
            try
            {
#if NET48
                c = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
#else
                c = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
#endif
            }
            catch { return; }
            _ = Task.Run(() => HandleAsync(c), CancellationToken.None);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                using var ns = client.GetStream();
                using var reader = new StreamReader(ns, Encoding.ASCII, false, 4096, leaveOpen: true);

                string? req = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(req)) return;

                long start = 0, end = Payload.Length - 1;
                bool hasRange = false;
                while (true)
                {
                    string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null || line.Length == 0) break;
                    if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                    {
                        string v = line.Substring(6).Trim();
                        int eq = v.IndexOf('=');
                        if (eq >= 0) v = v.Substring(eq + 1);
                        int dash = v.IndexOf('-');
                        if (dash > 0 && long.TryParse(v.Substring(0, dash), out long s)
                            && long.TryParse(v.Substring(dash + 1), out long e))
                        {
                            start = s; end = Math.Min(e, Payload.Length - 1); hasRange = true;
                        }
                    }
                }

                long len = end - start + 1;
                var sb = new StringBuilder();
                sb.Append(hasRange ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
                if (hasRange) sb.Append($"Content-Range: bytes {start}-{end}/{Payload.Length}\r\n");
                sb.Append("Content-Type: application/octet-stream\r\n");
                sb.Append($"Content-Length: {len}\r\n");
                sb.Append("Accept-Ranges: bytes\r\n");
                sb.Append("Connection: keep-alive\r\n\r\n");

                byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
                await ns.WriteAsync(head, 0, head.Length, _cts.Token).ConfigureAwait(false);
                await ns.WriteAsync(Payload, (int)start, (int)len, _cts.Token).ConfigureAwait(false);
                await ns.FlushAsync(_cts.Token).ConfigureAwait(false);
            }
            catch { }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { _accept.Wait(2000); } catch { }
        _cts.Dispose();
    }
}
