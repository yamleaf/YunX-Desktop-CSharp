using System.Security.Cryptography;
using System.Text;

namespace YunX.Core.Security;

/// <summary>凭证保护等级（对应原项目「数据库加密等级」三档）。</summary>
public enum KeyProtectionLevel
{
    /// <summary>
    /// 宽松：主密钥明文放在 <c>credential.key</c>。
    /// 换机器 / 便携版带走必须用这档（其他两档都绑定本机）。
    ///
    /// <para><b>代价要明说</b>：密钥与数据同处一个目录，谁拿到这个目录就拿到了全部凭证。
    /// 只在"确实要跨设备携带"时选它。</para>
    /// </summary>
    Portable = 0,

    /// <summary>
    /// 普通（默认）：主密钥用本机派生的 KEK 包裹（PBKDF2 21 万次 + 机器标识）。
    ///
    /// <para><b>换机解不开是刻意的设计，不是缺陷</b>：登录凭证（网盘 Cookie / GitHub Token）
    /// 等价于账号控制权，不应当拷一个数据目录就能在另一台机器上继续用。
    /// 绑定本机让"数据目录被拷走"这条攻击路径失效 —— 攻击者还得拿到那台机器本身。</para>
    ///
    /// <para>需要跨设备时的正路是"用口令派生的备份"（<c>.yunx</c>，见 AuthBackup），
    /// 而不是放宽主密钥的保护。</para>
    /// </summary>
    MachineBound = 1,

    /// <summary>
    /// 较为严格：主密钥由 Windows DPAPI 保护，绑定当前登录用户。
    /// 换用户 / 换机同样解不开，理由同上。
    /// </summary>
    UserBound = 2,
}

/// <summary>
/// 凭证加密器：AES-256-GCM 加密 Cookie / Token / JWT，主密钥按等级保护。
///
/// <para><b>为什么不是"直接用口令派生的密钥加密数据"</b>：那样每次改口令都要重新加密全部凭证。
/// 这里采用信封加密（envelope encryption）：数据永远用**固定的主密钥**加密，
/// 切换保护等级时只重新包裹这 32 字节主密钥，库内密文一行都不用动。
/// 这也是原项目的做法，是正确的设计。</para>
///
/// <para>GCM 的 nonce 每次随机生成，与密文一起存储（<c>nonce(12) + tag(16) + ciphertext</c>）。</para>
/// </summary>
public sealed class CredentialCipher : IDisposable
{
    private const int KeySize = 32;          // AES-256
    private const int NonceSize = 12;        // GCM 标准 nonce
    private const int TagSize = 16;
    private const int Pbkdf2Iterations = 210_000;
    private const string KeyFileName = "credential.key";

    private readonly string _keyPath;
    private byte[]? _masterKey;
    private readonly object _gate = new();

    /// <summary>
    /// 上次 <see cref="Initialize"/> 是否丢掉了旧主密钥（解不开 → 重新生成）。
    ///
    /// <para>必须让调用方看见：丢掉主密钥意味着**所有已保存的凭证永久失效**。
    /// 之前这里完全静默 —— 用户只会看到「账号全都退回未登录」，
    /// 却不知道为什么，也没有任何补救入口。</para>
    /// </summary>
    public bool MasterKeyWasReset { get; private set; }

    /// <summary>导致重置的原始异常（没有则为 null）。用于向用户展示原因。</summary>
    public Exception? ResetReason { get; private set; }

    /// <summary>密钥文件的完整路径（供「清理损坏凭据」使用）。</summary>
    public string KeyFilePath => _keyPath;

    public KeyProtectionLevel Level { get; private set; } = KeyProtectionLevel.MachineBound;

    public CredentialCipher(string dataDirectory, KeyProtectionLevel level)
    {
        Directory.CreateDirectory(dataDirectory);
        _keyPath = Path.Combine(dataDirectory, KeyFileName);
        Level = level;
    }

    /// <summary>
    /// 装载（或首次生成）主密钥。
    /// </summary>
    public void Initialize()
    {
        lock (_gate)
        {
            MasterKeyWasReset = false;
            ResetReason = null;

            if (File.Exists(_keyPath))
            {
                try
                {
                    var blob = KeyFileBlob.FromBytes(File.ReadAllBytes(_keyPath));
                    Level = blob.Level;
                    _masterKey = Unwrap(blob);
                    if (_masterKey != null) return;

                    // 文件存在、格式合法，但就是解不开：
                    // 换了机器 / 换了 Windows 用户 / 文件被改坏。
                    ResetReason = new CryptographicException(
                        "密钥文件存在但无法解开（可能是换了电脑或换了 Windows 用户，" +
                        "也可能是文件损坏）。");
                }
                catch (Exception ex)
                {
                    // 密钥文件损坏 → 走重新生成，但把原因记下来
                    ResetReason = ex;
                }
            }

            // 没有可用密钥：生成新的（旧凭证将无法解密，调用方需要能容忍）
            MasterKeyWasReset = File.Exists(_keyPath);
            _masterKey = Rng48.GetBytes(KeySize);
            Save();
        }
    }

