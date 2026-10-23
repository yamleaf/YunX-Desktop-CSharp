#if NET48
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace YunX.Core.Download
{
    /// <summary>
    /// 定位写入 / 定位读取的文件句柄抽象层（net48 实现）。
    ///
    /// <para><b>为什么需要它</b>：多线程分片下载的基石是「多个 worker 同时往同一个文件的
    /// 不同偏移写，互不干扰」。net8 用 <c>RandomAccess.WriteAsync</c> 实现（本质是 pwrite，
    /// 不移动文件指针，也不需要锁）。net48 没有这个 API，只能自己包 Win32
    /// <c>WriteFile</c> + <c>OVERLAPPED</c>：把偏移塞进 OVERLAPPED，同样得到 pwrite 语义。</para>
    ///
    /// <para><b>为什么不用 FileStream.Seek + Write</b>：那需要一把全局锁保护
    /// 「seek 到写」这段临界区，否则两个线程会互相踩偏移。锁一上，8 线程就串行化了。</para>
    ///
    /// <para><b>为什么用同步 WriteFile + Task.Run 而不是真异步</b>：
    /// .NET Framework 上把 OVERLAPPED 完成通知接回 Task 只有两条路 ——
    /// <c>ThreadPool.BindHandle</c>（要求句柄从未同步使用，出错时异常难懂）或
    /// <c>RegisterWaitForSingleObject</c>（每次 IO 都要注册/反注册，开销反而更大）。
    /// 而本项目每个分片是 16~96KB 的连续写，Windows 的文件写几乎都命中页缓存、
    /// 返回时数据已进内核。定位语义（正确性关键）由 OVERLAPPED 保证，
    /// 并发度由 Task.Run 提供。实测 8 线程吞吐与 RandomAccess 完全一致
    /// （8.31 MB/s，见 docs/SIZE-OPTIMIZATION.md 的 PoC）。</para>
    ///
    /// <para><b>OVERLAPPED 怎么传</b>：用 <see cref="NativeOverlapped"/>（BCL 提供的
    /// 非托管可寻址结构，实现 <c>IAsyncResult</c>），通过 <c>stackalloc</c> 分配在栈上，
    /// 调用期间不移动，正好满足 Win32 的要求。不要自己定义 OVERLAPPED 结构体 ——
    /// 它带 union，布局稍有偏差就会「写入成功但数据落到错位置」，且完全静默。</para>
    /// </summary>
    internal sealed class PwriteStream : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private bool _disposed;

        /// <summary>
        /// 串行化同一句柄上的 IO 提交。
        ///
        /// <para><b>为什么必须加这把锁</b>：带 FILE_FLAG_OVERLAPPED 的句柄上，
        /// 多个线程**同时**调 WriteFile 会让内核在同一个句柄上排队多个
        /// OVERLAPPED 请求，而我们的 OVERLAPPED 是 <c>stackalloc</c> 在各自线程
        /// 栈上的 —— 这是合法的，但实测 8 线程并发时会挂死（内核在有多个
        /// pending 请求且 hEvent 为空时依赖 IO 完成端口，而我们没绑定 IOCP）。</para>
        ///
        /// <para>所以这里把「提交 + 等待结果」整段锁住。<b>代价可接受</b>：
        /// 每次写只有 16~96 KB，锁内是两次系统调用（WriteFile +
        /// GetOverlappedResult），没有用户态计算。而且这个锁只串行化了
        /// **同一文件的写入**，网络读取、校验、UI 全部并行不受影响。</para>
        ///
        /// <para>与 net8 的对比：net8 的 RandomAccess 内部也是靠内核的
        /// 定位写（真正的 pwrite），不需要锁。但两者的实际吞吐都远超
        /// 网络带宽（PoC 实测 8.31 MB/s vs 真实需求 1.5 MB/s），
        /// 所以这把锁在真实场景中不可观测。</para>
        /// </summary>
        private readonly object _ioGate = new object();

        private PwriteStream(SafeFileHandle handle) => _handle = handle;

        /// <summary>打开（或创建）一个支持定位读写的文件。</summary>
        public static PwriteStream Open(string path, FileMode mode, FileAccess access, FileShare share)
        {
            // FILE_FLAG_OVERLAPPED 必须设：不设的话带 OVERLAPPED 结构的 WriteFile
            // 会同步执行并**忽略 OVERLAPPED 里的偏移** —— 数据会落到文件指针位置，
            // 也就是所有分片互相覆盖。这是整个方案里最容易踩的坑，
            // 而且它是静默的：写入成功、长度正确、内容全错。
            const uint FILE_FLAG_OVERLAPPED = 0x40000000;

            uint dwAccess = access switch
            {
                FileAccess.Read => Native.GENERIC_READ,
                FileAccess.Write => Native.GENERIC_WRITE,
                _ => Native.GENERIC_READ | Native.GENERIC_WRITE,
            };

            // share 由调用方决定（下载引擎传 FileShare.Read：允许别的进程读 .part，
            // 但不允许它们写，避免两个实例同时下一份文件互相破坏）。
            // 注意：本类自己的 SetLength 已经改用 SetFileInformationByHandle，
            // 不再需要「另开一个可写句柄」这种会被 share 挡住的操作。
            var handle = Native.CreateFileW(path, dwAccess, (uint)share, IntPtr.Zero,
                (uint)mode, FILE_FLAG_OVERLAPPED, IntPtr.Zero);

            if (handle.IsInvalid)
            {
                int err = Marshal.GetLastWin32Error();
                handle.Dispose();
                throw new IOException(
                    $"无法打开文件「{path}」（Win32 错误 {err}：{Native.ErrorText(err)}）", err);
            }

            return new PwriteStream(handle);
        }

        /// <summary>
        /// 把文件长度设成指定值（预分配，避免分片写入时反复扩展文件）。
        ///
        /// <para><b>为什么不用 SetFilePointerEx + SetEndOfFile</b>：带 FILE_FLAG_OVERLAPPED
        /// 的句柄上 <c>SetFilePointerEx</c> 不移动文件指针（Win32 既定行为），
        /// 而 <c>SetEndOfFile</c> 以「当前文件指针」为准截断 —— 组合起来会把文件截成
        /// 0 字节且不报错。表现为「预分配之后所有写入都跑到文件外」。
        /// 因此改用独立通道（FileStream）设置长度。</para>
        /// </summary>
        public unsafe void SetLength(long length)
        {
            ThrowIfDisposed();

            // ★ 不要另开 FileStream 去 SetLength。
            //
            //   最初就是这么写的，结果必然失败：本类的句柄是以
            //   FileShare.Read 打开的（只允许别人读），于是另一个以
            //   FileAccess.Write 打开的 FileStream 会被它自己的共享模式拒绝：
            //       IOException: 文件正由另一进程使用，该进程无法访问此文件
            //   而下载引擎一启动就要 SetLength 预分配 → 整个分片路径挂掉。
            //
            //   即使把共享模式放宽成 FileShare.ReadWrite 绕过去，也只是掩盖问题 ——
            //   两次系统调用之间有窗口，且没必要为了设长度再开一个句柄。
            //
            //   正确做法：直接对现有句柄用 SetFileInformationByHandle
            //   + FileEndOfFileInfo（0x00000006）。这是「设置文件末尾」的原生方式，
            //   **不依赖文件指针**，所以对 FILE_FLAG_OVERLAPPED 句柄同样有效 ——
            //   这正是 SetFilePointerEx + SetEndOfFile 组合做不到的
            //   （前者对 OVERLAPPED 句柄不生效，后者按指针截断 → 会把文件截成 0）。
            var eof = new Native.FILE_END_OF_FILE_INFO { EndOfFile = length };
            if (!Native.SetFileInformationByHandle(_handle.DangerousGetHandle(),
                    Native.FileEndOfFileInfo, &eof, (uint)sizeof(Native.FILE_END_OF_FILE_INFO)))
            {
                int err = Marshal.GetLastWin32Error();
                throw new IOException(
                    $"设置文件长度失败（Win32 错误 {err}：{Native.ErrorText(err)}）", err);
            }
        }

        /// <summary>定位写入。返回时数据已交给内核（不一定已落盘）。</summary>
        public Task WriteAsync(byte[] buffer, int offset, int count, long position,
            CancellationToken ct = default)
        {
            ThrowIfDisposed();
            if (count == 0) return Task.CompletedTask;
            ct.ThrowIfCancellationRequested();

            // 快路径：如果此刻没有别的线程在写（单流下载、或分片但并发度低），
            // 直接在调用线程上同步写，跳过 Task.Run 的排队与线程切换。
            //
            // 为什么这很重要：16~96KB 的写几乎总命中页缓存、几十微秒就返回，
            // 走 Task.Run 反而要付出「入队 + 唤醒工作线程 + 上下文切换」的固定成本，
            // 在单线程下载时是纯亏。而真实下载慢速宽带下单线程 1 MB/s ≈ 每秒 64 次写，
            // 这点开销占比可达个位数百分比，正好能解释基准里 net48 单线程落后 4%。
            //
            // 只有在真有并发争用时才退回线程池，避免阻塞 worker。
            if (Monitor.TryEnter(_ioGate))
            {
                try
                {
                    WriteCoreLocked(buffer, offset, count, position);
                    return Task.CompletedTask;
                }
                catch (Exception e)
                {
                    return Task.FromException(e);
                }
                finally
                {
                    Monitor.Exit(_ioGate);
                }
            }

            // 慢路径：锁被占用（有别的线程正在写），交给线程池等
            return Task.Run(() => WriteCore(buffer, offset, count, position), ct);
        }

        /// <summary>定位读取。读到 EOF 返回 0。</summary>
        public Task<int> ReadAsync(byte[] buffer, int offset, int count, long position,
            CancellationToken ct = default)
        {
            ThrowIfDisposed();
            if (count == 0) return Task.FromResult(0);
            ct.ThrowIfCancellationRequested();

            // 同 WriteAsync：无争用时同步返回，省掉调度开销
            if (Monitor.TryEnter(_ioGate))
            {
                try
                {
                    return Task.FromResult(ReadCoreLocked(buffer, offset, count, position));
                }
                catch (Exception e)
                {
                    return Task.FromException<int>(e);
                }
                finally
                {
                    Monitor.Exit(_ioGate);
                }
            }

            return Task.Run(() => ReadCore(buffer, offset, count, position), ct);
        }

        private void WriteCore(byte[] buffer, int offset, int count, long position)
        {
            lock (_ioGate) WriteCoreLocked(buffer, offset, count, position);
        }

        private unsafe void WriteCoreLocked(byte[] buffer, int offset, int count, long position)
        {
            var ov = new NativeOverlapped
            {
                OffsetLow = (int)(position & 0xFFFFFFFF),
                OffsetHigh = (int)((position >> 32) & 0xFFFFFFFF),
            };

            IntPtr h = _handle.DangerousGetHandle();
            fixed (byte* p = buffer)
            {
                bool ok = Native.WriteFile(h, (IntPtr)(p + offset), (uint)count,
                    out uint written, &ov);

                // ★ ERROR_IO_PENDING 是**正常路径**，不是失败。
                //
                //   带 FILE_FLAG_OVERLAPPED 的句柄上，WriteFile 只负责把 IO 提交给内核，
                //   真正完成与否要看 GetOverlappedResult。第一次跑探针时就栽在这里 ——
                //   把 997（ERROR_IO_PENDING）当成错误抛出，
                //   报错文本还是「重叠的 I/O 操作正在进行中」，看着像并发冲突，
                //   实际是「IO 已排队，等结果」。
                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != Native.ERROR_IO_PENDING)
                        throw new IOException(
                            $"WriteFile 失败（Win32 错误 {err}：{Native.ErrorText(err)}）", err);
                }

                // 两种情况都要走 GetOverlappedResult：
                //   · ok == true  → 同步完成，但 written 拿的是 out 参数，这里再确认一次
                //   · ok == false → 已排队，这会阻塞等到完成
                // bWait: true 表示「等到完成再返回」，正是我们要的语义。
                if (!Native.GetOverlappedResult(h, &ov, out uint transferred, true))
                {
                    int err = Marshal.GetLastWin32Error();
                    throw new IOException(
                        $"WriteFile 完成失败（Win32 错误 {err}：{Native.ErrorText(err)}）", err);
                }

                if (transferred != (uint)count)
                    throw new IOException($"WriteFile 只写入 {transferred}/{count} 字节");
            }
        }

        private int ReadCore(byte[] buffer, int offset, int count, long position)
        {
            lock (_ioGate) return ReadCoreLocked(buffer, offset, count, position);
        }

        private unsafe int ReadCoreLocked(byte[] buffer, int offset, int count, long position)
        {
            var ov = new NativeOverlapped
            {
                OffsetLow = (int)(position & 0xFFFFFFFF),
                OffsetHigh = (int)((position >> 32) & 0xFFFFFFFF),
            };

            IntPtr h = _handle.DangerousGetHandle();
            fixed (byte* p = buffer)
            {
                bool ok = Native.ReadFile(h, (IntPtr)(p + offset), (uint)count,
                    out uint read, &ov);

                if (!ok)
                {
                    int err = Marshal.GetLastWin32Error();
                    // 偏移 >= EOF 时返回 ERROR_HANDLE_EOF —— 这不是错误，是 EOF。
                    if (err == Native.ERROR_HANDLE_EOF) return 0;
                    if (err != Native.ERROR_IO_PENDING)
                        throw new IOException(
                            $"ReadFile 失败（Win32 错误 {err}：{Native.ErrorText(err)}）", err);
                }

                if (!Native.GetOverlappedResult(h, &ov, out uint transferred, true))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == Native.ERROR_HANDLE_EOF) return 0;
                    throw new IOException(
                        $"ReadFile 完成失败（Win32 错误 {err}：{Native.ErrorText(err)}）", err);
                }

                return (int)transferred;
            }
        }

        /// <summary>把页缓存刷到磁盘（对应 RandomAccess.FlushToDisk）。</summary>
        public void FlushToDisk()
        {
            ThrowIfDisposed();
            // FlushFileBuffers 是同步阻塞调用。只在收尾时调一次，可以接受；
            // 若放进每个分片的完成路径会显著拖慢（每次都要等盘）。
            Native.FlushFileBuffers(_handle.DangerousGetHandle());
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _handle.Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(PwriteStream));
        }
    }
}
#endif
