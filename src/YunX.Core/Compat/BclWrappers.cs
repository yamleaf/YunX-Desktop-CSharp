// =====================================================================================
//  BCL 便捷 API 的跨框架薄包装
//
//  背景：net8 有一批 net48 没有的 BCL 便捷方法（Math.Clamp / Convert.ToHexString /
//  MD5.HashData / RandomNumberGenerator.GetBytes(int) / HttpContent.ReadAsStringAsync(ct)…）。
//  它们是**静态方法**或**实例重载**，无法用 using 别名统一。
//
//  架构决策：**两个框架都编译这个文件**，net8 分支直接转调 BCL（零开销，JIT 内联），
//  net48 分支手写等价实现。好处是业务代码完全不需要 #if —— 只有这一层知道
//  两个框架的差异。这是这次迁移能收敛住的关键。
//
//  ★ 唯一要守住的原则：net8 分支不要写「比 BCL 更慢」的实现，一律转调。
// =====================================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace YunX.Core.Compat
{
    /// <summary><c>Math.Clamp</c> 的统一入口（net8 转调 BCL）。</summary>
    internal static class Math48
    {
        public static int Clamp(int value, int min, int max)
        {
#if NET48
            if (min > max) throw new ArgumentException($"'{min}' 不能大于 '{max}'。");
            if (value < min) return min;
            if (value > max) return max;
            return value;
#else
            return Math.Clamp(value, min, max);
#endif
        }

        public static long Clamp(long value, long min, long max)
        {
#if NET48
            if (min > max) throw new ArgumentException($"'{min}' 不能大于 '{max}'。");
            if (value < min) return min;
            if (value > max) return max;
            return value;
#else
            return Math.Clamp(value, min, max);
#endif
        }

        public static double Clamp(double value, double min, double max)
        {
#if NET48
            if (min > max) throw new ArgumentException($"'{min}' 不能大于 '{max}'。");
            if (double.IsNaN(value)) return value;
            if (value < min) return min;
            if (value > max) return max;
            return value;
#else
            return Math.Clamp(value, min, max);
#endif
        }

        public static float Clamp(float value, float min, float max)
        {
#if NET48
            if (min > max) throw new ArgumentException($"'{min}' 不能大于 '{max}'。");
            if (float.IsNaN(value)) return value;
            if (value < min) return min;
            if (value > max) return max;
            return value;
#else
            return Math.Clamp(value, min, max);
#endif
        }
    }

    /// <summary><c>Convert.ToHexString</c> 的统一入口。</summary>
    internal static class Helpers
    {
        private const string HexDigits = "0123456789ABCDEF";

        /// <summary>大写、无分隔符，与 <c>Convert.ToHexString(byte[])</c> 一致。</summary>
        public static string ToHexString(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
#if NET48
            return ToHexString(bytes, 0, bytes.Length);
#else
            return Convert.ToHexString(bytes);
#endif
        }

        public static string ToHexString(byte[] bytes, int offset, int length)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (offset < 0 || length < 0 || offset + length > bytes.Length)
                throw new ArgumentOutOfRangeException(nameof(offset));
#if NET48
            // 手工查表比 BitConverter.ToString().Replace("-","") 快 3~5 倍，
            // 且不产生 2 倍中间字符串。分片校验会频繁调用。
            var chars = new char[length * 2];
            for (int i = 0; i < length; i++)
            {
                byte b = bytes[offset + i];
                chars[i * 2] = HexDigits[b >> 4];
                chars[i * 2 + 1] = HexDigits[b & 0xF];
            }
            return new string(chars);
#else
            return Convert.ToHexString(bytes, offset, length);
#endif
        }
    }

    /// <summary><c>MD5/SHA1/SHA256/SHA512.HashData</c> 的统一入口。</summary>
    internal static class Hash48
    {
        public static byte[] Md5(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
#if NET48
            using (var alg = MD5.Create()) return alg.ComputeHash(data);
#else
            return MD5.HashData(data);
#endif
        }

        public static byte[] Sha1(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
#if NET48
            using (var alg = SHA1.Create()) return alg.ComputeHash(data);
#else
            return SHA1.HashData(data);
#endif
        }

        public static byte[] Sha256(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
#if NET48
            using (var alg = SHA256.Create()) return alg.ComputeHash(data);
#else
            return SHA256.HashData(data);
#endif
        }

        public static byte[] Sha512(byte[] data)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
#if NET48
            using (var alg = SHA512.Create()) return alg.ComputeHash(data);
#else
            return SHA512.HashData(data);
#endif
        }

        public static byte[] Sha256(ReadOnlySpan<byte> data)
        {
#if NET48
            // net48 的 ComputeHash 没有 Span 重载，先 ToArray。
            // 调用点都是 16KB 级的小缓冲（分片首尾哨兵），拷贝可忽略。
            using (var alg = SHA256.Create()) return alg.ComputeHash(data.ToArray());
#else
            return SHA256.HashData(data);
#endif
        }

        public static byte[] Sha1(ReadOnlySpan<byte> data)
        {
#if NET48
            using (var alg = SHA1.Create()) return alg.ComputeHash(data.ToArray());
#else
            return SHA1.HashData(data);
#endif
        }
    }

    /// <summary><c>RandomNumberGenerator</c> 静态便捷方法的统一入口。</summary>
    internal static class Rng48
    {
        public static byte[] GetBytes(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
#if NET48
            var buffer = new byte[count];
            if (count == 0) return buffer;
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(buffer);
            return buffer;
#else
            return RandomNumberGenerator.GetBytes(count);
#endif
        }

        public static int GetInt32(int toExclusive)
        {
            if (toExclusive <= 0) throw new ArgumentOutOfRangeException(nameof(toExclusive));
#if NET48
            return GetInt32Core(0, toExclusive);
#else
            return RandomNumberGenerator.GetInt32(toExclusive);
#endif
        }

        public static int GetInt32(int fromInclusive, int toExclusive)
        {
            if (fromInclusive >= toExclusive) throw new ArgumentException("上界必须大于下界。");
#if NET48
            return GetInt32Core(fromInclusive, toExclusive);
#else
            return RandomNumberGenerator.GetInt32(fromInclusive, toExclusive);
#endif
        }

#if NET48
        /// <summary>
        /// 拒绝采样，保证均匀分布。
        /// 简单取模会引入模偏差 —— 对 nonce / 随机 Hex 这类用途不可接受。
        /// </summary>
        private static int GetInt32Core(int fromInclusive, int toExclusive)
        {
            uint range = (uint)(toExclusive - fromInclusive) - 1;
            if (range == 0) return fromInclusive;

            uint mask = range;
            mask |= mask >> 1; mask |= mask >> 2; mask |= mask >> 4;
            mask |= mask >> 8; mask |= mask >> 16;

            var buf = new byte[4];
            uint result;
            using (var rng = RandomNumberGenerator.Create())
            {
                do
                {
                    rng.GetBytes(buf);
                    result = BitConverter.ToUInt32(buf, 0) & mask;
                } while (result > range);
            }
            return (int)(result + (uint)fromInclusive);
        }
#endif

        public static void Fill(Span<byte> buffer)
        {
            if (buffer.Length == 0) return;
#if NET48
            var tmp = new byte[buffer.Length];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(tmp);
            tmp.AsSpan().CopyTo(buffer);
#else
            RandomNumberGenerator.Fill(buffer);
#endif
        }
    }

    /// <summary>
    /// <c>Rfc2898DeriveBytes.Pbkdf2</c> 静态重载（net6+）的统一入口。
    ///
    /// <para>net48 只有构造函数写法，且**默认 PRF 是 SHA-1**。这里显式传
    /// <c>HashAlgorithmName</c> 保证与 net8 结果一致 —— 否则同一密码在两个框架下
    /// 派生出的密钥不同，备份文件互相打不开。</para>
    /// </summary>
    internal static class Pbkdf2_48
    {
        public static byte[] Derive(
            byte[] password, byte[] salt, int iterations, HashAlgorithmName hashAlgorithm, int outputLength)
        {
            if (password == null) throw new ArgumentNullException(nameof(password));
            if (salt == null) throw new ArgumentNullException(nameof(salt));
            if (iterations <= 0) throw new ArgumentOutOfRangeException(nameof(iterations));
            if (outputLength <= 0) throw new ArgumentOutOfRangeException(nameof(outputLength));
#if NET48
            using (var kdf = new Rfc2898DeriveBytes(password, salt, iterations, hashAlgorithm))
            {
                return kdf.GetBytes(outputLength);
            }
#else
            return Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, hashAlgorithm, outputLength);
#endif
        }
    }

    /// <summary><c>CryptographicOperations</c> 的统一入口。</summary>
    internal static class Crypto48
    {
        public static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left == null || right == null) return false;
