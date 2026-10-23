// =====================================================================================
//  Stream / File / Task 的跨框架补齐
//
//  net48 缺失：
//    · Stream.ReadAsync(Memory<byte>, ct)      → 只有 byte[] 重载
//    · Stream.WriteAsync(ReadOnlyMemory<byte>) → 同上
//    · Stream.ReadExactlyAsync / ReadAtLeastAsync
//    · Task.WaitAsync(TimeSpan|ct)             → net6+
//    · File.ReadAllTextAsync / WriteAllTextAsync / ReadAllBytesAsync
//
//  全部用扩展方法补齐，两个框架都编译 —— net8 上实例方法优先，扩展不会被选中。
// =====================================================================================

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace YunX.Core.Compat
{
    /// <summary>Stream 在 net48 上缺失的重载。</summary>
    internal static class StreamCompatExtensions
    {
        public static Task<int> ReadAsync(this Stream stream, Memory<byte> buffer,
            CancellationToken ct = default)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
#if NET48
            // Memory<byte> → ArraySegment：用 TryGetArray 拿到底层段，避免拷贝。
            if (MemoryMarshal.TryGetArray<byte>(buffer, out ArraySegment<byte> segment))
                return stream.ReadAsync(segment.Array!, segment.Offset, segment.Count, ct);

            // 理论上走不到（Memory<byte> 都是数组支撑的），保险起见退化为拷贝
            var tmp = new byte[buffer.Length];
            return ReadAndCopyAsync(stream, tmp, buffer, ct);
#else
            return stream.ReadAsync(buffer, ct).AsTask();
#endif
        }

#if NET48
        private static async Task<int> ReadAndCopyAsync(Stream stream, byte[] tmp,
            Memory<byte> destination, CancellationToken ct)
        {
            int n = await stream.ReadAsync(tmp, 0, tmp.Length, ct).ConfigureAwait(false);
            tmp.AsMemory(0, n).CopyTo(destination);
            return n;
        }
#endif

        public static Task WriteAsync(this Stream stream, ReadOnlyMemory<byte> buffer,
            CancellationToken ct = default)
        {
            if (stream == null) throw new ArgumentNullException(nameof(stream));
#if NET48
            if (MemoryMarshal.TryGetArray(buffer, out ArraySegment<byte> segment))
                return stream.WriteAsync(segment.Array!, segment.Offset, segment.Count, ct);
            return stream.WriteAsync(buffer.ToArray(), 0, buffer.Length, ct);
#else
            return stream.WriteAsync(buffer, ct).AsTask();
#endif
        }

        /// <summary>等价于 net8 的 <c>Stream.ReadExactlyAsync</c>：读满或抛异常。</summary>
        public static async Task ReadExactlyAsync(this Stream stream, byte[] buffer,
            int offset, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                int n = await stream.ReadAsync(buffer, offset + total, count - total, ct)
                    .ConfigureAwait(false);
                if (n == 0)
                    throw new EndOfStreamException($"预期读 {count} 字节，只读到 {total} 字节。");
                total += n;
            }
        }

        /// <summary>等价于 net8 的 <c>Stream.ReadAtLeastAsync</c>。</summary>
        public static async Task<int> ReadAtLeastAsync(this Stream stream, byte[] buffer,
            int minimumBytes, bool throwOnEndOfStream, CancellationToken ct)
        {
            int total = 0;
            while (total < buffer.Length)
            {
                int n = await stream.ReadAsync(buffer, total, buffer.Length - total, ct)
                    .ConfigureAwait(false);
                if (n == 0)
                {
                    if (total < minimumBytes && throwOnEndOfStream)
                        throw new EndOfStreamException($"预期至少 {minimumBytes} 字节，实际 {total} 字节。");
                    break;
                }
                total += n;
            }
            return total;
        }
    }

    /// <summary>File 的异步便捷方法。</summary>
    internal static class FileCompat
    {
        public static async Task<string> ReadAllTextAsync(string path,
            CancellationToken ct = default)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, useAsync: true);
            using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return await reader.ReadToEndAsync().ConfigureAwait(false);
        }

        public static async Task WriteAllTextAsync(string path, string contents,
            CancellationToken ct = default)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                4096, useAsync: true);
            using var writer = new StreamWriter(fs,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await writer.WriteAsync(contents).ConfigureAwait(false);
        }

        public static async Task<byte[]> ReadAllBytesAsync(string path,
            CancellationToken ct = default)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, useAsync: true);
            long length = fs.Length;
            if (length > int.MaxValue)
                throw new IOException("文件过大，无法一次性读入内存。");
            var buffer = new byte[length];
            int total = 0;
            while (total < buffer.Length)
            {
                int n = await fs.ReadAsync(buffer, total, buffer.Length - total, ct)
                    .ConfigureAwait(false);
                if (n == 0) break;
                total += n;
            }
            if (total != buffer.Length) Array.Resize(ref buffer, total);
            return buffer;
        }

        public static async Task WriteAllBytesAsync(string path, byte[] bytes,
            CancellationToken ct = default)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None,
                4096, useAsync: true);
            await fs.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <c>Task.WaitAsync(TimeSpan)</c> / <c>WaitAsync(ct)</c>（net6+）。
    ///
    /// <para>语义：await 原任务，超时抛 <c>TimeoutException</c>、取消抛
    /// <c>OperationCanceledException</c>，原任务**继续在后台跑**（与 BCL 一致）。</para>
    /// </summary>
    public static class TaskCompat
    {
        public static async Task<T> WaitAsync<T>(this Task<T> task, TimeSpan timeout)
        {
            if (task.IsCompleted) return await task.ConfigureAwait(false);
            using var cts = new CancellationTokenSource(timeout);
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cts.Token.Register(() => tcs.TrySetResult(true)))
            {
                var finished = await Task.WhenAny(task, tcs.Task).ConfigureAwait(false);
                if (finished != task) throw new TimeoutException();
            }
            return await task.ConfigureAwait(false);
        }

        public static async Task<T> WaitAsync<T>(this Task<T> task, CancellationToken ct)
        {
            if (task.IsCompleted || !ct.CanBeCanceled)
                return await task.ConfigureAwait(false);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => tcs.TrySetResult(true)))
            {
                var finished = await Task.WhenAny(task, tcs.Task).ConfigureAwait(false);
                if (finished != task) throw new OperationCanceledException(ct);
            }
            return await task.ConfigureAwait(false);
        }

        public static async Task WaitAsync(this Task task, TimeSpan timeout)
        {
            if (task.IsCompleted) { await task.ConfigureAwait(false); return; }
            using var cts = new CancellationTokenSource(timeout);
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (cts.Token.Register(() => tcs.TrySetResult(true)))
            {
                var finished = await Task.WhenAny(task, tcs.Task).ConfigureAwait(false);
                if (finished != task) throw new TimeoutException();
            }
            await task.ConfigureAwait(false);
        }
    }
}
