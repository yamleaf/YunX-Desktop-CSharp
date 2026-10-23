// =====================================================================================
//  net48 需要的 Win32 P/Invoke 声明
//
//  只声明用到的：CreateFileW / ReadFile / WriteFile /
//  SetFileInformationByHandle / FlushFileBuffers / CloseHandle。
//
//  ★ OVERLAPPED 用 BCL 的 System.Threading.NativeOverlapped，
//    不自己定义 —— Windows 的 OVERLAPPED 是个带 union 的结构，布局稍有偏差
//    就会「写入成功但数据落到错位置」，且完全不报错，极难排查。
//    BCL 已把偏移和 hEvent 的布局处理正确。
// =====================================================================================

#if NET48
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace YunX.Core.Download
{
    internal static class Native
    {
        public const uint GENERIC_READ = 0x80000000;
        public const uint GENERIC_WRITE = 0x40000000;

        public const int ERROR_IO_PENDING = 997;
        public const int ERROR_HANDLE_EOF = 38;

        public const uint FILE_BEGIN = 0;

        /// <summary>
        /// <c>FILE_INFO_BY_HANDLE_CLASS.FileEndOfFileInfo</c>。用于设置文件末尾，
        /// 不依赖文件指针 —— 这是 FILE_FLAG_OVERLAPPED 句柄上唯一可靠的截断方式。
        /// </summary>
        public const int FileEndOfFileInfo = 6;

        /// <summary><c>FILE_END_OF_FILE_INFO</c>：只含一个 LARGE_INTEGER EndOfFile。</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct FILE_END_OF_FILE_INFO
        {
            public long EndOfFile;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true,
            EntryPoint = "CreateFileW", BestFitMapping = false)]
        public static extern SafeFileHandle CreateFileW(
            string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
            uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern unsafe bool ReadFile(
            IntPtr hFile, IntPtr lpBuffer, uint nNumberOfBytesToRead,
            out uint lpNumberOfBytesRead, System.Threading.NativeOverlapped* lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern unsafe bool WriteFile(
            IntPtr hFile, IntPtr lpBuffer, uint nNumberOfBytesToWrite,
            out uint lpNumberOfBytesWritten, System.Threading.NativeOverlapped* lpOverlapped);

        /// <summary>
        /// 设置文件末尾（等价于 SetLength）。
        ///
        /// <para>为什么不用 <c>SetFilePointerEx + SetEndOfFile</c>：在
        /// FILE_FLAG_OVERLAPPED 句柄上 <c>SetFilePointerEx</c> 不生效（Win32 既定行为），
        /// 而 <c>SetEndOfFile</c> 以「当前指针」为准截断 —— 组合起来会把文件截成 0 字节，
        /// 而且不报错。这个 API 直接给出末尾偏移，与指针无关。</para>
        /// </summary>
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern unsafe bool SetFileInformationByHandle(
            IntPtr hFile, int fileInformationClass, void* lpFileInformation, uint dwBufferSize);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern unsafe bool GetOverlappedResult(
            IntPtr hFile, System.Threading.NativeOverlapped* lpOverlapped,
            out uint lpNumberOfBytesTransferred, [MarshalAs(UnmanagedType.Bool)] bool bWait);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool FlushFileBuffers(IntPtr hFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr hObject);

        /// <summary>把 Win32 错误码转成可读文本（避免用户看到「错误 5」不知所以）。</summary>
        public static string ErrorText(int code)
        {
            try
            {
                return new System.ComponentModel.Win32Exception(code).Message;
            }
            catch
            {
                return "未知错误";
            }
        }
    }
}
#endif
