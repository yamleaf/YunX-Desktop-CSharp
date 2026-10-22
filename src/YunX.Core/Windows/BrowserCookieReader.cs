using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;

namespace YunX.Core.Windows;

/// <summary>
/// 从本机 Chrome / Edge 导入 Cookie。
///
/// <para><b>为什么值得做</b>：网盘登录都要 Cookie，让用户自己去开发者工具里翻
/// 「Application → Cookies → 复制」对普通用户几乎不可行。而浏览器已经存好了登录态，
/// 直接读出来是最省事的路径。</para>
///
/// <para><b>加密方式</b>（Chromium 的演进，必须逐代兼容）：</para>
/// <list type="number">
///   <item><b>v10 + AES-GCM</b>（当前主流）：密钥在 <c>Local State</c> 的
///         <c>os_crypt.encrypted_key</c> 里，是「DPAPI 保护过的 32 字节随机密钥」。
///         先 <see cref="ProtectedData.Unprotect"/> 解出密钥，再解密
///         <c>v10 || nonce(12) || ciphertext || tag(16)</c>。</item>
///   <item><b>v10 + AES-CBC</b>（旧版）：密钥是硬编码 <c>peanuts</c> 经
///         PBKDF2（1 轮、16 字节）+ IV 16 个空格。用长度和 GCM 解密失败来区分。</item>
///   <item><b>v20 + App-Bound Encryption</b>（Chrome 127+）：密钥由系统服务持有，
///         普通权限<b>解不开</b>。这种情况必须明确告知用户「请改用手动粘贴」，
///         不能假装成功。</item>
/// </list>
///
/// <para><b>文件锁</b>：浏览器运行时 Cookies 库是被独占打开的，
/// 直接连会报「being used by another process」。做法是把主库 + <c>-wal</c>/<c>-journal</c>
/// 一起用 <see cref="FileShare.ReadWrite"/> 流式拷到临时目录再读。</para>
/// </summary>
public static class BrowserCookieReader
{
    /// <summary>一次导入的结果。</summary>
    public sealed class ImportResult
    {
        /// <summary>拼好的 Cookie 请求头（name=value; name2=value2）。</summary>
        public string CookieHeader { get; init; } = "";
        /// <summary>找到的 Cookie 条数。</summary>
        public int Count { get; init; }
        /// <summary>来源浏览器名。</summary>
        public string Source { get; init; } = "";
        /// <summary>遇到 App-Bound 加密（v20）时为 true，此时无法解密。</summary>
        public bool AppBoundBlocked { get; init; }
        /// <summary>给用户看的原因（失败或部分成功时非空）。</summary>
        public string? Note { get; init; }

        /// <summary>
        /// 因浏览器正在运行而读不到 Cookie 的浏览器名。
        ///
        /// <para>新版 Chrome 会用独占方式打开 Cookies 库（配合 App-Bound 加密防抓取），
        /// 这种情况下任何第三方程序都无法读取 —— 只能提示用户退出浏览器。</para>
        /// </summary>
        public List<string> LockedBrowsers { get; init; } = new();
    }

    /// <summary>已知的 Chromium 系浏览器（名称 → User Data 目录）。</summary>
    private static IEnumerable<(string Name, string UserData)> ChromiumBrowsers()
    {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        yield return ("Chrome", Path.Combine(local, "Google", "Chrome", "User Data"));
        yield return ("Edge", Path.Combine(local, "Microsoft", "Edge", "User Data"));
        yield return ("Chrome Beta", Path.Combine(local, "Google", "Chrome Beta", "User Data"));
        yield return ("Chrome Dev", Path.Combine(local, "Google", "Chrome Dev", "User Data"));
        yield return ("Chromium", Path.Combine(local, "Chromium", "User Data"));
        yield return ("Brave", Path.Combine(local, "BraveSoftware", "Brave-Browser", "User Data"));
        // 部分环境把 Profile 放在 Roaming
        yield return ("Chrome (Roaming)", Path.Combine(roaming, "Google", "Chrome", "User Data"));
    }

