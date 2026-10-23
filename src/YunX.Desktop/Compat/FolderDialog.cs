#if NET48
// =====================================================================================
//  net48 的「选择文件夹」对话框
//
//  Microsoft.Win32.OpenFolderDialog 是 .NET 8 才加入 WPF 的（之前只能在
//  WinForms 里用 FolderBrowserDialog，或自己调 IFileOpenDialog COM 接口）。
//
//  net48 有三条路：
//    1. WinForms 的 FolderBrowserDialog → 需要引用 System.Windows.Forms（12MB 依赖），
//       而且那是个树形老式界面，用户体验差
//    2. 调 SHBrowseForFolder → 同样是老式树形（BIF_NEWDIALOGSTYLE 稍好）
//    3. 调 Vista+ 的 IFileOpenDialog 并设 FOS_PICKFOLDERS → 就是资源管理器那套
//       现代界面，与 net8 的 OpenFolderDialog 完全一致
//
//  选 3。COM 互操作代码稍长，但零依赖、零体积代价，且界面一致。
// =====================================================================================

using System;
using System.Runtime.InteropServices;

namespace YunX.Desktop.Compat
{
    /// <summary>
    /// 文件夹选择对话框（net48 版）。
    ///
    /// <para>用法与 net8 的 <c>OpenFolderDialog</c> 对齐：
    /// <c>new FolderDialog { Title = "...", InitialDirectory = "..." }</c>，
    /// <c>ShowDialog() == true</c> 时读 <see cref="FolderName"/>。</para>
    /// </summary>
    internal sealed class FolderDialog
    {
        public string Title { get; set; } = "";
        public string InitialDirectory { get; set; } = "";
        public string FolderName { get; private set; } = "";

        public bool ShowDialog()
        {
            var dlg = (IFileOpenDialog)new FileOpenDialog();
            try
            {
                dlg.GetOptions(out uint options);
                options |= FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM | FOS_PATHMUSTEXIST;
                dlg.SetOptions(options);

                if (!string.IsNullOrEmpty(Title)) dlg.SetTitle(Title);
                if (!string.IsNullOrEmpty(InitialDirectory))
                {
                    try
                    {
                        var iid = typeof(IShellItem).GUID;
                        if (SHCreateItemFromParsingName(InitialDirectory, IntPtr.Zero,
                                ref iid, out IShellItem folder) == 0)
                        {
                            dlg.SetFolder(folder);
                            Marshal.ReleaseComObject(folder);
                        }
                    }
                    catch
                    {
                        // 初始目录不存在时忽略，让对话框用默认位置
                    }
                }

                int hr = dlg.Show(IntPtr.Zero);
                // 用户取消返回 HRESULT 0x800704C7（ERROR_CANCELLED）
                if (hr != 0) return false;

                dlg.GetResult(out IShellItem item);
                try
                {
                    item.GetDisplayName(SIGDN_FILESYSPATH, out IntPtr pszPath);
                    try
                    {
                        FolderName = Marshal.PtrToStringUni(pszPath) ?? "";
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(pszPath);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(item);
                }

                return !string.IsNullOrEmpty(FolderName);
            }
            catch
            {
                return false;
            }
            finally
            {
                Marshal.ReleaseComObject(dlg);
            }
        }

        // ---------------------------------------------------------------- COM 声明

        private const uint FOS_PICKFOLDERS = 0x00000020;
        private const uint FOS_FORCEFILESYSTEM = 0x00000040;
        private const uint FOS_PATHMUSTEXIST = 0x00000800;
        private const uint SIGDN_FILESYSPATH = 0x80058000;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath, IntPtr pbc,
            ref Guid riid, out IShellItem ppv);

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialog
        {
        }

        [ComImport, Guid("42f85136-db7e-439c-85f1-e4075d135fc8"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            // IModalWindow
            [PreserveSig]
            int Show(IntPtr parent);

            // IFileDialog
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);

            // IFileOpenDialog 特有
            void GetResults(out IntPtr ppenum);
            void GetSelectedItems(out IntPtr ppsai);
        }

        [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }
    }
}
#endif
