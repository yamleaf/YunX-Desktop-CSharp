using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace YunX.Core.Download;

/// <summary>
/// Gopeed 内核（官方 Windows 无界面服务端版）的获取与安装。
///
/// <para><b>为什么走"下载官方内核"而不是自己实现 BT</b>：BT 协议包含 DHT、PEX、
/// tracker 协商、分块校验、peer 调度等一整套东西，自己写既不可能做稳，也没必要。
/// Gopeed 官方发布的 <c>gopeed-web-&lt;版本&gt;-windows-&lt;架构&gt;.zip</c> 是一个
/// 自包含的 HTTP 服务端，提供完整的 REST API —— 直接用它当本地引擎，磁力/种子下载
/// 由它负责，我们只管调度与界面。</para>
///
/// <para><b>代价要说清楚</b>：首次使用需要联网下载约 20~30MB 的内核包，
/// 失败时功能就不可用。所以这里把"获取过程"做成有进度、可取消、可重试的显式步骤，
/// 而不是在后台悄悄失败。</para>
/// </summary>
public sealed class GopeedProvisioner
{
    private const string ReleasesLatestUrl =
        "https://api.github.com/repos/GopeedLab/gopeed/releases/latest";

    private const string AssetPrefix = "gopeed-web-";
    private const string ExeName = "gopeed.exe";

    private readonly HttpClient _http;

    public GopeedProvisioner(HttpClient http) => _http = http;

    /// <summary>获取过程各阶段的进度。</summary>
    public enum Stage { Resolving, Downloading, Extracting, Done }

    /// <summary>一次获取的状态快照。</summary>
    /// <param name="Downloaded">已下载字节（仅 Downloading 有意义）。</param>
    /// <param name="Total">总字节，-1 表示服务端未给长度。</param>
    public sealed record Progress(Stage Stage, long Downloaded, long Total, string Message)
    {
        public double Percent => Total > 0
            ? Math48.Clamp(Downloaded * 100.0 / Total, 0, 100)
            : 0;
    }

    /// <summary>解析出可下载的内核资产。</summary>
    public sealed record KernelAsset(string Tag, string Name, string Url, long Size);