#if NET48
            if (left.Length != right.Length) return false;
            int diff = 0;
            for (int i = 0; i < left.Length; i++) diff |= left[i] ^ right[i];
            return diff == 0;
#else
            return CryptographicOperations.FixedTimeEquals(left, right);
#endif
        }

        public static bool FixedTimeEquals(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
        {
#if NET48
            if (left.Length != right.Length) return false;
            int diff = 0;
            for (int i = 0; i < left.Length; i++) diff |= left[i] ^ right[i];
            return diff == 0;
#else
            return CryptographicOperations.FixedTimeEquals(left, right);
#endif
        }

        public static void ZeroMemory(Span<byte> buffer)
        {
#if NET48
            // net48 没有 CryptographicOperations.ZeroMemory，
            // 但手写循环不会被 JIT 优化掉（与 Span.Clear 不同），效果等价。
            for (int i = 0; i < buffer.Length; i++) buffer[i] = 0;
#else
            CryptographicOperations.ZeroMemory(buffer);
#endif
        }
    }

    /// <summary><c>KeyValuePair&lt;K,V&gt;.Deconstruct</c>（C# 8 解构语法依赖）。</summary>
    internal static class KeyValuePairExtensions
    {
        public static void Deconstruct<TKey, TValue>(
            this KeyValuePair<TKey, TValue> pair, out TKey key, out TValue value)
        {
            key = pair.Key;
            value = pair.Value;
        }
    }

    /// <summary>
    /// <c>HttpContent.ReadAsStringAsync(ct)</c> 等带取消令牌的重载。
    ///
    /// <para>net48 上这些方法**没有 ct 参数** → 扩展方法被选中；
    /// net8 上有实例重载 → 实例优先，扩展不会被调用。两框架都定义是安全的。</para>
    /// </summary>
    internal static class HttpContentExtensions48
    {
        public static async Task<string> ReadAsStringAsync(
            this HttpContent content, CancellationToken ct)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            ct.ThrowIfCancellationRequested();
#if NET48
            return await WithCancellation(content.ReadAsStringAsync(), ct).ConfigureAwait(false);
#else
            return await content.ReadAsStringAsync(ct).ConfigureAwait(false);
#endif
        }

        public static async Task<Stream> ReadAsStreamAsync(
            this HttpContent content, CancellationToken ct)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            ct.ThrowIfCancellationRequested();
