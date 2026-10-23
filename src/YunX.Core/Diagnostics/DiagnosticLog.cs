using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;

namespace YunX.Core.Diagnostics;

/// <summary>诊断模块（对应原项目 6 个模块分文件）。</summary>
public enum DiagModule
{
    Db,
    Crypto,
    Download,
    Webview,
    Network,
    Operation,
}

/// <summary>
/// 诊断日志。
///
/// <para><b>设计要点</b>（这套设计来自原项目，是它工程质量最高的部分，值得完整移植）：</para>
/// <list type="number">
///   <item><b>调用点零开销</b>：关闭时只做一次 bool 判断，什么都不写；</item>
///   <item><b>不阻塞调用线程</b>：只做一次 <c>TryAdd</c> 入队，实际写盘在独立守护线程；</item>
///   <item><b>全局限流 300 行/秒</b>：超出的直接丢弃并计数，防止日志本身成为性能瓶颈；</item>
///   <item><b>有界队列（2000）</b>：队列满就丢，绝不因为写日志而拖慢下载；</item>
///   <item><b>按模块分文件 + 自动轮转</b>：单文件 2MB、每模块 5 份；</item>
///   <item><b>敏感信息脱敏</b>：URL 只留 scheme://host，凭证只记用途与长度。</item>
/// </list>
/// </summary>
public sealed class DiagnosticLog : IDisposable
{
    private const int MaxLinesPerSecond = 300;
    private const int QueueCapacity = 2000;
    private const long MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxFilesPerModule = 5;

    private readonly string _directory;
    private readonly BlockingCollection<(DiagModule Module, string Line)> _queue =
        new(new ConcurrentQueue<(DiagModule, string)>(), QueueCapacity);

    private readonly Thread _writer;
    private readonly CancellationTokenSource _cts = new();
    private readonly ConcurrentDictionary<DiagModule, long> _dropped = new();
    private long _windowStartTicks = Stopwatch.GetTimestamp();
    private int _windowCount;

    private volatile bool _enabled;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            if (!value)
            {
                // 关闭时清空队列（原项目语义：关闭诊断即清空，且不再写一行）
                while (_queue.TryTake(out _)) { }
            }
        }
    }

    public DiagnosticLog(string baseDirectory)
    {
        _directory = Path.Combine(baseDirectory, "diagnostic_logs");
        Directory.CreateDirectory(_directory);

        _writer = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "DiagLogWriter",
            Priority = ThreadPriority.BelowNormal,
        };
        _writer.Start();
    }

    /// <summary>
    /// 写一行诊断日志。
    /// <b>这是热路径方法</b>：关闭时只有一个 bool 判断，绝不能有其它开销。
    /// </summary>
    public void Write(DiagModule module, string message)
    {
        if (!_enabled) return;

        // 全局限流：滑动窗口 1 秒
        long now = Stopwatch.GetTimestamp();
        double elapsed = (now - _windowStartTicks) / (double)Stopwatch.Frequency;
        if (elapsed >= 1.0)
        {
            _windowStartTicks = now;
            _windowCount = 0;
        }
        if (++_windowCount > MaxLinesPerSecond)
        {
            _dropped.AddOrUpdate(module, 1, (_, v) => v + 1);
            return;
        }

        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";
        // 队列满直接丢弃，不阻塞调用线程
        _queue.TryAdd((module, line));
    }

    /// <summary>写一条记录，带上脱敏后的 URL。</summary>
    public void WriteUrl(DiagModule module, string action, string url, string? extra = null)
    {
        if (!_enabled) return;
        string redacted = LogRedactor.Url(url);
        Write(module, extra == null ? $"{action} {redacted}" : $"{action} {redacted} | {extra}");
    }

    private void WriteLoop()
    {
        try
        {
            foreach (var (module, line) in _queue.GetConsumingEnumerable(_cts.Token))
            {
                try
                {
                    AppendLine(module, line);
                }
                catch
                {
                    // 写盘失败不能中断守护线程
                }
            }
        }
        catch (OperationCanceledException) { /* 正常退出 */ }
        catch (ObjectDisposedException) { /* Dispose 抢先释放了队列 */ }
        catch
        {
            // 手工 Thread 上的未捕获异常会直接终止进程（.NET 2.0+ 的默认策略），
            // 日志线程绝不能把进程带走。
        }
    }

    private void AppendLine(DiagModule module, string line)
    {
        string path = CurrentFile(module);

        // 轮转：超过上限就换新文件
        var fi = new FileInfo(path);
        if (fi.Exists && fi.Length > MaxFileBytes) Rotate(module, path);

        File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
    }

    private string CurrentFile(DiagModule module) =>
        Path.Combine(_directory, $"{ModuleName(module)}.log");

    private void Rotate(DiagModule module, string current)
    {
        for (int i = MaxFilesPerModule - 1; i >= 1; i--)
        {
            string from = $"{current}.{i}";
            string to = $"{current}.{i + 1}";
            if (!File.Exists(from)) continue;
            try
            {
                if (File.Exists(to)) File.Delete(to);
                File.Move(from, to);
            }
            catch { /* ignore */ }
        }
        try
        {
            string first = current + ".1";
            if (File.Exists(first)) File.Delete(first);
            File.Move(current, first);
        }
        catch { /* ignore */ }
    }

    private static string ModuleName(DiagModule m) => m switch
    {
        DiagModule.Db => "db",
        DiagModule.Crypto => "crypto",
        DiagModule.Download => "download",
        DiagModule.Webview => "webview",
        DiagModule.Network => "network",
        DiagModule.Operation => "operation",
        _ => "other",
    };

    /// <summary>
    /// 打包导出（先 flush 再压 zip）。
    /// </summary>
    public async Task<string?> ExportZipAsync(string outputDirectory,
        CancellationToken ct = default)
    {
        // flush：入队可能还有积压，等它落盘（最多等 1 秒）
        var deadline = DateTime.UtcNow.AddSeconds(1);
        while (_queue.Count > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50, ct).ConfigureAwait(false);

        var files = Directory.GetFiles(_directory, "*.log*");
        if (files.Length == 0) return null;

        Directory.CreateDirectory(outputDirectory);
        string zipPath = Path.Combine(outputDirectory,
            $"yunx_diagnostic_logs_{DateTime.Now:yyyyMMdd_HHmmss}.zip");

        await Task.Run(() =>
        {
            using var zip = System.IO.Compression.ZipFile.Open(zipPath,
                System.IO.Compression.ZipArchiveMode.Create);
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                var entry = zip.CreateEntry(Path.GetFileName(f),
                    System.IO.Compression.CompressionLevel.Optimal);
                using var es = entry.Open();
                using var fs = File.OpenRead(f);
                fs.CopyTo(es);
            }
        }, ct).ConfigureAwait(false);

        return zipPath;
    }

    /// <summary>清空全部诊断日志（关闭开关时的二次确认后调用）。</summary>
    public void ClearAll()
    {
        while (_queue.TryTake(out _)) { }
        foreach (var f in Directory.GetFiles(_directory, "*.log*"))
        {
            try { File.Delete(f); } catch { /* ignore */ }
        }
        _dropped.Clear();
    }

    /// <summary>被限流丢弃的行数统计（导出时附在头部）。</summary>
    public IReadOnlyDictionary<DiagModule, long> DroppedCounts => _dropped;

    public string Summary()
    {
        if (_dropped.IsEmpty) return "";
        var sb = new StringBuilder("（限流丢弃统计：");
        foreach (var (m, n) in _dropped)
            sb.Append($"{ModuleName(m)}={n} ");
        sb.Append('）');
        return sb.ToString();
    }

    private int _disposed;

    /// <summary>
    /// 收尾：先关写入口，再让写线程<b>把队列排空</b>（这就是「flush」），最后才释放队列。
    ///
    /// <para>原来的写法是 <c>Cancel() + Join(2s) + Dispose()</c>：Join 超时后仍无条件 Dispose，
    /// 而写线程可能正卡在磁盘上，随后撞上已释放的队列 → <c>ObjectDisposedException</c>
    /// 从手工 <see cref="Thread"/> 逃出 → 进程被终止。这里改成「排空 + 超时就不释放」。</para>
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // 关掉入口：之后 Write 只做一次 bool 判断，不再碰队列
        _enabled = false;

        // CompleteAdding（而不是 Cancel）让写线程把积压写完再退出
        try { _queue.CompleteAdding(); } catch (ObjectDisposedException) { }

        if (!_writer.Join(TimeSpan.FromSeconds(2)))
        {
            // 还在写（磁盘慢 / 积压多）：取消它，但**不释放**队列与 CTS，
            // 否则正在运行的写线程会撞上已释放对象。对象随进程退出由 GC 回收。
            try { _cts.Cancel(); } catch { /* ignore */ }
            return;
        }

        try { _cts.Dispose(); } catch { /* ignore */ }
        try { _queue.Dispose(); } catch { /* ignore */ }
    }
}

