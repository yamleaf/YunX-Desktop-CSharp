using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using YunX.Core.Security;

namespace YunX.Core.Data;

/// <summary>
/// 认证备份（<c>.yunx</c> 文件）。
///
/// <para><b>为什么要独立于主密钥体系</b>：主密钥按设计绑定本机（换机解不开），
/// 但用户换电脑时希望能带走登录状态。所以备份用<b>用户口令</b>派生的密钥加密 ——
/// 与机器无关，只要记得口令就能在任意设备恢复。</para>
///
/// <para><b>文件格式</b>：
/// <c>magic(4) "YXBK" | version(1) | iterations(4) | salt(16) | nonce(12) | ciphertext</c>。
/// PBKDF2-SHA256 派生出 32 字节密钥，AES-256-GCM 加密整个 JSON。</para>
/// </summary>
public static class AuthBackup
{
    private static readonly byte[] Magic = "YXBK"u8.ToArray();
    private const byte Version = 1;
    private const int Iterations = 310_000;   // 与主密钥同一量级
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    /// <summary>备份内容（平台 → 已加密的凭证密文；备份内再整体加密一次）。</summary>
    private sealed class Payload
    {
        public Dictionary<string, string> Credentials { get; set; } = new();
        public string CreatedAt { get; set; } = "";
        public string Machine { get; set; } = "";
    }

    /// <summary>导出备份，返回生成的文件路径。</summary>
    public static string Export(AppDatabase db, CredentialCipher cipher, string password,
        string outputPath)
    {
        // 1) 取出所有平台的凭证（已解密，因为要在新设备上重新加密保存）
        var creds = new Dictionary<string, string>();
        foreach (var platform in new CredentialRepository(db, cipher).Platforms())
        {
            var loaded = new CredentialRepository(db, cipher).Load(platform);
            if (loaded == null) continue;
            // 用 平台|extra 的结构保存，恢复时按同样的结构还原
            creds[platform] = $"{loaded.Value.Value}\u0001{loaded.Value.Extra}";
        }

        var payload = new Payload
        {
            Credentials = creds,
            CreatedAt = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Machine = Environment.MachineName,
        };

        byte[] json = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));

        // 2) 用口令派生密钥加密
        byte[] salt = Rng48.GetBytes(SaltSize);
        byte[] key = Pbkdf2_48.Derive(Encoding.UTF8.GetBytes(password), salt,
            Iterations, HashAlgorithmName.SHA256, KeySize);
        byte[] nonce = Rng48.GetBytes(NonceSize);

        byte[] cipherText = new byte[json.Length];
        byte[] tag = new byte[TagSize];
        using (var gcm = new AesGcmCompat(key, TagSize))
        {
            gcm.Encrypt(nonce, json, cipherText, tag);
        }

        // 3) 组装文件
        using var ms = new MemoryStream();
        ms.Write(Magic, 0, Magic.Length);
        ms.WriteByte(Version);
        // net48 的 MemoryStream 没有 Write(byte[]) 单参数重载，只有 Write(byte[],int,int)
        byte[] iterBytes = BitConverter.GetBytes(Iterations);
        ms.Write(iterBytes, 0, iterBytes.Length);
        ms.Write(salt, 0, salt.Length);
        ms.Write(nonce, 0, nonce.Length);
        ms.Write(tag, 0, tag.Length);
        ms.Write(cipherText, 0, cipherText.Length);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllBytes(outputPath, ms.ToArray());
        Crypto48.ZeroMemory(key);
        return outputPath;
    }

    /// <summary>导入备份，返回恢复的平台数量。</summary>
    public static int Import(AppDatabase db, CredentialCipher cipher, string password,
        string backupPath)
    {
        byte[] data = File.ReadAllBytes(backupPath);
        if (data.Length < 4 + 1 + 4 + SaltSize + NonceSize + TagSize)
            throw new InvalidDataException("备份文件已损坏");
        if (!data.AsSpan(0, 4).SequenceEqual(Magic))
            throw new InvalidDataException("不是 YunX 备份文件");

        int offset = 4;
        byte version = data[offset++];
        if (version != Version)
            throw new InvalidDataException($"不支持的备份版本：{version}");

        int iterations = BitConverter.ToInt32(data, offset);
        offset += 4;
        // 防篡改：迭代次数异常大时拒绝（避免构造恶意文件导致长时间卡死）
        if (iterations is < 10_000 or > 2_000_000)
            throw new InvalidDataException("备份文件的迭代次数异常");

        byte[] salt = data.AsSpan(offset, SaltSize).ToArray();
        offset += SaltSize;
        byte[] nonce = data.AsSpan(offset, NonceSize).ToArray();
        offset += NonceSize;
        byte[] tag = data.AsSpan(offset, TagSize).ToArray();
        offset += TagSize;
        byte[] cipherText = data.AsSpan(offset).ToArray();

        byte[] key = Pbkdf2_48.Derive(Encoding.UTF8.GetBytes(password), salt,
            iterations, HashAlgorithmName.SHA256, KeySize);
        byte[] plain = new byte[cipherText.Length];
        try
        {
            using var gcm = new AesGcmCompat(key, TagSize);
            gcm.Decrypt(nonce, cipherText, tag, plain);
        }
        catch (CryptographicException)
        {
            // 口令错误（GCM 认证失败）与文件损坏无法区分，统一提示
            throw new InvalidDataException("口令错误，或备份文件已损坏");
        }
        finally
        {
            Crypto48.ZeroMemory(key);
        }

        var payload = JsonSerializer.Deserialize<Payload>(Encoding.UTF8.GetString(plain));
        if (payload == null) throw new InvalidDataException("备份内容无法解析");

        var repo = new CredentialRepository(db, cipher);
        int count = 0;
        foreach (var (platform, combined) in payload.Credentials)
        {
            var parts = combined.Split('\u0001');
            string value = parts[0];
            string extra = parts.Length > 1 ? parts[1] : "";
            repo.Save(platform, value, extra);
            count++;
        }
        return count;
    }
}
