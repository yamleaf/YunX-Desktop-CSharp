// =====================================================================================
//  进程与哈希的跨框架补齐
//
//  net48 缺失：
//    · Process.Kill(entireProcessTree: true)   → net5+
//    · ProcessStartInfo.ArgumentList           → netcore 2.1+
//    · HashAlgorithm.ComputeHashAsync(Stream)  → net5+
// =====================================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace YunX.Core.Compat
{
    internal static class Process48
    {
        /// <summary>
        /// 杀掉整个进程树。
        ///
        /// <para>net48 没有 <c>Kill(entireProcessTree: true)</c>，用系统自带的
        /// <c>taskkill /T /F</c> —— 它的语义就是「杀进程树」，比手工枚举子进程可靠。
        /// 这是 Gopeed 内核（会 fork 下载子进程）必须的能力：只杀主进程会留下孤儿进程
        /// 继续占着端口和文件。</para>
        /// </summary>
        public static void KillTree(Process? process)
        {
            if (process == null) return;
            try
            {
                if (process.HasExited) return;
            }
            catch
            {
                return;
            }

#if NET48
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/T /F /PID {process.Id}",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                using var killer = Process.Start(psi);
                killer?.WaitForExit(5000);
                return;
            }
            catch
            {
                // taskkill 不可用时退化为只杀主进程
            }
#endif
            try { process.Kill(); } catch { /* 已退出或权限不足，忽略 */ }
        }

        /// <summary>
        /// 按 Windows <c>CommandLineToArgvW</c> 规则把参数数组拼成命令行字符串。
        ///
        /// <para>规则：含空格/制表符/引号的参数整体加双引号；参数内紧邻引号的反斜杠要加倍；
        /// 末尾的反斜杠也要加倍（否则会把结尾引号转义掉）。这段细节错了会导致
        /// 带空格路径（如 <c>C:/Program Files\...</c>）被截断成两个参数。</para>
        /// </summary>
        public static string BuildArgumentString(IEnumerable<string> args)
        {
            var sb = new StringBuilder();
            foreach (var arg in args)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(QuoteArgument(arg));
            }
            return sb.ToString();
        }

        private static string QuoteArgument(string? arg)
        {
            if (string.IsNullOrEmpty(arg)) return "\"\"";
            if (arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return arg;

            var sb = new StringBuilder();
            sb.Append('"');
            for (int i = 0; i < arg.Length; i++)
            {
                int backslashes = 0;
                while (i < arg.Length && arg[i] == '\\') { backslashes++; i++; }

                if (i == arg.Length)
                {
                    // 末尾反斜杠：加倍，避免转义掉收尾引号
                    sb.Append('\\', backslashes * 2);
                    break;
                }

                if (arg[i] == '"')
                {
                    sb.Append('\\', backslashes * 2 + 1);
                    sb.Append('"');
                }
                else
                {
                    sb.Append('\\', backslashes);
                    sb.Append(arg[i]);
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }

    /// <summary><c>HashAlgorithm.ComputeHashAsync</c>（net5+）。</summary>
    internal static class HashCompat
    {
        public static async Task<byte[]> ComputeHashAsync(HashAlgorithm alg, Stream stream,
            CancellationToken ct = default)
        {
            if (alg == null) throw new ArgumentNullException(nameof(alg));
            if (stream == null) throw new ArgumentNullException(nameof(stream));
#if NET48
            // net48 没有增量式异步哈希入口，用 TransformBlock 手工分块。
            // 96KB 缓冲：既不让 LOH 压力太大，也不让循环太碎。
            const int BufferSize = 96 * 1024;
            var buffer = new byte[BufferSize];
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, ct)
                       .ConfigureAwait(false)) > 0)
            {
                alg.TransformBlock(buffer, 0, read, null, 0);
            }
            alg.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return alg.Hash!;
#else
            return await alg.ComputeHashAsync(stream, ct).ConfigureAwait(false);
#endif
        }
    }

    /// <summary>net8 的 <c>ObjectDisposedException.ThrowIf</c> 替代。</summary>
    internal static class CompatGuard
    {
        public static void ThrowIfDisposed(bool condition, object instance)
        {
            if (condition) throw new ObjectDisposedException(instance?.GetType().Name);
        }
    }

    /// <summary>
    /// <c>Aes.EncryptCbc</c> / <c>Aes.DecryptCbc</c>（net6+ 的便捷重载）。
    ///
    /// <para>net48 只能用 <c>CryptoStream</c> 手工包装。这里保持与 BCL 相同的
    /// PKCS7 填充与「IV 由调用方提供」的语义。</para>
    /// </summary>
    internal static class AesCbcCompat
    {
        public static byte[] EncryptCbc(byte[] key, byte[] iv, byte[] plaintext)
            => Run(key, iv, plaintext, PaddingMode.PKCS7, encrypt: true);

        public static byte[] DecryptCbc(byte[] key, byte[] iv, byte[] ciphertext,
            PaddingMode padding = PaddingMode.PKCS7)
            => Run(key, iv, ciphertext, padding, encrypt: false);

        private static byte[] Run(byte[] key, byte[] iv, byte[] input, PaddingMode padding,
            bool encrypt)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (iv == null) throw new ArgumentNullException(nameof(iv));
            if (input == null) throw new ArgumentNullException(nameof(input));

#if NET48
            using var aes = Aes.Create();
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = padding;

            using var transform = encrypt ? aes.CreateEncryptor() : aes.CreateDecryptor();
            using var output = new MemoryStream();
            using (var cs = new CryptoStream(output, transform, CryptoStreamMode.Write))
            {
                cs.Write(input, 0, input.Length);
                cs.FlushFinalBlock();
            }
            return output.ToArray();
#else
            using var aes = Aes.Create();
            aes.Key = key;
            return encrypt
                ? aes.EncryptCbc(input, iv, padding)
                : aes.DecryptCbc(input, iv, padding);
#endif
        }
    }
}
