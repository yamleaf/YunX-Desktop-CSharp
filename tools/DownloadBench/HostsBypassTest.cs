using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using YunX.Core.Net;

/// <summary>
/// 验证 net48 的 hosts 覆盖是否真的生效。
///
/// <para>net8 靠 <c>ConnectCallback</c>，net48 靠「URI 换 IP + 补 Host 头 + 按原域名校验证书」。
/// 后者是模拟实现，必须验证三件事：</para>
///
/// <list type="number">
/// <item>改写确实发生了（URI 变 IP、Host 头保持原域名）</item>
/// <item>服务器收到的 Host 头是原域名（不是 IP）—— 否则虚拟主机/CDN 会 404</item>
/// <item>证书域名匹配逻辑正确（含通配符）</item>
/// </list>
/// </summary>
internal static class HostsBypassTest
{
    public static async Task<int> RunAsync()
    {
        Console.WriteLine("============================================================");
        Console.WriteLine("  hosts 覆盖验证（net48 模拟实现）");
        Console.WriteLine("============================================================");
        Console.WriteLine();

        int failed = 0;

        // ---- 1. URI 改写 + Host 头 ----
        failed += TestRewrite();

        // ---- 2. 真实请求：验证服务器拿到的 Host 头 ----
        failed += await TestRealRequestAsync().ConfigureAwait(false);

        // ---- 3. 证书域名匹配 ----
        failed += TestCertNameMatching();

        Console.WriteLine();
        Console.WriteLine(failed == 0 ? "全部通过" : $"失败 {failed} 项");
        return failed;
    }

    private static int TestRewrite()
    {
        Console.WriteLine("--- 1. URI 改写 + Host 头 ---");
        int failed = 0;

#if NET48
        // 注入一条覆盖：github.com -> 127.0.0.1
        SetOverrides(new Dictionary<string, IPAddress> { ["github.com"] = IPAddress.Loopback });

        using var req = new System.Net.Http.HttpRequestMessage(
            System.Net.Http.HttpMethod.Get, "https://github.com/owner/repo/releases/download/v1/a.zip");

        bool rewritten = HostsRewrite48.Apply(req);
        Check(ref failed, "改写发生", rewritten);
        Check(ref failed, "URI host 变成 IP", req.RequestUri!.Host == "127.0.0.1",
            req.RequestUri.Host);
        Check(ref failed, "Host 头保持原域名", req.Headers.Host == "github.com",
            req.Headers.Host ?? "(null)");
        Check(ref failed, "路径保持", req.RequestUri.AbsolutePath
            == "/owner/repo/releases/download/v1/a.zip", req.RequestUri.AbsolutePath);

        // 不在覆盖表里的 host 不改写
        using var req2 = new System.Net.Http.HttpRequestMessage(
            System.Net.Http.HttpMethod.Get, "https://example.com/a.zip");
        Check(ref failed, "未覆盖的 host 不改写", !HostsRewrite48.Apply(req2));

        SetOverrides(new Dictionary<string, IPAddress>());
#else
        Console.WriteLine("  (net48 专用，net8 走 ConnectCallback，此处跳过)");
#endif
        Console.WriteLine();
        return failed;
    }

    private static async Task<int> TestRealRequestAsync()
    {
        Console.WriteLine("--- 2. 真实请求：服务器收到的 Host 头 ---");
        int failed = 0;

#if NET48
        using var server = new HostEchoServer();
        SetOverrides(new Dictionary<string, IPAddress> { ["fake-host.test"] = IPAddress.Loopback });

        try
        {
            var provider = new HttpClientProvider(NetworkMode.Direct);
            try
            {
                using var client = provider.CreateClient(forDownload: true);
                using var req = new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Get,
                    $"http://fake-host.test:{server.Port}/probe");

                bool rewritten = HostsRewrite48.Apply(req);
                Check(ref failed, "改写发生", rewritten);

                using var resp = await client.SendAsync(req).ConfigureAwait(false);
                Check(ref failed, "请求成功", resp.IsSuccessStatusCode,
                    ((int)resp.StatusCode).ToString());

                string seenHost = server.LastHostHeader ?? "";
                Check(ref failed, "服务器收到原域名（不是 IP）",
                    seenHost.StartsWith("fake-host.test", StringComparison.OrdinalIgnoreCase),
                    seenHost.Length > 0 ? seenHost : "(空)");
            }
            finally { provider.Dispose(); }
        }
        finally
        {
            SetOverrides(new Dictionary<string, IPAddress>());
        }
#else
        Console.WriteLine("  (net48 专用，跳过)");
#endif
        Console.WriteLine();
        return failed;
    }

    private static int TestCertNameMatching()
    {
        Console.WriteLine("--- 3. 证书域名匹配 ---");
        int failed = 0;

#if NET48
        // 直接测 NameMatches 的语义（反射拿私有方法，避免为测试扩大可见性）
        var t = typeof(HostsOverride48);
        var m = t.GetMethod("NameMatches", BindingFlags.NonPublic | BindingFlags.Static);
        if (m == null)
        {
            Check(ref failed, "找到 NameMatches", false);
            Console.WriteLine();
            return failed;
        }

        bool Match(string pattern, string host) => (bool)m.Invoke(null, new object[] { pattern, host })!;

        Check(ref failed, "精确匹配", Match("github.com", "github.com"));
        Check(ref failed, "大小写不敏感", Match("GitHub.com", "github.com"));
        Check(ref failed, "不匹配别的域", !Match("github.com", "evil.com"));
        Check(ref failed, "通配匹配一级", Match("*.github.com", "api.github.com"));
        Check(ref failed, "通配不匹配多级", !Match("*.github.com", "a.b.github.com"));
        Check(ref failed, "通配不匹配裸域", !Match("*.github.com", "github.com"));
        Check(ref failed, "通配不越界匹配",
            !Match("*.github.com", "notgithub.com"));
#else
        Console.WriteLine("  (net48 专用，跳过)");
#endif
        Console.WriteLine();
        return failed;
    }


    /// <summary>
    /// 注入覆盖表。Overrides 是 <c>IReadOnlyDictionary</c> 且有 private setter，
    /// 测试里通过反射替换整个字典 —— 比把它改成可写更安全（生产代码不该有公开写入口）。
    /// </summary>
    private static void SetOverrides(Dictionary<string, IPAddress> map)
    {
        var prop = typeof(HostsBypass).GetProperty(nameof(HostsBypass.Overrides),
            BindingFlags.Public | BindingFlags.Static);
        prop!.SetValue(null, map);
    }

    private static void Check(ref int failed, string name, bool ok, string detail = "")
    {
        Console.WriteLine((ok ? "  [OK]   " : "  [FAIL] ") + name +
                          (detail.Length > 0 ? "  " + detail : ""));
        if (!ok) failed++;
    }
}

/// <summary>记录收到的 Host 头并回 200 的最小 HTTP 服务。</summary>
internal sealed class HostEchoServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _accept;

    public string? LastHostHeader { get; private set; }
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public HostEchoServer()
    {
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
                using var ns = client.GetStream();
                using var reader = new StreamReader(ns, Encoding.ASCII, false, 4096, leaveOpen: true);

                if (string.IsNullOrEmpty(await reader.ReadLineAsync().ConfigureAwait(false))) return;
                while (true)
                {
                    string? line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null || line.Length == 0) break;
                    if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                        LastHostHeader = line.Substring(5).Trim();
                }

                byte[] resp = Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok");
                await ns.WriteAsync(resp, 0, resp.Length, _cts.Token).ConfigureAwait(false);
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
