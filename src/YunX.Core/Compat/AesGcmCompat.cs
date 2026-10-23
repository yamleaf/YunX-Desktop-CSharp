// =====================================================================================
//  AES-GCM 的跨框架入口
//
//  System.Security.Cryptography.AesGcm 是 .NET Core 3.0+ 才有的（底层走 BCrypt.dll）。
//  .NET Framework 4.8 **没有**任何托管的 GCM 实现 —— AesManaged 只支持 CBC/ECB/CFB。
//
//  net48 的三条可选路径：
//    1. BouncyCastle（约 3 MB 依赖）→ 直接违背「减体积」的初衷，否决
//    2. 纯 C# 手工实现 GCM → GHASH 与 CTR 都不难，但恒定时间与正确性风险高
//    3. P/Invoke 系统 BCrypt.dll → Windows 自带，零体积代价，且是系统实现
//
//  本项目选 3。BCrypt 的 GCM 接口 Vista+ 就有，Win10/11 必在。
//  net8 分支直接转调 BCL 的 AesGcm —— 零包装成本。
//
//  ★ nonce 绝不能重复。这里不做 nonce 管理，只提供加解密原语
//    —— 调用方（CredentialCipher / AuthBackup）用 RandomNumberGenerator 生成。
// =====================================================================================

using System;
using System.Runtime.InteropServices;

namespace YunX.Core.Compat
{
    /// <summary>
    /// 与 <c>System.Security.Cryptography.AesGcm</c> 同形的 GCM 入口。
    ///
    /// <para>net8 转调 BCL；net48 走 BCrypt。调用点不需要任何条件编译。</para>
    /// </summary>
    internal sealed class AesGcmCompat : IDisposable
    {
        /// <summary>标准 GCM 认证标签长度（字节）。</summary>
        public const int TagSize = 16;

#if NET48
        private readonly byte[] _key;
        private IntPtr _hAlg;
        private IntPtr _hKey;
        private bool _disposed;

        public AesGcmCompat(byte[] key)
            : this(key, TagSize)
        {
        }

        public AesGcmCompat(byte[] key, int tagSizeInBytes)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (tagSizeInBytes <= 0) throw new ArgumentOutOfRangeException(nameof(tagSizeInBytes));
            if (key.Length != 16 && key.Length != 24 && key.Length != 32)
                throw new ArgumentException("AES 密钥长度必须是 16、24 或 32 字节。", nameof(key));

            _key = (byte[])key.Clone();
            TagSizeInBytes = tagSizeInBytes;
            OpenAlgorithm();
        }

        public int TagSizeInBytes { get; }

        private void OpenAlgorithm()
        {
            int status = BCrypt.BCryptOpenAlgorithmProvider(
                out _hAlg, BCrypt.BCRYPT_AES_ALGORITHM, null, 0);
            if (status != 0) Throw(status, "BCryptOpenAlgorithmProvider");

            status = BCrypt.BCryptSetProperty(_hAlg, BCrypt.BCRYPT_CHAINING_MODE,
                BCrypt.BCRYPT_CHAIN_MODE_GCM, (BCrypt.BCRYPT_CHAIN_MODE_GCM.Length + 1) * 2, 0);
            if (status != 0) Throw(status, "BCryptSetProperty(ChainingMode=GCM)");

            status = BCrypt.BCryptGenerateSymmetricKey(_hAlg, out _hKey, IntPtr.Zero, 0,
                _key, _key.Length, 0);
            if (status != 0) Throw(status, "BCryptGenerateSymmetricKey");
        }

        public void Encrypt(byte[] nonce, byte[] plaintext, byte[] ciphertext, byte[] tag,
            byte[]? associatedData = null)
        {
            ThrowIfDisposed();
            if (nonce == null) throw new ArgumentNullException(nameof(nonce));
            if (plaintext == null) throw new ArgumentNullException(nameof(plaintext));
            if (ciphertext == null) throw new ArgumentNullException(nameof(ciphertext));
            if (tag == null) throw new ArgumentNullException(nameof(tag));
            if (ciphertext.Length < plaintext.Length)
                throw new ArgumentException("输出缓冲比明文短。", nameof(ciphertext));
            if (tag.Length != TagSizeInBytes)
                throw new ArgumentException("标签长度不符。", nameof(tag));

            GcmCore(nonce, plaintext, 0, plaintext.Length, ciphertext, tag, associatedData,
                encrypt: true);
        }

