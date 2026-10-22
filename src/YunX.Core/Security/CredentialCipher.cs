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
            if (File.Exists(_keyPath))
            {
                try
                {
                    var blob = KeyFileBlob.FromBytes(File.ReadAllBytes(_keyPath));
                    Level = blob.Level;
                    _masterKey = Unwrap(blob);
                    if (_masterKey != null) return;
                }
                catch
                {
                    // 密钥文件损坏或绑定环境变化 → 走重新生成
                }
            }

            // 没有可用密钥：生成新的（旧凭证将无法解密，调用方需要能容忍）
            _masterKey = RandomNumberGenerator.GetBytes(KeySize);
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
            CryptographicOperations.ZeroMemory(_masterKey);
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
        File.Move(tmp, _keyPath, overwrite: true);
    }

    // ================= 包裹 / 解包主密钥 =================

    private KeyFileBlob Wrap(byte[] master)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);

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
                byte[] wrapped = AesGcmEncrypt(kek, nonce, master);
                return new KeyFileBlob(Level, nonce, wrapped, salt);
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
                    return AesGcmDecrypt(kek, blob.Nonce, blob.Payload);
                }
            }
        }
        catch
        {
            // DPAPI 换用户 / 换机器都会到这里
            return null;
        }
    }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("YunX.Desktop.Credential.v1");

    /// <summary>
    /// 派生包装密钥（KEK）。绑定机器名 + 用户 SID，换机 / 换用户都解不开 —— 这是刻意的。
    /// </summary>
    private static byte[] DeriveMachineKek(out byte[] salt, byte[]? existingSalt = null)
    {
        salt = existingSalt is { Length: 16 } ? existingSalt : RandomNumberGenerator.GetBytes(16);

        string machine = Environment.MachineName;
        string user = Environment.UserName;
        byte[] material = Encoding.UTF8.GetBytes($"YunX|{machine}|{user}|v1");

        return Rfc2898DeriveBytes.Pbkdf2(material, salt, Pbkdf2Iterations,
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
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);
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
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plain, output.AsSpan(NonceSize + TagSize),
            output.AsSpan(NonceSize, TagSize));
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
        using var gcm = new AesGcm(key, TagSize);
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
        ms.Write(Magic);
        ms.WriteByte(1);
        ms.WriteByte((byte)Level);
        ms.WriteByte((byte)Nonce.Length);
        ms.WriteByte((byte)Salt.Length);
        ms.Write(Nonce);
        ms.Write(Salt);
        ms.Write(Payload);
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
