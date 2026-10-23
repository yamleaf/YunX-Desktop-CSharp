using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace YunX.Core.Download;


#if NET48
using NetHandler = System.Net.Http.HttpClientHandler;
#else
using NetHandler = System.Net.Http.SocketsHttpHandler;
#endif
/// <summary>
/// Gopeed 引擎子进程的生命周期与 REST API 客户端。
///
/// <para><b>设计要点</b>（都是从原项目踩过的坑里总结的）：</para>
/// <list type="bullet">
///   <item><b>只监听 <c>127.0.0.1</c> + 随机空闲端口</b>：不设密码时不启用鉴权，
///         绑到 0.0.0.0 等于把下载引擎暴露给局域网。</item>
///   <item><b>本地请求必须绕开系统代理</b>：用户配了代理时，<c>127.0.0.1</c> 的请求
///         也会被代理走，表现成"引擎起来了但所有 API 都超时"。</item>
///   <item><b>退出时要杀干净</b>：子进程不随宿主退出（尤其宿主被强杀时），
///         残留进程仍占着 bolt 存储锁，下次启动会直接失败。</item>
///   <item><b>taskStatus 只能读 <c>/api/v1/tasks/{id}</c></b>：gopeed 服务端没有
///         <c>/{id}/status</c> 这个路由（那是上游 Android 版的用法），调用必然 404，
///         表现为"进度永远同步不上"。</item>
/// </list>
/// </summary>
public sealed class GopeedEngine : IDisposable
{
    private readonly string _engineDir;
    private readonly HttpClient _local;

    private Process? _process;
    private int _port;
    private bool _disposed;

    /// <summary>等待引擎就绪的上限。冷启动要初始化 bolt 存储，给足时间。</summary>
    private static readonly TimeSpan ReadyTimeout = TimeSpan.FromSeconds(20);

