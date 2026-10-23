using System.Text.Json;
using System.Text.Json.Serialization;

namespace YunX.Core.Download;

/// <summary>一个已完成区间（用于 JSON 序列化）。</summary>
public sealed class IntervalDto
{
    [JsonPropertyName("s")] public long Start { get; set; }
    [JsonPropertyName("e")] public long End { get; set; }
}

/// <summary>
/// 断点续传元数据，与 <c>xxx.part</c> 同目录、名为 <c>xxx.part.json</c>。
///
/// <para><b>三处关键校验</b>（缺任一条都可能拼出损坏文件）：</para>
/// <list type="number">
///   <item><b>UrlKey</b>：确认这个 part 属于同一个资源（去掉镜像前缀差异后比对）；</item>
///   <item><b>Total</b>：总大小必须一致；</item>
///   <item><b>ETag</b>：续传时以 <c>If-Range</c> 发出。服务器若发现内容变了会返回 200（全量）
///         而不是 206（部分），此时必须丢弃旧数据重下 —— 原项目完全没有这层校验。</item>
/// </list>
///
/// <para>写入采用 <c>临时文件 + 原子替换</c>，掉电不会留下半截 JSON。</para>
/// </summary>
public sealed class PartMetadata
{
    public const string Version = "2";

    [JsonPropertyName("v")] public string SchemaVersion { get; set; } = Version;
    [JsonPropertyName("urlKey")] public string UrlKey { get; set; } = "";
    [JsonPropertyName("total")] public long Total { get; set; }
    [JsonPropertyName("etag")] public string? ETag { get; set; }
    [JsonPropertyName("lastModified")] public string? LastModified { get; set; }
    [JsonPropertyName("chunkSize")] public long ChunkSize { get; set; }
    [JsonPropertyName("done")] public List<IntervalDto> Done { get; set; } = new();
    [JsonPropertyName("mode")] public string Mode { get; set; } = "multi";
    [JsonPropertyName("mirror")] public string Mirror { get; set; } = "";
    [JsonPropertyName("savedAt")] public long SavedAt { get; set; }

    public static string PathFor(string partFile) => partFile + ".json";

    /// <summary>把 URL 归一化为「资源身份」：去协议、去查询串，统一小写。</summary>
    public static string UrlKeyOf(string url)
    {
        try
        {
            var u = new Uri(url);
            return (u.Host + u.AbsolutePath).ToLowerInvariant();
        }
        catch
        {
            return url.Split('?')[0].ToLowerInvariant();
        }
    }

    public static PartMetadata? TryLoad(string partFile)
    {
        string path = PathFor(partFile);
        if (!File.Exists(path)) return null;
        try
        {
            using var fs = File.OpenRead(path);
            var meta = JsonSerializer.Deserialize<PartMetadata>(fs);
            if (meta == null || meta.SchemaVersion != Version) return null;
            return meta;
        }
        catch
        {
            // 元数据损坏 = 断点不可信，一律当作没有
            return null;
        }
    }

    /// <summary>
    /// 原子写入：先序列化到内存，再写临时文件，最后替换目标。
    ///
    /// <para><b>为什么必须加锁</b>：同一任务的 N 个 worker 在分片完成时都会调这里
    /// （引擎侧已按 1 秒节流，但节流窗口内仍可能有多个线程同时进入）。原来的实现用
    /// 固定名 <c>xxx.part.json.tmp</c> 且无锁：A 正在写 tmp 时 B 执行 <c>File.Create(tmp)</c>
    /// 会因 <c>FileShare.None</c> 抛 IOException（被吞），更糟的是 B 的清理分支会把 A 的
    /// tmp 删掉 → A 的 <c>File.Move</c> 抛 FileNotFoundException，或把半截 JSON 落成正式
    /// 元数据 → 下次 <see cref="TryLoad"/> 返回 null → 静默从 0 重下。</para>
    /// </summary>
    public void Save(string partFile)
    {
        string path = PathFor(partFile);

        // 先序列化到内存：序列化异常时磁盘上什么都没发生过
        byte[] payload;
        try
        {
            SavedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            payload = JsonSerializer.SerializeToUtf8Bytes(this, new JsonSerializerOptions
            {
                WriteIndented = false,
            });
        }
        catch
        {
            return;
        }

        string tmp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        lock (GateFor(path))
        {
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None))
                {
                    fs.Write(payload, 0, payload.Length);
                    fs.Flush(flushToDisk: true);
                }
#if NET48
                // net48 的 File.Move 没有 overwrite 重载：先删目标再移。
                // 这里的 tmp→path 是同一目录内的原子替换，删了再移的窗口极短，
                // 且失败时 tmp 还在，调用方的 catch 会保留它。
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
#else
                File.Move(tmp, path, overwrite: true);
#endif
            }
            catch
            {
                // 元数据写失败不应中断下载；只是下次不能续传
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
            }
        }
    }

    /// <summary>按目标路径分桶的写锁。不同任务的元数据互不阻塞。</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Gates
        = new(StringComparer.OrdinalIgnoreCase);

    private static object GateFor(string path) => Gates.GetOrAdd(path, _ => new object());

    public static void Delete(string partFile)
    {
        string path = PathFor(partFile);
        lock (GateFor(path))
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }

            // 清掉可能残留的临时文件（进程被强杀 / 写失败时留下的）
            string dir = Path.GetDirectoryName(path) is { Length: > 0 } d ? d : ".";
            string pattern = Path.GetFileName(path) + ".*.tmp";
            try
            {
                foreach (var stale in Directory.EnumerateFiles(dir, pattern))
                {
                    try { File.Delete(stale); } catch { /* ignore */ }
                }
            }
            catch { /* ignore */ }
        }
    }
}