    /// <summary>
    /// 从所有可用浏览器里挑出匹配指定域后缀的 Cookie。
    /// </summary>
    /// <param name="domainSuffixes">域名后缀，如 <c>quark.cn</c>、<c>115.com</c>。</param>
    public static ImportResult Import(string[] domainSuffixes)
    {
        if (domainSuffixes.Length == 0)
            return new ImportResult { Note = "未指定域名" };

        var found = new List<(string Name, string Value)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new List<string>();
        var locked = new List<string>();
        bool appBound = false;
        string? lastError = null;

        foreach (var (name, userData) in ChromiumBrowsers())
        {
            if (!Directory.Exists(userData)) continue;

            // 多个 Profile（Default、Profile 1…）都扫，用户可能在任意一个里登录的
            foreach (var profile in EnumerateProfiles(userData))
            {
                try
                {
                    var cookies = ReadProfileCookies(profile, domainSuffixes, ref appBound);
                    if (cookies.Count == 0) continue;

                    foreach (var (n, v) in cookies)
                    {
                        if (seen.Add(n)) found.Add((n, v));
                    }
                    if (!sources.Contains(name)) sources.Add(name);
                }
                catch (IOException)
                {
                    // 浏览器正在运行并独占 Cookies 库
                    if (!locked.Contains(name)) locked.Add(name);
                }
                catch (Exception e)
                {
                    lastError = $"{name}: {e.Message}";
                }
            }
        }

        if (appBound && found.Count == 0)
        {
            return new ImportResult
            {
                AppBoundBlocked = true,
                Source = string.Join(" / ", sources),
                LockedBrowsers = locked,
                Note = "浏览器使用了 App-Bound 加密（Chrome 127+），程序无法解密。请改用手动粘贴 Cookie。",
            };
        }

        if (found.Count == 0)
        {
            string note = locked.Count > 0
                ? $"{string.Join("、", locked)} 正在运行，Cookies 库被独占锁定。" +
                  "请先完全退出该浏览器再试，或改用「内嵌浏览器登录」。"
                : lastError ?? "没找到该平台的 Cookie —— 可能还没在这个浏览器里登录过";

            return new ImportResult
            {
                Source = string.Join(" / ", sources),
                Note = note,
                LockedBrowsers = locked,
            };
        }

        // Cookie 顺序无影响，但按名字排序便于人工核对
        string header = string.Join("; ", found
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => $"{x.Name}={x.Value}"));

        // 部分成功时把「哪些浏览器读不到」也告知用户，避免他以为全都导进来了
        var notes = new List<string>();
        if (appBound) notes.Add("部分条目为 App-Bound 加密，已跳过");
        if (locked.Count > 0) notes.Add($"{string.Join("、", locked)} 正在运行，已跳过");

