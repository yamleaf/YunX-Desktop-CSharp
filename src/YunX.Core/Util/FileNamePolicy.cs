using System.Security.Cryptography;

namespace YunX.Core.Util;

/// <summary>
/// 下载文件名 / 相对路径净化。
///
/// 比原 Python 版严格得多，补上了原版缺失的**目录穿越防护**：
/// 原版 <c>safe_filename</c> 只替换非法字符，若将来支持「保留目录结构下载」，
/// 形如 <c>../../windows/system32/x.dll</c> 的路径会逃出下载目录。
/// </summary>
public static class FileNamePolicy
{
    private static readonly char[] Illegal = { '<', '>', ':', '"', '/', '\\', '|', '?', '*' };

    /// <summary>Windows 保留设备名（大小写不敏感，且带扩展名同样被占用）。</summary>
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private const int MaxNameLength = 180;

    /// <summary>
    /// 净化单个文件名。返回可直接落盘的名字（不含目录分隔符）。
    /// </summary>
    public static string SanitizeFileName(string? name, string fallback = "download.bin")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;

        var cleaned = new string(name
            .Replace('\r', ' ').Replace('\n', ' ')
            .Select(c => Illegal.Contains(c) || char.IsControl(c) ? '_' : c)
            .ToArray()).Trim().Trim('.');

        if (cleaned.Length == 0) return fallback;

        // 只取最后一段，防 `a/b` 这类把目录塞进文件名的输入
        int slash = cleaned.LastIndexOfAny(new[] { '/', '\\' });
        if (slash >= 0) cleaned = cleaned[(slash + 1)..];
        if (cleaned.Length == 0) return fallback;

        if (cleaned.Length > MaxNameLength)
        {
            string ext = Path.GetExtension(cleaned);
            if (ext.Length > 16) ext = string.Empty;
            int keep = MaxNameLength - ext.Length;
            cleaned = cleaned[..Math.Max(1, keep)] + ext;
        }

        string stem = Path.GetFileNameWithoutExtension(cleaned);
        if (Reserved.Contains(stem)) cleaned = "_" + cleaned;

        return cleaned;
    }

    /// <summary>
    /// 净化一个"相对路径"（用于保留目录结构下载，如 <c>docs/img/a.png</c>）。
    /// 拒绝绝对路径、盘符、<c>..</c> 段；任何可疑输入一律降级为纯文件名。
    /// </summary>
    public static string SanitizeRelativePath(string? path, string fallback = "download.bin")
    {
        if (string.IsNullOrWhiteSpace(path)) return fallback;

        // 绝对路径 / 盘符 / UNC 直接拒绝：只保留最后一段文件名
        string normalized = path.Replace('\\', '/');
        if (Path.IsPathRooted(path) || normalized.Length >= 2 && normalized[1] == ':')
        {
            var leaf = Path.GetFileName(path);
            if (string.IsNullOrEmpty(leaf)) leaf = normalized.Split('/').LastOrDefault() ?? "";
            return SanitizeFileName(leaf, fallback);
        }

        var segments = new List<string>();
        foreach (var raw in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            // "." 与 ".." 一律丢弃 —— 目录穿越的核心防线
            if (raw is "." or "..") continue;
            var seg = SanitizeFileName(raw, string.Empty);
            if (seg.Length > 0 && seg != "_") segments.Add(seg);
        }

        if (segments.Count == 0) return fallback;

        // 最多 8 层：防止恶意构造的超深路径触达系统路径长度上限
        if (segments.Count > 8) segments = segments.Take(8).ToList();
        return string.Join("/", segments);
    }

    /// <summary>
    /// 落盘前二次校验：确保最终解析出的绝对路径仍位于 <paramref name="baseDir"/> 之内。
    /// 防符号链接 / 大小写 / 短名绕过。
    /// </summary>
    public static bool IsContained(string baseDir, string candidateFullPath)
    {
        try
        {
            string b = Path.GetFullPath(baseDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            string c = Path.GetFullPath(candidateFullPath);
            return c.StartsWith(b, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(c, b.TrimEnd(Path.DirectorySeparatorChar),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 选一个不冲突的目标路径：绝不覆盖用户已有文件。
    /// 依次尝试 <c>name</c>、<c>name (1)</c>…<c>name (999)</c>，同时避开未完成的 .gfdpart。
    /// </summary>
    /// <summary>
    /// 进程内已分配但尚未落盘的目标路径。
    ///
    /// <para><b>为什么需要它</b>：<see cref="EnsureUnique"/> 是典型的 check-then-act ——
    /// 「看看这个文件名在不在，不在就用它」。两个并发任务同时下同名文件时（GitHub 批量
    /// 下载最典型：每个子目录都有一个 <c>README.md</c>），它们都会看到该名字可用，
    /// 于是拿到同一条目标路径，写同一个 <c>.part</c> → 数据交错 → 落出一个损坏文件，
    /// 而且两边都以为成功了。加一张预留表把这个判断变成原子的。</para>
    ///
    /// <para>条目由 <see cref="Release"/> 释放；即使漏了几条也不影响正确性 ——
    /// 文件真正落盘后 <c>File.Exists</c> 会接管判断，最多让后续同名文件多一个编号。</para>
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte>
        Claimed = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>预留表上限：超过就整体清空（说明有大量并发任务，旧条目已无意义）。</summary>
    private const int MaxClaimed = 512;

    /// <summary>
    /// 选一个不冲突的目标路径，并<b>原子地</b>预留它。
    ///
    /// <para>冲突判断覆盖三种形态：正式文件、<c>.part</c>、<c>.part.json</c> ——
    /// 少判任何一个都可能复用到一个还在下载中的断点。</para>
    /// </summary>
    public static string EnsureUnique(string directory, string fileName)
    {
        if (Claimed.Count > MaxClaimed) Claimed.Clear();

        string candidate = Path.Combine(directory, fileName);
        if (TryReserve(candidate)) return candidate;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        for (int i = 1; i < 1000; i++)
        {
            candidate = Path.Combine(directory, $"{stem} ({i}){ext}");
            if (TryReserve(candidate)) return candidate;
        }

        // 极端情况：同名文件上千个。带时间戳 + Guid 后缀保证唯一
        return Path.Combine(directory,
            $"{stem}-{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..4]}{ext}");
    }

    /// <summary>释放预留（任务结束、路径不再需要独占时调用）。</summary>
    public static void Release(string path)
    {
        if (string.IsNullOrEmpty(path)) return;
        Claimed.TryRemove(path, out _);
    }

    private static bool TryReserve(string candidate)
    {
        if (File.Exists(candidate) || File.Exists(candidate + ".part")
            || File.Exists(candidate + ".part.json"))
            return false;

        // TryAdd 成功 = 这个进程里第一次占用这个名字
        return Claimed.TryAdd(candidate, 0);
    }

    /// <summary>日志脱敏：URL 只留 scheme://host，查询串整体丢弃（可能含 Token）。</summary>
    public static string RedactUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return "<invalid-url>";
        return $"{u.Scheme}://{u.Host}{(u.IsDefaultPort ? "" : ":" + u.Port)}{u.AbsolutePath}";
    }

    /// <summary>计算文件的 SHA-256（十六进制）。校验用。</summary>
    public static async Task<string> Sha256Async(string path, CancellationToken ct = default)
    {
#if NET48
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
#else
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
#endif
            1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var sha = SHA256.Create();
        byte[] hash = await HashCompat.ComputeHashAsync(sha, fs, ct).ConfigureAwait(false);
        return Helpers.ToHexString(hash).ToLowerInvariant();
    }
}
