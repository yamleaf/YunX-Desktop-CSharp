using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// 纯 HTTP 读流开销对比：不写盘，只把响应体读完。
///
/// <para>目的是把「下载慢」拆成两半：网络读 vs 写盘。
/// 上一轮 IoBench 已证明写盘两边都远超需求（449/1024 vs 549/1172 MB/s），
/// 所以如果这里出现数倍差距，问题就在 HTTP 层。</para>
/// </summary>
internal static class HttpBench
{
    private const int PayloadSize = 16 * 1024 * 1024;   // 16MB
    private const int ReadBuffer = 96 * 1024;

    public static async Task RunAsync()
    {
        Console.WriteLine("============================================================");
        Console.WriteLine("  HTTP 读流开销对比（不写盘）");
        Console.WriteLine("============================================================");

        using var server = new PlainServer(PayloadSize);

#if NET48
        // net48 只有 HttpClientHandler
        using var handler = new HttpClientHandler { UseProxy = false, UseCookies = false };
        string label = "HttpClientHandler (net48)";
#else
        using var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxConnectionsPerServer = 64,
        };
        string label = "SocketsHttpHandler (net8)";
#endif
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "YunX-Bench/1.0");

        // ---- A. 单流整段读 ----
        var times = new double[3];
        for (int r = 0; r < 3; r++)
        {
            var sw = Stopwatch.StartNew();
            using (var resp = await client.GetAsync(server.Url,
                       HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                var buffer = new byte[ReadBuffer];
                long total = 0;
                int n;
                while ((n = await stream.ReadAsync(buffer, 0, buffer.Length)
                           .ConfigureAwait(false)) > 0)
                {
                    total += n;
                }
                if (total != PayloadSize)
                    Console.WriteLine($"  [WARN] 只读到 {total} / {PayloadSize}");
            }
            sw.Stop();
            times[r] = sw.Elapsed.TotalSeconds;
        }
        Array.Sort(times);
        Console.WriteLine($"  A. 单流读 {PayloadSize / 1024 / 1024}MB: " +
                          $"{times[1]:F3}s  {PayloadSize / 1024.0 / 1024.0 / times[1]:F1} MB/s   [{label}]");

        // ---- B. 8 路并发 Range 读（模拟分片下载的 HTTP 部分）----
        times = new double[3];
        for (int r = 0; r < 3; r++)
        {
            var sw = Stopwatch.StartNew();
            int chunk = PayloadSize / 8;
            var tasks = new Task[8];
            for (int i = 0; i < 8; i++)
            {
                int idx = i;
                tasks[i] = Task.Run(async () =>
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, server.Url);
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(
                        (long)idx * chunk, (long)(idx + 1) * chunk - 1);
                    using var resp = await client.SendAsync(req,
                        HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
                    resp.EnsureSuccessStatusCode();
                    using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                    var buffer = new byte[ReadBuffer];
                    long got = 0;
                    int n;
                    while ((n = await stream.ReadAsync(buffer, 0, buffer.Length)
                               .ConfigureAwait(false)) > 0)
                    {
                        got += n;
                    }
                });
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
            sw.Stop();
            times[r] = sw.Elapsed.TotalSeconds;
        }
        Array.Sort(times);
        Console.WriteLine($"  B. 8 路并发 Range 读: " +
                          $"{times[1]:F3}s  {PayloadSize / 1024.0 / 1024.0 / times[1]:F1} MB/s   [{label}]");
        Console.WriteLine();
    }
}

/// <summary>极简 HTTP 服务：不限速、支持 Range。</summary>
internal sealed class PlainServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _accept;
    private readonly byte[] _payload;

    public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/f.bin";

    public PlainServer(int size)
    {
        _payload = new byte[size];
        new Random(7).NextBytes(_payload);
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

                if (string.IsNullOrEmpty(await reader.ReadLineAsync().ConfigureAwait(false))) return;

                long start = 0, end = _payload.Length - 1;
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
                            start = s; end = Math.Min(e, _payload.Length - 1); hasRange = true;
                        }
                    }
                }

                long len = end - start + 1;
                var sb = new StringBuilder();
                sb.Append(hasRange ? "HTTP/1.1 206 Partial Content\r\n" : "HTTP/1.1 200 OK\r\n");
                if (hasRange) sb.Append($"Content-Range: bytes {start}-{end}/{_payload.Length}\r\n");
                sb.Append("Content-Type: application/octet-stream\r\n");
                sb.Append($"Content-Length: {len}\r\n");
                sb.Append("Accept-Ranges: bytes\r\n");
                sb.Append("Connection: keep-alive\r\n\r\n");

                byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
                await ns.WriteAsync(head, 0, head.Length, _cts.Token).ConfigureAwait(false);
                await ns.WriteAsync(_payload, (int)start, (int)len, _cts.Token).ConfigureAwait(false);
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
