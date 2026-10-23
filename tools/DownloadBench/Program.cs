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
/// 下载速度基准：同一个场景在 net8 / net48 下各跑一遍，用于量化差异。
///
/// <para><b>为什么不能直接拿 LocalFileServer 压满速度</b>：那是本地回环，
/// 吞吐在 GB/s 量级，瓶颈落在 TCP 栈和 Task 调度上，而不是我们关心的写盘路径。
/// 结论会变成「两边一样快」但原因是「两边都没到瓶颈」，毫无参考价值。</para>
///
/// <para><b>做法</b>：服务端按 16KB 一包、带 pacing 延迟地吐数据，
/// 把有效带宽限制在接近真实宽带（约 1.2~1.5 MB/s）的水平，
/// 这样测的就是「引擎在真实网速下有没有额外开销」——
/// 正是用户能感知到的那个指标。</para>
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        string tfm =
#if NET48
            "net48";
#else
            "net8.0-windows";
#endif

        Console.WriteLine("============================================================");
        Console.WriteLine($"  下载速度基准  [{tfm}]");
        Console.WriteLine("============================================================");
        Console.WriteLine();

        // ---- 场景 1：真实宽带节奏下的多线程下载 ----
        // 8MB 文件 / 8 线程，服务端限速 ~1.5MB/s（模拟 gh-proxy 的真实带宽）
        await RunCaseAsync(tfm, "慢速宽带 · 8MB / 8 线程", sizeMB: 8, threads: 8,
            serverDelayMs: 10, rounds: 3).ConfigureAwait(false);

        // ---- 场景 2：不限速（压测写盘上限）----
        // 同样的文件，服务端不限速，看两边写盘路径的纯开销差异
        await RunCaseAsync(tfm, "本地回环不限速 · 16MB / 8 线程", sizeMB: 16, threads: 8,
            serverDelayMs: 0, rounds: 3).ConfigureAwait(false);

        // ---- 场景 3：单线程（nolock 竞争最小，最能体现单次调用开销）----
        await RunCaseAsync(tfm, "慢速宽带 · 8MB / 1 线程", sizeMB: 8, threads: 1,
            serverDelayMs: 10, rounds: 3).ConfigureAwait(false);

        // ---- 场景 4：锁竞争最坏情况（32 线程 × 256KB 分片 + 分片级校验）----
        // 这一项验证 net48 的 _ioGate 在高并发下既不丢字节也不显著掉速
        int stressFailed = await StressTest.RunAsync(tfm).ConfigureAwait(false);

        // ---- 场景 5：纯 IO 提交开销（排除引擎/HTTP 干扰）----
        // 用来解释「为什么本地回环下 net8 反而慢」：差距在 IO 提交方式本身
        await IoBench.RunAsync().ConfigureAwait(false);

        // ---- 场景 6：纯 HTTP 读流（不写盘）----
        await HttpBench.RunAsync().ConfigureAwait(false);

        // ---- 场景 7：hosts 覆盖（net48 模拟实现的正确性）----
        int hostsFailed = await HostsBypassTest.RunAsync().ConfigureAwait(false);

        return (stressFailed + hostsFailed) == 0 ? 0 : 1;
    }

    private static async Task RunCaseAsync(string tfm, string name, int sizeMB, int threads,
        int serverDelayMs, int rounds)
    {
        Console.WriteLine($"--- {name} ---");

        var speeds = new List<double>();
        var elapsed = new List<double>();

        for (int r = 0; r < rounds; r++)
        {
            using var server = new PacedFileServer(sizeMB * 1024 * 1024, serverDelayMs);
            string dir = Path.Combine(Path.GetTempPath(),
                "yunx_bench_" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dir);

            try
            {
                var provider = new HttpClientProvider(NetworkMode.Direct);
                try
                {
                    var pool = new MirrorPool(Array.Empty<Mirror>(),
                        provider.CreateClient(forDownload: true), provider);
                    var engine = new DownloadEngine(provider, pool);

                    var sw = Stopwatch.StartNew();
                    var result = await engine.DownloadAsync(server.Url, dir, "bench.bin",
                        new DownloadOptions
                        {
                            Threads = threads,
                            VerifyChunks = false,       // 关掉校验，只测传输+落盘
                            PreemptSlowChunks = false,  // 关掉抢占，避免误判干扰
                            StaggerConnect = false,
                        }, null, null, CancellationToken.None).ConfigureAwait(false);
                    sw.Stop();

                    long bytes = new FileInfo(result.FilePath).Length;
                    double seconds = sw.Elapsed.TotalSeconds;
                    speeds.Add(bytes / 1024.0 / 1024.0 / seconds);
                    elapsed.Add(seconds);

                    Console.WriteLine($"  第 {r + 1} 轮: {seconds:F2}s  " +
                                      $"{bytes / 1024.0 / 1024.0 / seconds:F2} MB/s  " +
                                      $"({result.ChunkCount} 分片)");
                }
                finally
                {
                    provider.Dispose();
                }
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        // 取中位数：避免 GC / 系统抖动拉偏
        speeds.Sort();
        elapsed.Sort();
        double medSpeed = speeds[speeds.Count / 2];
        double medTime = elapsed[elapsed.Count / 2];

        Console.WriteLine($"  >> 中位: {medTime:F2}s  {medSpeed:F2} MB/s");
        Console.WriteLine();
    }
}

/// <summary>
/// 按固定节奏吐数据的本地 HTTP 服务，用来模拟真实宽带而非本地回环峰值。
/// </summary>
internal sealed class PacedFileServer : IDisposable
{
    private const int PacketSize = 16 * 1024;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _accept;
    private readonly int _delayMs;
    private readonly byte[] _payload;

    public string Url => $"http://127.0.0.1:{Port}/file.bin";
    private int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public PacedFileServer(int payloadSize, int delayMsPerPacket)
    {
        _delayMs = delayMsPerPacket;
        _payload = new byte[payloadSize];
        new Random(20261023).NextBytes(_payload);

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        _accept = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
#if NET48
                client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
#else
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
#endif
            }
            catch { return; }

            _ = Task.Run(() => HandleAsync(client), CancellationToken.None);
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
                using var reader = new StreamReader(ns, Encoding.ASCII, false, 4096,
                    leaveOpen: true);

                string? requestLine = await reader.ReadLineAsync().ConfigureAwait(false);
                if (string.IsNullOrEmpty(requestLine)) return;

                long rangeStart = 0;
                long rangeEnd = _payload.Length - 1;
                bool hasRange = false;

                while (true)
                {
                    string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null || line.Length == 0) break;
                    if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                    {
                        // Range: bytes=start-end
                        string v = line.Substring(6).Trim();
                        int eq = v.IndexOf('=');
                        if (eq >= 0) v = v.Substring(eq + 1);
                        int dash = v.IndexOf('-');
                        if (dash > 0
                            && long.TryParse(v.Substring(0, dash), out long s)
                            && long.TryParse(v.Substring(dash + 1), out long e))
                        {
                            rangeStart = s;
                            rangeEnd = Math.Min(e, _payload.Length - 1);
                            hasRange = true;
                        }
                    }
                }

                long len = rangeEnd - rangeStart + 1;

                var header = new StringBuilder();
                if (hasRange)
                {
                    header.Append("HTTP/1.1 206 Partial Content\r\n");
                    header.Append($"Content-Range: bytes {rangeStart}-{rangeEnd}/{_payload.Length}\r\n");
                }
                else
                {
                    header.Append("HTTP/1.1 200 OK\r\n");
                }
                header.Append("Content-Type: application/octet-stream\r\n");
                header.Append($"Content-Length: {len}\r\n");
                header.Append("Accept-Ranges: bytes\r\n");
                header.Append($"ETag: \"bench\"\r\n");
                header.Append("Connection: keep-alive\r\n");
                header.Append("\r\n");

                byte[] head = Encoding.ASCII.GetBytes(header.ToString());
                await ns.WriteAsync(head, 0, head.Length, _cts.Token).ConfigureAwait(false);

                // 分片吐数据，每包之间 sleep，把有效带宽压到真实宽带水平
                long sent = 0;
                while (sent < len)
                {
                    int n = (int)Math.Min(PacketSize, len - sent);
                    await ns.WriteAsync(_payload, (int)(rangeStart + sent), n, _cts.Token)
                        .ConfigureAwait(false);
                    sent += n;

                    if (_delayMs > 0)
                        await Task.Delay(_delayMs, _cts.Token).ConfigureAwait(false);
                }

                await ns.FlushAsync(_cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // 客户端提前断开（抢占/取消）属正常
            }
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