/// <summary>
/// 日志脱敏。
///
/// <para>诊断日志最容易出问题的地方就是「把 Token 写进日志」。
/// 这里统一处理：URL 只留 scheme://host，敏感头只记长度。</para>
/// </summary>
public static class LogRedactor
{
    private static readonly string[] SensitiveKeys =
    {
        "cookie", "authorization", "token", "auth", "password", "passwd",
        "secret", "key", "sign", "session", "bduss", "stoken",
    };

    /// <summary>URL 脱敏：只保留 scheme://host:port/path，丢弃查询串（可能含 Token）。</summary>
    public static string Url(string url)
    {
        if (string.IsNullOrEmpty(url)) return "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "<invalid>";

        // 有的直链把签名放在 path 里：只保留前两段
        var segs = u.AbsolutePath.Trim('/').Split('/');
        string path = segs.Length <= 2 ? u.AbsolutePath : "/" + string.Join("/", segs.Take(2)) + "/…";

        return $"{u.Scheme}://{u.Host}{(u.IsDefaultPort ? "" : ":" + u.Port)}{path}";
    }

    /// <summary>敏感值只记长度与前后各 2 字符。</summary>
    public static string Secret(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "<empty>";
        if (value.Length <= 8) return $"<len={value.Length}>";
        return $"{value[..2]}…{value[^2..]}(len={value.Length})";
    }

    /// <summary>把请求头字典转成可安全打印的文本。</summary>
    public static string Headers(IReadOnlyDictionary<string, string> headers)
    {
        if (headers.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var (k, v) in headers)
        {
            bool sensitive = SensitiveKeys.Any(s => k.Contains(s, StringComparison.OrdinalIgnoreCase));
            sb.Append(k).Append('=').Append(sensitive ? Secret(v) : v).Append("; ");
        }
        return sb.ToString().TrimEnd(' ', ';');
    }

    /// <summary>
    /// 请求体 / 响应体截断：只在非 200/206 或异常时记录，各截 1.5KB。
    /// </summary>
    public static string Body(string? body, int maxBytes = 1536)
    {
        if (string.IsNullOrEmpty(body)) return "";
        if (body.Length <= maxBytes) return body;
        return body[..maxBytes] + $"…(截断，原长 {body.Length})";
    }
}