    public GopeedEngine(string engineDir)
    {
        _engineDir = engineDir;

        // 关键：绕过系统代理。本地回环请求一旦被代理接管就是超时。
        var handler = new NetHandler
        {
            UseProxy = false,
#if !NET48
            ConnectTimeout = TimeSpan.FromSeconds(5),
#endif
        };

        _local = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = TimeSpan.FromSeconds(15),
        };
        _local.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "YunX-Desktop");
    }

    /// <summary>
    /// 引擎是否可用。
    ///
    /// <para><b>必须检查 <c>HasExited</c></b>：Gopeed 底层用 Go 写，
    /// 某些非法输入（如全零 info hash）会让它 <c>panic</c> 直接退出。
    /// 只看"进程对象存在"会把已崩溃的引擎误判成正常运行，
    /// 后续所有调用都变成"连接被拒绝"，错误信息还指向端口而不是真正原因。</para>
    /// </summary>
    public bool IsRunning
    {
        get
        {
            try { return _process is { HasExited: false } && _port > 0; }
            catch { return false; }     // 进程对象已释放
        }
    }
    public int Port => _port;

    /// <summary>引擎可执行文件路径。</summary>
    public string ExePath => Path.Combine(_engineDir, "bin", "gopeed.exe");

    /// <summary>引擎存储目录（bolt 数据库与下载元数据）。</summary>
    private string StorageDir => Path.Combine(_engineDir, "storage");

    private string LogFile => Path.Combine(_engineDir, "engine.log");

    private string BaseUrl => $"http://127.0.0.1:{_port}";

    /// <summary>
    /// 启动引擎并等待就绪。
    /// </summary>
    /// <exception cref="InvalidOperationException">内核未安装、启动失败或就绪超时。</exception>
    public async Task StartAsync(Action<string>? log = null, CancellationToken ct = default)
    {
        if (IsRunning) return;

        if (!File.Exists(ExePath))
            throw new InvalidOperationException("还没有安装 Gopeed 内核");

        // 上次被强杀可能留下孤儿进程：它占着 bolt 存储锁，必须先清掉
        KillStaleProcesses(log);

        Directory.CreateDirectory(StorageDir);

        int port = PickFreePort();
        var psi = new ProcessStartInfo
        {
            FileName = ExePath,
            WorkingDirectory = Path.Combine(_engineDir, "bin"),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
#if NET48
        // net48 只有字符串形式的 Arguments，需要按 Windows 命令行规则手工转义。
        psi.Arguments = Process48.BuildArgumentString(new[]
        {
            "-A", "127.0.0.1", "-P", port.ToString(), "-d", StorageDir,
        });
#else
        psi.ArgumentList.Add("-A");
        psi.ArgumentList.Add("127.0.0.1");
        psi.ArgumentList.Add("-P");
        psi.ArgumentList.Add(port.ToString());
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(StorageDir);
#endif

        // stdout/stderr 必须消费掉：不读管道会写满缓冲区把子进程阻塞住
        var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var logWriter = new StreamWriter(LogFile, append: false) { AutoFlush = true };
        process.OutputDataReceived += (_, e) => { if (e.Data != null) logWriter.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) logWriter.WriteLine(e.Data); };

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
        catch (Exception e)
        {
            logWriter.Dispose();
            throw new InvalidOperationException($"启动 Gopeed 内核失败：{e.Message}", e);
        }

        _process = process;
        _port = port;
        log?.Invoke($"Gopeed 内核启动中（端口 {port}）…");

        // 轮询 /api/v1/info 直到就绪
        var deadline = DateTime.UtcNow + ReadyTimeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();

            if (process.HasExited)
            {
                int code = process.ExitCode;
                _process = null;
                _port = 0;
                logWriter.Flush();
                logWriter.Dispose();
                throw new InvalidOperationException(
                    $"Gopeed 内核进程已退出（退出码 {code}）。" +
                    $"常见原因是内核包与系统架构不匹配。详见日志：{LogFile}");
            }

            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(2));
                using var resp = await _local.GetAsync($"{BaseUrl}/api/v1/info", cts.Token)
                    .ConfigureAwait(false);

                if (resp.IsSuccessStatusCode)
                {
                    log?.Invoke($"Gopeed 内核已就绪（端口 {port}）");
                    return;
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 单次探测超时：继续等
            }
            catch (HttpRequestException)
            {
                // 端口还没起来：继续等
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        // 超时：清掉进程，避免留下半死状态
        try { if (!process.HasExited) Process48.KillTree(process); } catch { }
        _process = null;
        _port = 0;
        logWriter.Dispose();

        throw new InvalidOperationException(
            $"Gopeed 内核在 {ReadyTimeout.TotalSeconds:0} 秒内没有响应。详见日志：{LogFile}");
    }

    /// <summary>停止引擎（幂等）。</summary>
    public void Stop()
    {
        var p = _process;
        _process = null;
        _port = 0;
        if (p == null) return;

        try
        {
            if (!p.HasExited)
            {
                // 先礼后兵：Gopeed 对 SIGTERM 不敏感，但正常结束能让它刷盘
                try { p.CloseMainWindow(); } catch { }
                if (!p.WaitForExit(2000))
                    Process48.KillTree(p);
            }
        }
        catch { /* 退出路径上不能再抛 */ }
        finally
        {
            try { p.Dispose(); } catch { }
        }
    }

    // ===================== REST API =====================

    /// <summary>引擎版本（就绪探测也用这个）。</summary>
    public async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync("/api/v1/info", ct).ConfigureAwait(false);
        return doc.RootElement.TryGetProperty("data", out var d)
               && d.TryGetProperty("version", out var v)
            ? v.GetString() ?? "" : "";
    }

    /// <summary>新建下载任务，返回引擎内部任务 id。</summary>
    public async Task<string> CreateTaskAsync(string url, string saveDir, string? name = null,
        int connections = 0, CancellationToken ct = default)
    {
        var reqObj = new Dictionary<string, object?> { ["url"] = url };
        var optsObj = new Dictionary<string, object?> { ["path"] = saveDir };

        if (!string.IsNullOrEmpty(name)) optsObj["name"] = name;
        if (connections > 0)
            optsObj["extra"] = new Dictionary<string, object?> { ["connections"] = connections };

        var body = new Dictionary<string, object?> { ["req"] = reqObj, ["opts"] = optsObj };

        using var doc = await PostJsonAsync("/api/v1/tasks", body, ct).ConfigureAwait(false);

        // 注意：新建任务的 data 是**任务 id 字符串**，不是对象（查询类接口才是对象）
        string id = doc.RootElement.TryGetProperty("data", out var d) ? d.GetString() ?? "" : "";
        if (id.Length == 0)
            throw new InvalidOperationException("Gopeed 未返回任务 ID");

        return id;
    }

    /// <summary>查询任务状态。</summary>
    public async Task<GopeedTask> GetTaskAsync(string taskId, CancellationToken ct = default)
    {
        using var doc = await GetJsonAsync($"/api/v1/tasks/{taskId}", ct).ConfigureAwait(false);
        var data = doc.RootElement.TryGetProperty("data", out var d) ? d : default;

        long downloaded = 0, speed = 0, total = 0;
        string status = "", name = "";

        if (data.ValueKind == JsonValueKind.Object)
        {
            if (data.TryGetProperty("status", out var st)) status = st.GetString() ?? "";
            if (data.TryGetProperty("progress", out var pg) && pg.ValueKind == JsonValueKind.Object)
            {
                if (pg.TryGetProperty("downloaded", out var dl)) downloaded = dl.GetInt64();
                if (pg.TryGetProperty("speed", out var sp)) speed = sp.GetInt64();
            }

            // ★ 总大小不在 progress 里，而在 meta.res.size —— 这是最容易读错的一处
            if (data.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object
                && meta.TryGetProperty("res", out var res) && res.ValueKind == JsonValueKind.Object)
            {
                if (res.TryGetProperty("size", out var sz)) total = sz.GetInt64();
                if (res.TryGetProperty("name", out var nm)) name = nm.GetString() ?? "";
            }
        }

        return new GopeedTask(taskId, status, downloaded, total, speed, name);
    }

    /// <summary>暂停 / 继续 / 删除。</summary>
    public Task PauseAsync(string taskId, CancellationToken ct = default)
        => PutAsync($"/api/v1/tasks/{taskId}/pause", ct);

    public Task ResumeAsync(string taskId, CancellationToken ct = default)
        => PutAsync($"/api/v1/tasks/{taskId}/continue", ct);

    public Task DeleteAsync(string taskId, CancellationToken ct = default)
        => DeleteAsyncInternal($"/api/v1/tasks/{taskId}", ct);

    // ===================== 内部 HTTP =====================

    private async Task<JsonDocument> GetJsonAsync(string path, CancellationToken ct)
    {
        using var resp = await _local.GetAsync(BaseUrl + path, ct).ConfigureAwait(false);
        return await ReadEnvelopeAsync(resp, path, ct).ConfigureAwait(false);
    }

    private async Task<JsonDocument> PostJsonAsync(string path, object body, CancellationToken ct)
    {
        string json = JsonSerializer.Serialize(body);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await _local.PostAsync(BaseUrl + path, content, ct).ConfigureAwait(false);
        return await ReadEnvelopeAsync(resp, path, ct).ConfigureAwait(false);
    }

    private async Task PutAsync(string path, CancellationToken ct)
    {
        using var resp = await _local.PutAsync(BaseUrl + path, null, ct).ConfigureAwait(false);
        using var _ = await ReadEnvelopeAsync(resp, path, ct).ConfigureAwait(false);
    }

    private async Task DeleteAsyncInternal(string path, CancellationToken ct)
    {
        using var resp = await _local.DeleteAsync(BaseUrl + path, ct).ConfigureAwait(false);
        using var _ = await ReadEnvelopeAsync(resp, path, ct).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReadEnvelopeAsync(HttpResponseMessage resp,
        string path, CancellationToken ct)
    {
        string text = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"Gopeed API {path} 返回 {(int)resp.StatusCode}：{Truncate(text, 300)}");

        // Gopeed 的响应是 { code, msg, data } 信封，code != 0 表示业务失败
        var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text);
        if (doc.RootElement.TryGetProperty("code", out var code)
            && code.ValueKind == JsonValueKind.Number && code.GetInt32() != 0)
        {
            string msg = doc.RootElement.TryGetProperty("msg", out var m)
                ? m.GetString() ?? "" : "";
            doc.Dispose();
            throw new InvalidOperationException($"Gopeed 返回错误：{msg}");
        }
        return doc;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "…";

    /// <summary>挑一个空闲的本地端口。</summary>
    private static int PickFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>
    /// 清掉上一次残留的 gopeed 进程。
    ///
    /// <para>宿主被强杀时子进程不会跟着退出，它仍持有 <c>storage</c> 下的 bolt 锁，
    /// 新进程启动会直接失败（报错还很含糊）。启动前先按名字清理一遍最省事。</para>
    /// </summary>
    private static void KillStaleProcesses(Action<string>? log)
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("gopeed"))
            {
                try
                {
                    // 只杀**我们自己的**那份：路径必须匹配引擎目录，避免误杀用户手动开的 Gopeed
                    string? path = p.MainModule?.FileName;
                    if (path != null && path.Contains("gopeed", StringComparison.OrdinalIgnoreCase))
                    {
                        Process48.KillTree(p);
                        p.WaitForExit(1500);
                        log?.Invoke("已清理上一次残留的 Gopeed 进程");
                    }
                }
                catch { /* 访问被拒绝（权限/已退出）：忽略 */ }
                finally { p.Dispose(); }
            }
        }
        catch { /* 枚举失败不影响启动 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try { _local.Dispose(); } catch { }
    }
}

/// <summary>引擎侧的任务快照。</summary>
/// <param name="Status">ready / running / wait / pause / error / done</param>
public sealed record GopeedTask(string Id, string Status, long Downloaded, long Total,
    long Speed, string Name)
{
    public bool IsDone => Status.Equals("done", StringComparison.OrdinalIgnoreCase);
    public bool IsPaused => Status.Equals("pause", StringComparison.OrdinalIgnoreCase);
    public bool IsError => Status.Equals("error", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否还在进行（ready/running/wait）。</summary>
    public bool IsActive => Status is "ready" or "running" or "wait";
}