        return new ImportResult
        {
            CookieHeader = header,
            Count = found.Count,
            Source = string.Join(" / ", sources),
            AppBoundBlocked = appBound,
            Note = notes.Count > 0 ? string.Join("；", notes) : null,
            LockedBrowsers = locked,
        };
    }

    private static IEnumerable<string> EnumerateProfiles(string userData)
    {
        // Default 优先
        string def = Path.Combine(userData, "Default");
        if (Directory.Exists(def)) yield return def;

        IEnumerable<string> dirs;
        try
        {
            dirs = Directory.EnumerateDirectories(userData, "Profile *");
        }
        catch
        {
            yield break;
        }

        foreach (var d in dirs) yield return d;
    }

    /// <summary>读一个 Profile 的 Cookies 库。新版在 <c>Network/</c> 子目录，旧版在根。</summary>
    private static List<(string Name, string Value)> ReadProfileCookies(string profileDir,
        string[] domainSuffixes, ref bool appBound)
    {
        var candidates = new[]
        {
            Path.Combine(profileDir, "Network", "Cookies"),
            Path.Combine(profileDir, "Cookies"),
        };

        string? dbPath = candidates.FirstOrDefault(File.Exists);
        if (dbPath == null) return new List<(string, string)>();

        string tempDir = Path.Combine(Path.GetTempPath(), "yunx_cookies_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string tempDb = Path.Combine(tempDir, "Cookies");

        try
        {
            // 浏览器运行时主库被独占，必须流式拷出来；wal/journal 也要一起，否则数据可能不完整
            CopyShared(dbPath, tempDb);
            foreach (var suffix in new[] { "-wal", "-journal", "-shm" })
            {
                string src = dbPath + suffix;
                if (File.Exists(src)) CopyShared(src, tempDb + suffix);
            }

            byte[]? key = LoadChromeKey(profileDir);
            return QueryCookies(tempDb, domainSuffixes, key, ref appBound);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { /* ignore */ }
        }
    }

    /// <summary>用 FileShare.ReadWrite 复制，绕过浏览器的独占锁。</summary>
    private static void CopyShared(string src, string dst)
    {
        using var input = new FileStream(src, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var output = new FileStream(dst, FileMode.Create, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
    }

    private static List<(string Name, string Value)> QueryCookies(string dbPath,
        string[] domainSuffixes, byte[]? key, ref bool appBound)
    {
        var result = new List<(string, string)>();

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
            // 只读 + 不做连接池，避免临时文件被占用导致删除失败
            Pooling = false,
        };

        using var conn = new SqliteConnection(builder.ToString());
        conn.Open();

        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT host_key, name, value, encrypted_value FROM cookies " +
            "WHERE host_key LIKE '%' || $suffix ORDER BY host_key, name";

        // SQLite 只支持一个参数占位，逐后缀查再合并
        foreach (var suffix in domainSuffixes)
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$suffix", suffix);

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                string name = reader.GetString(1);
                string plain = reader.IsDBNull(2) ? "" : reader.GetString(2);
                byte[] enc = reader.IsDBNull(3) ? Array.Empty<byte>() : (byte[])reader.GetValue(3);

                if (plain.Length > 0)
                {
                    result.Add((name, plain));
                    continue;
                }
                if (enc.Length == 0) continue;

                var (value, blocked) = Decrypt(enc, key);
                if (blocked) appBound = true;
                if (value.Length > 0) result.Add((name, value));
            }
        }

        return result;
    }

    /// <summary>
    /// 取 Chrome 的 AES-GCM 主密钥。
    ///
    /// <para><c>Local State</c> 在 User Data 根目录（不是 Profile 目录），
    /// 里面 <c>os_crypt.encrypted_key</c> 是 base64，前 5 字节是 <c>DPAPI</c> 前缀标记。</para>
    /// </summary>
    private static byte[]? LoadChromeKey(string profileDir)
    {
        // profileDir 形如 ...\User Data\Default，Local State 在上一级
        string? userData = Path.GetDirectoryName(profileDir);
        if (userData == null) return null;

        string localState = Path.Combine(userData, "Local State");
        if (!File.Exists(localState)) return null;

        try
        {
            string json = File.ReadAllText(localState, Encoding.UTF8);
            var root = JsonNode.Parse(json);
            string b64 = root?["os_crypt"]?["encrypted_key"]?.GetValue<string>() ?? "";
            if (b64.Length == 0) return null;

            byte[] raw = Convert.FromBase64String(b64);
            // 去掉 "DPAPI" 五字节前缀
            if (raw.Length <= 5) return null;
            byte[] protectedKey = raw[5..];

            return ProtectedData.Unprotect(protectedKey, null, DataProtectionScope.CurrentUser);
        }
        catch
        {
            // App-Bound 加密或权限不足
            return null;
        }
    }

    /// <summary>
    /// 解一条加密 Cookie。
    /// 返回 (明文, 是否因 App-Bound 加密而放弃)。
    /// </summary>
    private static (string Value, bool AppBound) Decrypt(byte[] enc, byte[]? key)
    {
        // v20：App-Bound 加密，普通权限解不开
        if (enc.Length >= 3 && enc[0] == 'v' && enc[1] == '2' && enc[2] == '0')
            return ("", true);

        // v10：AES-GCM（当前主流）
        if (enc.Length >= 3 + 12 + 16 && enc[0] == 'v' && enc[1] == '1' && enc[2] == '0' && key != null)
        {
            try
            {
                var nonce = enc.AsSpan(3, 12).ToArray();
                var cipher = enc.AsSpan(15, enc.Length - 15 - 16).ToArray();
                var tag = enc.AsSpan(enc.Length - 16, 16).ToArray();

                using var aes = new AesGcm(key, 16);
                var plain = new byte[cipher.Length];
                aes.Decrypt(nonce, cipher, tag, plain);
                return (Encoding.UTF8.GetString(plain), false);
            }
            catch
            {
                // 落到下面的旧版 CBC 分支
            }
        }

        // v10：旧版 AES-CBC（key = PBKDF2("peanuts", "saltysalt", 1)，IV = 16 空格）
        if (enc.Length > 3 && enc[0] == 'v' && enc[1] == '1' && enc[2] == '0')
        {
            try
            {
                byte[] legacyKey = Rfc2898DeriveBytes.Pbkdf2("peanuts", Encoding.ASCII.GetBytes("saltysalt"),
                    1, HashAlgorithmName.SHA1, 16);
                using var aes = Aes.Create();
                aes.Key = legacyKey;
                aes.IV = Encoding.ASCII.GetBytes(new string(' ', 16));
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;

                byte[] plain = aes.DecryptCbc(enc.AsSpan(3).ToArray(), aes.IV);
                return (Encoding.UTF8.GetString(plain), false);
            }
            catch
            {
                return ("", false);
            }
        }

        return ("", false);
    }
}