        public void Encrypt(byte[] nonce, byte[] plaintext, Span<byte> ciphertext, Span<byte> tag,
            byte[]? associatedData = null)
        {
            // BCrypt 需要 pin 住的连续缓冲，Span 无法直接 pin。
            // 凭证加密是低频路径（每次存取凭据一次），这点拷贝完全可接受。
            ThrowIfDisposed();
            if (ciphertext.Length < plaintext.Length)
                throw new ArgumentException("输出缓冲比明文短。", nameof(ciphertext));

            byte[] ct = new byte[plaintext.Length];
            byte[] tg = new byte[tag.Length];
            GcmCore(nonce, plaintext, 0, plaintext.Length, ct, tg, associatedData, encrypt: true);
            ct.AsSpan().CopyTo(ciphertext);
            tg.AsSpan().CopyTo(tag);
        }

        public void Decrypt(byte[] nonce, byte[] ciphertext, byte[] tag, byte[] plaintext,
            byte[]? associatedData = null)
        {
            ThrowIfDisposed();
            if (nonce == null) throw new ArgumentNullException(nameof(nonce));
            if (ciphertext == null) throw new ArgumentNullException(nameof(ciphertext));
            if (tag == null) throw new ArgumentNullException(nameof(tag));
            if (plaintext == null) throw new ArgumentNullException(nameof(plaintext));
            if (plaintext.Length < ciphertext.Length)
                throw new ArgumentException("输出缓冲比密文短。", nameof(plaintext));

            GcmCore(nonce, ciphertext, 0, ciphertext.Length, plaintext, tag, associatedData,
                encrypt: false);
        }

        public void Decrypt(byte[] nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag,
            Span<byte> plaintext, byte[]? associatedData = null)
        {
            ThrowIfDisposed();
            byte[] ct = ciphertext.ToArray();
            byte[] tg = tag.ToArray();
            byte[] pt = new byte[ct.Length];
            GcmCore(nonce, ct, 0, ct.Length, pt, tg, associatedData, encrypt: false);
            pt.AsSpan().CopyTo(plaintext);
        }