    /// <summary>
    /// 清掉损坏的密钥文件并重建主密钥。
    ///
    /// <para>语义：**放弃所有已加密的凭证**。这是用户明确选择「清理」时才该调的路径 ——
    /// 自动重置会让用户毫无察觉地丢掉网盘登录态，必须由人确认。</para>
    /// </summary>
    public void ResetKeyFile()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(_keyPath)) File.Delete(_keyPath);
            }
            catch (Exception ex)
            {
                throw new IOException("删除密钥文件失败：" + ex.Message, ex);
            }

            MasterKeyWasReset = false;
            ResetReason = null;
            _masterKey = Rng48.GetBytes(KeySize);
            Save();
        }
    }

    /// <summary>
    /// 切换保护等级：只重新包裹主密钥，不触碰已加密数据。
    /// </summary>
    public void ChangeLevel(KeyProtectionLevel level)
    {
        lock (_gate)
        {
            if (_masterKey == null) throw new InvalidOperationException("尚未初始化");
            Level = level;
            Save();
        }
    }

    /// <summary>
    /// 清零主密钥。
    ///
    /// <para>主密钥是 32 字节的托管数组，GC 不会清零内容；进程内存转储 / 页面文件里
    /// 能直接读到它，等于所有已加密凭证全部暴露。退出时显式 <c>ZeroMemory</c> 是
    /// 这个类唯一有意义的收尾动作。</para>
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_masterKey == null) return;
            Crypto48.ZeroMemory(_masterKey);
            _masterKey = null;
        }
    }

    private void Save()
    {
        if (_masterKey == null) return;
        var blob = Wrap(_masterKey);
        // 原子写：先写临时文件再替换，避免掉电留下半截密钥文件
        string tmp = _keyPath + ".tmp";
        File.WriteAllBytes(tmp, blob.ToBytes());
#if NET48
        // net48 没有 File.Move(overwrite) 重载：先删再移（同一目录，窗口极短）
        if (File.Exists(_keyPath)) File.Delete(_keyPath);
        File.Move(tmp, _keyPath);
#else
        File.Move(tmp, _keyPath, overwrite: true);
#endif
    }

    // ================= 包裹 / 解包主密钥 =================

    private KeyFileBlob Wrap(byte[] master)
    {
        byte[] nonce = Rng48.GetBytes(NonceSize);

        switch (Level)
        {
            case KeyProtectionLevel.Portable:
                // 明文存储：nonce 仅用于格式统一
                return new KeyFileBlob(Level, nonce, master, Array.Empty<byte>());

            case KeyProtectionLevel.UserBound:
            {
                byte[] protectedKey = ProtectedData.Protect(master, Entropy,
                    DataProtectionScope.CurrentUser);
                return new KeyFileBlob(Level, nonce, protectedKey, Array.Empty<byte>());
            }

            default:
            {
                byte[] kek = DeriveMachineKek(out byte[] salt);
                // ★ Payload 只放 tag+ct，nonce 单独存 header。
                //
                //   这里曾经写成 AesGcmEncrypt(kek, nonce, master) —— 那个方法返回的是
                //   nonce(12)+tag(16)+ct(32) 的**完整自包含 blob**，而 Unwrap 走的是
                //   「nonce 在 header、Payload 只有 tag+ct」的分支，于是把 Payload 的前
                //   16 字节当 tag 去验 —— 那是 nonce 的前 12 字节加 tag 的前 4 字节。
                //
                //   后果非常隐蔽：认证标签永远对不上 → Unwrap 恒返回 null →
                //   Initialize 每次都判定「没有可用密钥」→ 重新生成主密钥并覆盖写回。
                //   表现是「每次都能启动，但之前保存的凭证全部解不开、账号页看起来正常，
                //   实际每次启动都在丢凭证」；一旦叠加别的问题（比如构造抛异常），
                //   就变成账号页彻底空白，且找不到任何有用的报错。
                byte[] blob = AesGcmEncrypt(kek, nonce, master);
                byte[] payload = new byte[blob.Length - NonceSize];
                Buffer.BlockCopy(blob, NonceSize, payload, 0, payload.Length);
                return new KeyFileBlob(Level, nonce, payload, salt);
            }
        }
    }

    private byte[]? Unwrap(KeyFileBlob blob)
    {
        try
        {
            switch (blob.Level)
            {
                case KeyProtectionLevel.Portable:
                    return blob.Payload.Length == KeySize ? blob.Payload : null;

                case KeyProtectionLevel.UserBound:
                    return ProtectedData.Unprotect(blob.Payload, Entropy,
                        DataProtectionScope.CurrentUser);

                default:
                {
                    byte[] kek = DeriveMachineKek(out byte[] salt, blob.Salt);
                    // salt 必须与包裹时一致，否则派生出的 KEK 不同（解包失败 = 换了机器）
                    if (!salt.SequenceEqual(blob.Salt)) return null;

                    // 正常格式：Payload = tag + ct，nonce 在 header
                    try
                    {
                        return AesGcmDecrypt(kek, blob.Nonce, blob.Payload);
                    }
                    catch (CryptographicException)
                    {
                        // 兼容旧格式：早期版本把 AesGcmEncrypt 的完整输出
                        // （nonce+tag+ct）直接塞进了 Payload，于是 nonce 存了两份。
                        // 那种文件现在还能读出来 —— 只要确实能解开，就说明是老版本写的，
                        // 不必让用户承担丢凭证的代价。
                        return TryUnwrapSelfContained(kek, blob.Payload);
                    }
                }
            }
        }
        catch
        {
            // DPAPI 换用户 / 换机器都会到这里
            return null;
        }
    }

    /// <summary>
    /// 解开「自包含」格式的旧 Payload（nonce(12) + tag(16) + ct）。
    ///
    /// <para>只用于兼容历史文件。<see cref="Wrap"/> 现在写的是 tag+ct，
    /// 但老版本误把完整 blob 存了进来 —— 那些文件如果还能解开，
    /// 就不该让用户白白丢一次凭证。</para>
    /// </summary>
    private static byte[]? TryUnwrapSelfContained(byte[] kek, byte[] payload)
    {
        if (payload.Length < NonceSize + TagSize + KeySize) return null;
        try
        {
            byte[] innerNonce = payload.AsSpan(0, NonceSize).ToArray();
            byte[] tag = payload.AsSpan(NonceSize, TagSize).ToArray();
            byte[] ct = payload.AsSpan(NonceSize + TagSize).ToArray();
            byte[] pt = new byte[ct.Length];
            using var gcm = new AesGcmCompat(kek, TagSize);
            gcm.Decrypt(innerNonce, ct, tag, pt);
            return pt.Length == KeySize ? pt : null;
        }
        catch
        {
            return null;
        }
    }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("YunX.Desktop.Credential.v1");

    /// <summary>
    /// 派生包装密钥（KEK）。绑定机器名 + 用户 SID，换机 / 换用户都解不开 —— 这是刻意的。
    /// </summary>
    private static byte[] DeriveMachineKek(out byte[] salt, byte[]? existingSalt = null)
    {
        salt = existingSalt is { Length: 16 } ? existingSalt : Rng48.GetBytes(16);

        string machine = Environment.MachineName;
        string user = Environment.UserName;
        byte[] material = Encoding.UTF8.GetBytes($"YunX|{machine}|{user}|v1");

        return Pbkdf2_48.Derive(material, salt, Pbkdf2Iterations,
            HashAlgorithmName.SHA256, KeySize);
    }

    // ================= 数据加解密 =================

    /// <summary>加密一段文本，返回 Base64（nonce + tag + ciphertext）。</summary>
    public string Encrypt(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext)) return "";
        lock (_gate)
        {
            if (_masterKey == null) throw new InvalidOperationException("尚未初始化");
            byte[] plain = Encoding.UTF8.GetBytes(plaintext);
            byte[] nonce = Rng48.GetBytes(NonceSize);
            byte[] blob = AesGcmEncrypt(_masterKey, nonce, plain);
            return Convert.ToBase64String(blob);
        }
    }

    /// <summary>解密；密文损坏或密钥不匹配返回 null（不抛异常，避免一个坏凭证拖垮整个列表）。</summary>
    public string? Decrypt(string? base64)
    {
        if (string.IsNullOrEmpty(base64)) return "";
        lock (_gate)
        {
            if (_masterKey == null) return null;
            try
            {
                byte[] blob = Convert.FromBase64String(base64);
                byte[] plain = AesGcmDecrypt(_masterKey, null, blob);
                return Encoding.UTF8.GetString(plain);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>输出格式：nonce(12) | tag(16) | ciphertext。</summary>
    private static byte[] AesGcmEncrypt(byte[] key, byte[] nonce, byte[] plain)
    {
        byte[] output = new byte[NonceSize + TagSize + plain.Length];
        nonce.CopyTo(output, 0);
        using var gcm = new AesGcmCompat(key, TagSize);
#if NET48
        // AesGcmCompat 的签名只接受 byte[]（BCrypt 需要 pin 住的缓冲，
        // Span 无法直接 pin）。这里用临时数组再拷回，多一次 32~64 字节的拷贝，
        // 对凭证加密这种低频路径完全可以接受。
        byte[] ct = new byte[plain.Length];
        byte[] tag = new byte[TagSize];
        gcm.Encrypt(nonce, plain, ct, tag);
        Buffer.BlockCopy(ct, 0, output, NonceSize + TagSize, ct.Length);
        Buffer.BlockCopy(tag, 0, output, NonceSize, TagSize);
#else
        gcm.Encrypt(nonce, plain, output.AsSpan(NonceSize + TagSize),
            output.AsSpan(NonceSize, TagSize));
#endif
        return output;
    }

    /// <summary>解密；<paramref name="nonce"/> 为 null 表示 nonce 在密文头部。</summary>
    private static byte[] AesGcmDecrypt(byte[] key, byte[]? nonce, byte[] blob)
    {
        byte[] actualNonce;
        byte[] cipher;
        byte[] tag;

        if (nonce != null)
        {
            // 包裹路径：nonce 单独存放，blob 是 tag + ciphertext
            actualNonce = nonce;
            tag = blob.AsSpan(0, TagSize).ToArray();
            cipher = blob.AsSpan(TagSize).ToArray();
        }
        else
        {
            if (blob.Length < NonceSize + TagSize) throw new CryptographicException("密文过短");
            actualNonce = blob.AsSpan(0, NonceSize).ToArray();
            tag = blob.AsSpan(NonceSize, TagSize).ToArray();
            cipher = blob.AsSpan(NonceSize + TagSize).ToArray();
        }

        byte[] plain = new byte[cipher.Length];
        using var gcm = new AesGcmCompat(key, TagSize);
        gcm.Decrypt(actualNonce, cipher, tag, plain);
        return plain;
    }
}

/// <summary>
/// 密钥文件格式：magic(4) | version(1) | level(1) | nonceLen(1) | saltLen(1) | nonce | salt | payload
/// </summary>
internal sealed class KeyFileBlob
{
    private static readonly byte[] Magic = "YXK1"u8.ToArray();

    public KeyProtectionLevel Level { get; }
    public byte[] Nonce { get; }
    public byte[] Payload { get; }
    public byte[] Salt { get; }

    public KeyFileBlob(KeyProtectionLevel level, byte[] nonce, byte[] payload, byte[] salt)
    {
        Level = level;
        Nonce = nonce;
        Payload = payload;
        Salt = salt;
    }

    public byte[] ToBytes()
    {
        using var ms = new MemoryStream();
        ms.Write(Magic, 0, Magic.Length);
        ms.WriteByte(1);
        ms.WriteByte((byte)Level);
        ms.WriteByte((byte)Nonce.Length);
        ms.WriteByte((byte)Salt.Length);
        ms.Write(Nonce, 0, Nonce.Length);
        ms.Write(Salt, 0, Salt.Length);
        ms.Write(Payload, 0, Payload.Length);
        return ms.ToArray();
    }

    public static KeyFileBlob FromBytes(byte[] data)
    {
        if (data.Length < 8) throw new CryptographicException("密钥文件过短");
        if (!data.AsSpan(0, 4).SequenceEqual(Magic))
            throw new CryptographicException("不是 YunX 密钥文件");

        int level = data[5];
        int nonceLen = data[6];
        int saltLen = data[7];
        int offset = 8;

        byte[] nonce = data.AsSpan(offset, nonceLen).ToArray();
        offset += nonceLen;
        byte[] salt = data.AsSpan(offset, saltLen).ToArray();
        offset += saltLen;
        byte[] payload = data.AsSpan(offset).ToArray();

        return new KeyFileBlob((KeyProtectionLevel)level, nonce, payload, salt);
    }
}