#if NET48
            return await WithCancellation(content.ReadAsStreamAsync(), ct).ConfigureAwait(false);
#else
            return await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
#endif
        }

        public static async Task<byte[]> ReadAsByteArrayAsync(
            this HttpContent content, CancellationToken ct)
        {
            if (content == null) throw new ArgumentNullException(nameof(content));
            ct.ThrowIfCancellationRequested();
#if NET48
            return await WithCancellation(content.ReadAsByteArrayAsync(), ct).ConfigureAwait(false);
#else
            return await content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
#endif
        }

#if NET48
        /// <summary>
        /// net48 的网络读没法真正中断（HttpClient 内部不接 ct），
        /// 但至少让调用方能及时返回、不无限挂着。
        /// </summary>
        private static async Task<T> WithCancellation<T>(Task<T> task, CancellationToken ct)
        {
            if (!ct.CanBeCanceled) return await task.ConfigureAwait(false);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => tcs.TrySetResult(true)))
            {
                var finished = await Task.WhenAny(task, tcs.Task).ConfigureAwait(false);
                if (finished != task) throw new OperationCanceledException(ct);
            }
            return await task.ConfigureAwait(false);
        }
#endif
    }

    /// <summary>跨框架的常量与共享实例。</summary>
    internal static class Net48Constants
    {
        /// <summary>等价于 net8 的 <c>DecompressionMethods.All</c>。</summary>
#if NET48
        public const System.Net.DecompressionMethods DecompressionAll =
            System.Net.DecompressionMethods.GZip |
            System.Net.DecompressionMethods.Deflate |
            (System.Net.DecompressionMethods)4;   // Brotli：net48 枚举里没有具名成员
#else
        public const System.Net.DecompressionMethods DecompressionAll =
            System.Net.DecompressionMethods.All;
#endif

        /// <summary>
        /// <c>Random.Shared</c> 的替代。两框架统一走这个 ——
        /// net48 的 Random 实例在多线程下会返回全 0（内部状态被并发破坏），必须加锁。
        /// </summary>
        public static readonly ThreadSafeRandom SharedRandom = new ThreadSafeRandom();
    }

    /// <summary>线程安全的 Random。</summary>
    internal sealed class ThreadSafeRandom
    {
#if NET48
        private readonly Random _random = new Random(Guid.NewGuid().GetHashCode());
        private readonly object _gate = new object();

        public int Next(int maxValue) { lock (_gate) return _random.Next(maxValue); }
        public int Next(int minValue, int maxValue) { lock (_gate) return _random.Next(minValue, maxValue); }
        public double NextDouble() { lock (_gate) return _random.NextDouble(); }
#else
        public int Next(int maxValue) => Random.Shared.Next(maxValue);
        public int Next(int minValue, int maxValue) => Random.Shared.Next(minValue, maxValue);
        public double NextDouble() => Random.Shared.NextDouble();
#endif
    }
}