        /// <summary>
        /// BCrypt 的实际调用。
        ///
        /// <para><b>这里曾经踩过一个很难查的坑</b>：最初用
        /// <c>Marshal.AllocHGlobal + WriteInt32(offset, ...)</c> 手工拼
        /// <c>BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO</c>，按 72 字节分配并把
        /// cbSize 也填 72。结果 <c>BCryptEncrypt</c> 返回
        /// <c>NTSTATUS 0xC000000D</c>（STATUS_INVALID_PARAMETER），
        /// 而错误信息完全没提示「结构大小不对」。</para>
        ///
        /// <para>真实大小是 <b>88 字节</b>（x64），中间有 3 处 4 字节对齐填充，
        /// 且 cbSize 必须**精确等于** sizeof(结构)。改用
        /// <see cref="BCryptAuthCipherModeInfo"/> 让 CLR 算布局就不会再错。</para>
        /// </summary>
        private void GcmCore(byte[] nonce, byte[] input, int offset, int count, byte[] output,
            byte[] tag, byte[]? associatedData, bool encrypt)
        {
            IntPtr nonceBuf = IntPtr.Zero;
            IntPtr aadBuf = IntPtr.Zero;
            IntPtr tagBuf = IntPtr.Zero;
            IntPtr inBuf = IntPtr.Zero;
            IntPtr outBuf = IntPtr.Zero;

            try
            {
                nonceBuf = Marshal.AllocHGlobal(nonce.Length);
                Marshal.Copy(nonce, 0, nonceBuf, nonce.Length);

                int aadLen = associatedData?.Length ?? 0;
                if (aadLen > 0)
                {
                    aadBuf = Marshal.AllocHGlobal(aadLen);
                    Marshal.Copy(associatedData!, 0, aadBuf, aadLen);
                }

                tagBuf = Marshal.AllocHGlobal(tag.Length);
                // 解密时把调用方给的 tag 拷进去供内核校验；加密时内核负责写入
                if (!encrypt) Marshal.Copy(tag, 0, tagBuf, tag.Length);

                int bufLen = count == 0 ? 1 : count;
                inBuf = Marshal.AllocHGlobal(bufLen);
                outBuf = Marshal.AllocHGlobal(bufLen);
                if (count > 0) Marshal.Copy(input, offset, inBuf, count);

                var info = new BCryptAuthCipherModeInfo
                {
                    cbSize = Marshal.SizeOf<BCryptAuthCipherModeInfo>(),
                    dwInfoVersion = BCrypt.BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO_VERSION,
                    pbNonce = nonceBuf,
                    cbNonce = nonce.Length,
                    pbAuthData = aadBuf,
                    cbAuthData = aadLen,
                    pbTag = tagBuf,
                    cbTag = tag.Length,
                    pbMacContext = IntPtr.Zero,
                    cbMacContext = 0,
                    cbAAD = 0,
                    cbData = 0,
                    dwFlags = 0,
                };

                int status = encrypt
                    ? BCrypt.BCryptEncrypt(_hKey, inBuf, count, ref info, null, 0,
                        outBuf, count, out int written, 0)
                    : BCrypt.BCryptDecrypt(_hKey, inBuf, count, ref info, null, 0,
                        outBuf, count, out int read, 0);

                if (status != 0)
                {
                    if (!encrypt && status == BCrypt.STATUS_AUTH_TAG_MISMATCH)
                        throw new System.Security.Cryptography.CryptographicException(
                            "认证标签校验失败：数据被篡改或密码错误。");
                    Throw(status, encrypt ? "BCryptEncrypt" : "BCryptDecrypt");
                }

                if (count > 0) Marshal.Copy(outBuf, output, 0, count);
                if (encrypt) Marshal.Copy(tagBuf, tag, 0, tag.Length);
            }
            finally
            {
                if (nonceBuf != IntPtr.Zero) Marshal.FreeHGlobal(nonceBuf);
                if (aadBuf != IntPtr.Zero) Marshal.FreeHGlobal(aadBuf);
                if (tagBuf != IntPtr.Zero) Marshal.FreeHGlobal(tagBuf);
                if (inBuf != IntPtr.Zero) Marshal.FreeHGlobal(inBuf);
                if (outBuf != IntPtr.Zero) Marshal.FreeHGlobal(outBuf);
            }
        }

        private static void Throw(int status, string what)
        {
            throw new System.Security.Cryptography.CryptographicException(
                $"{what} 失败（NTSTATUS 0x{status:X8}）");
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(AesGcmCompat));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_hKey != IntPtr.Zero)
            {
                BCrypt.BCryptDestroyKey(_hKey);
                _hKey = IntPtr.Zero;
            }
            if (_hAlg != IntPtr.Zero)
            {
                BCrypt.BCryptCloseAlgorithmProvider(_hAlg, 0);
                _hAlg = IntPtr.Zero;
            }
        }