    /// <summary>
    /// 查询最新 Release，挑出与本机架构匹配的 Windows 包。
    ///
    /// <para>架构后缀：x64 → <c>amd64</c>，arm64 → <c>arm64</c>。
    /// 官方包名形如 <c>gopeed-web-v1.9.3-windows-amd64.zip</c>。</para>
    /// </summary>
    public async Task<KernelAsset> ResolveAsync(CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, ReleasesLatestUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", "YunX-Desktop");
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        #if NET48
        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        #else
#if NET48
        using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
#else
        await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
#endif
        #endif
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
            .ConfigureAwait(false);

        var root = doc.RootElement;
        string tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";

        string wanted = $"{AssetPrefix}{tag}-windows-{ArchSuffix()}.zip";

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                string name = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (!name.Equals(wanted, StringComparison.OrdinalIgnoreCase)) continue;

                string url = a.TryGetProperty("browser_download_url", out var u)
                    ? u.GetString() ?? "" : "";
                long size = a.TryGetProperty("size", out var s) ? s.GetInt64() : 0;
                if (url.Length == 0) continue;

                return new KernelAsset(tag, name, url, size);
            }
        }

        throw new InvalidOperationException(
            $"Gopeed 最新 Release（{tag}）里没有找到 {wanted}。" +
            "如果这个项目已改名或有新的打包方式，需要更新资产名匹配规则。");
    }

    /// <summary>本机架构 → Gopeed 官方包名后缀。</summary>
    private static string ArchSuffix() => RuntimeInformation.ProcessArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X64 => "amd64",
        var other => throw new PlatformNotSupportedException(
            $"Gopeed 官方没有为 {other} 提供 Windows 内核包"),
    };

    /// <summary>内核是否已安装（可执行文件存在）。</summary>
    public static bool IsInstalled(string engineDir)
        => File.Exists(Path.Combine(engineDir, "bin", ExeName));

    /// <summary>已安装内核的版本标记文件路径。</summary>
    public static string VersionFile(string engineDir) => Path.Combine(engineDir, "version.txt");

    /// <summary>
    /// 下载并解包内核到 <paramref name="engineDir"/>。
    ///
    /// <para>解包只提取 <c>gopeed.exe</c> 及其同目录的依赖 —— 官方 zip 里既有 web 资源也有
    /// 各平台二进制，全解出来会白占几十 MB。</para>
    ///
    /// <param name="fallbackUrls">
    /// 备用下载地址（镜像前缀拼出来的地址）。<b>必须提供</b>：
    /// <c>github.com</c> 的 release 下载在国内经常直接连不上（API 域名能通不等于下载能通），
    /// 只给直链就等于让这个功能对国内用户不可用。
    ///
    /// <para><b>顺序上是「镜像优先、直连兜底」</b>，不是反过来。原因：直连失败时通常是
    /// <b>卡住不返回</b>（TCP 连不通，等到超时才报错），而不是立刻报错 ——
    /// 先试直连会让用户白白干等几十秒。镜像哪怕慢，至少大概率能连上。</para>
    /// </param>
    /// </summary>
    public async Task InstallAsync(KernelAsset asset, string engineDir,
        IProgress<Progress>? progress = null, CancellationToken ct = default,
        IEnumerable<string>? fallbackUrls = null)
    {
        Directory.CreateDirectory(engineDir);

        string zipPath = Path.Combine(engineDir, asset.Name);

        // 镜像在前、直连最后
        var candidates = new List<string>();
        if (fallbackUrls != null)
            candidates.AddRange(fallbackUrls.Where(u => !string.IsNullOrEmpty(u)));
        candidates.Add(asset.Url);

        // ---- 1. 下载（逐个候选尝试）----
        Exception? lastError = null;
        bool ok = false;

        for (int i = 0; i < candidates.Count && !ok; i++)
        {
            string url = candidates[i];
            bool isDirect = url.Equals(asset.Url, StringComparison.OrdinalIgnoreCase);
            string label = isDirect ? "GitHub 直连" : $"镜像 {i + 1}";

            // 单条线路的超时：镜像可能半死不活，不能无限等。
            // 但也不能太短 —— 38MB 的包在慢线路上需要时间。
            using var lineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lineCts.CancelAfter(TimeSpan.FromSeconds(isDirect ? 25 : 180));

            try
            {
                progress?.Report(new Progress(Stage.Downloading, 0, asset.Size, $"正在下载内核…（{label}）"));

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", "YunX-Desktop");

                using var resp = await _http.SendAsync(req,
                    HttpCompletionOption.ResponseHeadersRead, lineCts.Token).ConfigureAwait(false);
                resp.EnsureSuccessStatusCode();

                long total = resp.Content.Headers.ContentLength ?? asset.Size;

#if NET48
                using var src = await resp.Content.ReadAsStreamAsync(lineCts.Token)
#else
                await using var src = await resp.Content.ReadAsStreamAsync(lineCts.Token)
#endif
                    .ConfigureAwait(false);
#if NET48
                using var dst = new FileStream(zipPath, FileMode.Create, FileAccess.Write,
#else
                await using var dst = new FileStream(zipPath, FileMode.Create, FileAccess.Write,
#endif
                    FileShare.None, 81920, FileOptions.Asynchronous);

                var buffer = new byte[81920];
                long done = 0;
                long lastReport = 0;

                while (true)
                {
                    int read = await src.ReadAsync(buffer, lineCts.Token).ConfigureAwait(false);
                    if (read <= 0) break;

                    await dst.WriteAsync(buffer.AsMemory(0, read), lineCts.Token).ConfigureAwait(false);
                    done += read;

                    // 节流上报：每个 80KB 块都回调会让 UI 线程吃不消
                    if (done - lastReport > 512 * 1024 || done == total)
                    {
                        lastReport = done;
                        progress?.Report(new Progress(Stage.Downloading, done, total,
                            $"正在下载内核…（{label}）"));
                    }
                }

                // 完整性粗检：明显短了说明下到半截（镜像有时会截断）。
                // 不用严格相等 —— 镜像可能做了传输压缩，Content-Length 与原始资产大小不同。
                if (asset.Size > 0 && done < asset.Size * 0.9)
                    throw new IOException($"下载不完整（{done} / 期望约 {asset.Size} 字节）");

                ok = true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                lastError = e;
                try { if (File.Exists(zipPath)) File.Delete(zipPath); } catch { }
                progress?.Report(new Progress(Stage.Downloading, 0, 0,
                    $"线路 {label} 不可用，换下一条…"));
            }
        }

        if (!ok)
            throw new InvalidOperationException(
                "所有线路都下载不了内核。请检查网络，或在设置里启用「绕过 hosts 劫持」后重试。" +
                $"最后一个错误：{lastError?.Message}", lastError);

        // ---- 2. 解包 ----
        progress?.Report(new Progress(Stage.Extracting, 0, 0, "正在解包内核…"));

        string binDir = Path.Combine(engineDir, "bin");
        Directory.CreateDirectory(binDir);

        bool foundExe = false;
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/')) continue;

                string fileName = Path.GetFileName(entry.FullName);

                // 只取二进制与配置：web 静态资源我们用不上（不启 Web UI）
                bool take = fileName.Equals(ExeName, StringComparison.OrdinalIgnoreCase)
                            || fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                            || fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                            || fileName.EndsWith(".so", StringComparison.OrdinalIgnoreCase);

                if (!take) continue;

                // 防 zip-slip：只用文件名，不带目录层级
                string dest = Path.Combine(binDir, fileName);
                entry.ExtractToFile(dest, overwrite: true);

                if (fileName.Equals(ExeName, StringComparison.OrdinalIgnoreCase))
                    foundExe = true;
            }
        }

        try { File.Delete(zipPath); } catch { /* 删不掉不影响使用 */ }

        if (!foundExe)
        {
            throw new InvalidOperationException(
                $"内核包里没有找到 {ExeName}（说明资产结构变了，需要更新解包规则）");
        }

        // 记录版本，便于将来判断是否需要升级
        await FileCompat.WriteAllTextAsync(VersionFile(engineDir), asset.Tag, ct).ConfigureAwait(false);

        progress?.Report(new Progress(Stage.Done, 0, 0, $"内核 {asset.Tag} 已就绪"));
    }

    /// <summary>删除已安装的内核（用于重装/排障）。</summary>
    public static void Uninstall(string engineDir)
    {
        string binDir = Path.Combine(engineDir, "bin");
        try { if (Directory.Exists(binDir)) Directory.Delete(binDir, recursive: true); } catch { }
        try { if (File.Exists(VersionFile(engineDir))) File.Delete(VersionFile(engineDir)); } catch { }
    }

    /// <summary>读取已安装版本（没有则空串）。</summary>
    public static string InstalledVersion(string engineDir)
    {
        try
        {
            string p = VersionFile(engineDir);
            return File.Exists(p) ? File.ReadAllText(p).Trim() : "";
        }
        catch { return ""; }
    }
}
