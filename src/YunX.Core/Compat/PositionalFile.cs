using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace YunX.Core.Download
{
    /// <summary>
    /// 定位写入 / 定位读取的文件句柄抽象层。
    ///
    /// <para>两个目标框架走完全不同的实现，对调用方是同一套签名：</para>
    /// <list type="bullet">
    /// <item><b>net8</b>：直接用 BCL 的 <see cref="RandomAccess"/>，零包装、零开销。</item>
    /// <item><b>net48</b>：用 <see cref="PwriteStream"/>（WriteFile + OVERLAPPED）。</item>
    /// </list>
    ///
    /// <para><b>为什么不抽成接口</b>：加一层虚分发在热路径上（8 线程 × 每 16KB 一次）
    /// 是白花的代价。这里用 <c>#if</c> 在同一方法里分两个实现体，编译后各自是直接调用。</para>
    /// </summary>
    internal sealed class PositionalFile : IDisposable
    {
#if NET48
        private readonly PwriteStream _stream;

        private PositionalFile(PwriteStream stream) => _stream = stream;
#else
        private readonly Microsoft.Win32.SafeHandles.SafeFileHandle _handle;

        private PositionalFile(Microsoft.Win32.SafeHandles.SafeFileHandle handle) => _handle = handle;
#endif

        /// <summary>
        /// 打开目标文件，返回支持定位读写的句柄。
        ///
        /// <para><paramref name="createNew"/> 为 true 时截断重写（FileMode.Create），
        /// false 时保留已有内容（FileMode.OpenOrCreate）—— 后者用于断点续传，
        /// 已有分片数据必须留着。</para>
        /// </summary>
        public static PositionalFile Open(string path, bool createNew)
        {
#if NET48
            return new PositionalFile(PwriteStream.Open(path,
                createNew ? FileMode.Create : FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.Read));
#else
            var handle = File.OpenHandle(path,
                createNew ? FileMode.Create : FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read,
                // FileOptions.Asynchronous 是必需的：不带它时 RandomAccess.WriteAsync
                // 会退回线程池同步写，多 worker 并发下线程池排队会拖慢整体吞吐。
                FileOptions.Asynchronous);
            return new PositionalFile(handle);
#endif
        }

        /// <summary>预分配到指定长度，后续分片写入不会触发文件扩展。</summary>
        public void SetLength(long length)
        {
#if NET48
            _stream.SetLength(length);
#else
            RandomAccess.SetLength(_handle, length);
#endif
        }

        /// <summary>定位写入。多个线程可并发调用，偏移各自独立（pwrite 语义）。</summary>
        public Task WriteAsync(byte[] buffer, int offset, int count, long position, CancellationToken ct)
        {
#if NET48
            return _stream.WriteAsync(buffer, offset, count, position, ct);
#else
            return RandomAccess.WriteAsync(_handle,
                new ReadOnlyMemory<byte>(buffer, offset, count), position, ct).AsTask();
#endif
        }

        /// <summary>定位读取。读到 EOF 返回 0。</summary>
        public Task<int> ReadAsync(byte[] buffer, int offset, int count, long position, CancellationToken ct)
        {
#if NET48
            return _stream.ReadAsync(buffer, offset, count, position, ct);
#else
            // net8 的 RandomAccess.ReadAsync 返回 ValueTask<int>，
            // 包成 Task<int> 与 net48 分支保持同一签名，调用方不用写两套 await。
            return ReadCoreAsync(_handle, buffer, offset, count, position, ct);
#endif
        }

#if !NET48
        private static async Task<int> ReadCoreAsync(
            Microsoft.Win32.SafeHandles.SafeFileHandle handle,
            byte[] buffer, int offset, int count, long position, CancellationToken ct)
        {
            return await RandomAccess.ReadAsync(handle,
                new Memory<byte>(buffer, offset, count), position, ct).ConfigureAwait(false);
        }
#endif

        /// <summary>把页缓存刷到磁盘。只在收尾时调一次。</summary>
        public void FlushToDisk()
        {
#if NET48
            _stream.FlushToDisk();
#else
            RandomAccess.FlushToDisk(_handle);
#endif
        }

        public void Dispose()
        {
#if NET48
            _stream.Dispose();
#else
            _handle.Dispose();
#endif
        }
    }
}