#else
        private readonly System.Security.Cryptography.AesGcm _gcm;

        public AesGcmCompat(byte[] key)
            : this(key, TagSize)
        {
        }

        public AesGcmCompat(byte[] key, int tagSizeInBytes)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            _gcm = new System.Security.Cryptography.AesGcm(key, tagSizeInBytes);
            TagSizeInBytes = tagSizeInBytes;
        }

        public int TagSizeInBytes { get; }

        public void Encrypt(byte[] nonce, byte[] plaintext, byte[] ciphertext, byte[] tag,
            byte[]? associatedData = null)
        {
            if (associatedData == null) _gcm.Encrypt(nonce, plaintext, ciphertext, tag);
            else _gcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        }

        public void Encrypt(byte[] nonce, byte[] plaintext, Span<byte> ciphertext, Span<byte> tag,
            byte[]? associatedData = null)
        {
            if (associatedData == null) _gcm.Encrypt(nonce, plaintext, ciphertext, tag);
            else _gcm.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        }

        public void Decrypt(byte[] nonce, byte[] ciphertext, byte[] tag, byte[] plaintext,
            byte[]? associatedData = null)
        {
            if (associatedData == null) _gcm.Decrypt(nonce, ciphertext, tag, plaintext);
            else _gcm.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        }

        public void Decrypt(byte[] nonce, ReadOnlySpan<byte> ciphertext, ReadOnlySpan<byte> tag,
            Span<byte> plaintext, byte[]? associatedData = null)
        {
            if (associatedData == null) _gcm.Decrypt(nonce, ciphertext, tag, plaintext);
            else _gcm.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        }

        public void Dispose() => _gcm.Dispose();
#endif
    }

#if NET48
    /// <summary>
    /// BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO 的托管映射。
    ///
    /// <para><b>必须用真实结构体而不是手工偏移</b>：x64 上它是 88 字节，
    /// 中间有 3 处 4 字节对齐填充，且 cbSize 必须精确等于 sizeof(结构)。
    /// 写成 72 会让 BCryptEncrypt 返回 STATUS_INVALID_PARAMETER，
    /// 报错信息不会提「大小不对」。</para>
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct BCryptAuthCipherModeInfo
    {
        public int cbSize;
        public int dwInfoVersion;
        public IntPtr pbNonce;
        public int cbNonce;
        public IntPtr pbAuthData;
        public int cbAuthData;
        public IntPtr pbTag;
        public int cbTag;
        public IntPtr pbMacContext;
        public int cbMacContext;
        public int cbAAD;
        public long cbData;
        public int dwFlags;
    }

    /// <summary>bcrypt.dll 的最小 P/Invoke 面。</summary>
    internal static class BCrypt
    {
        public const string BCRYPT_AES_ALGORITHM = "AES";
        public const string BCRYPT_CHAINING_MODE = "ChainingMode";
        public const string BCRYPT_CHAIN_MODE_GCM = "ChainingModeGCM";

        public const int STATUS_AUTH_TAG_MISMATCH = unchecked((int)0xC000A002);

        /// <summary>BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO 的当前版本号。</summary>
        public const int BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO_VERSION = 1;

        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        public static extern int BCryptOpenAlgorithmProvider(
            out IntPtr phAlgorithm, string pszAlgId, string? pszImplementation, int dwFlags);

        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        public static extern int BCryptSetProperty(
            IntPtr hObject, string pszProperty, string pbInput, int cbInput, int dwFlags);

        [DllImport("bcrypt.dll")]
        public static extern int BCryptGenerateSymmetricKey(
            IntPtr hAlgorithm, out IntPtr phKey, IntPtr pbKeyObject, int cbKeyObject,
            byte[] pbSecret, int cbSecret, int dwFlags);

        [DllImport("bcrypt.dll")]
        public static extern int BCryptEncrypt(
            IntPtr hKey, IntPtr pbInput, int cbInput, ref BCryptAuthCipherModeInfo pPaddingInfo,
            byte[]? pbIV, int cbIV, IntPtr pbOutput, int cbOutput, out int pcbResult, int dwFlags);

        [DllImport("bcrypt.dll")]
        public static extern int BCryptDecrypt(
            IntPtr hKey, IntPtr pbInput, int cbInput, ref BCryptAuthCipherModeInfo pPaddingInfo,
            byte[]? pbIV, int cbIV, IntPtr pbOutput, int cbOutput, out int pcbResult, int dwFlags);

        [DllImport("bcrypt.dll")]
        public static extern int BCryptDestroyKey(IntPtr hKey);

        [DllImport("bcrypt.dll")]
        public static extern int BCryptCloseAlgorithmProvider(IntPtr hAlgorithm, int dwFlags);
    }
#endif
}
