using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace YunX.Desktop;

/// <summary>
/// 原生/磁盘依赖引导：让「单文件 exe」也能正常使用 <c>System.Data.SQLite</c>。
///
/// <para><b>为什么 SQLite 不能像别的依赖那样从内存加载</b></para>
///
/// <para>Costura 能把托管 dll 从资源里直接加载，绝大多数库都吃这套。但
/// <c>System.Data.SQLite</c> 有两处硬性要求磁盘路径，缺一不可：</para>
///
/// <list type="number">
/// <item>
/// <b>它要算自己的目录</b>：内部用
/// <c>Path.Combine(Assembly.GetExecutingAssembly().Location 所在目录, ...)</c>。
/// 从内存资源加载时 <c>Location</c> 是空串，直接抛
/// <c>ArgumentNullException（值不能为 null，参数名: path1）</c> ——
/// 栈里只看到 <c>SQLiteConnection..ctor</c>，完全联想不到是"加载方式"的问题。
/// </item>
/// <item>
/// <b>原生库路径是它自己拼的</b>：<c>&lt;程序集目录&gt;\x64\SQLite.Interop.dll</c>，
/// 拼完直接 <c>LoadLibrary</c>。所以把目录加进 PATH / AddDllDirectory **没有用** ——
/// 它压根不去搜索路径里找。（实测确认：加了也没用，仍然 0x8007007E。）
/// </item>
/// </list>
///
/// <para><b>做法</b>：把这两个文件都嵌进 exe，启动最早期解压到一个稳定的缓存目录：</para>
/// <code>
/// %LOCALAPPDATA%\YunX-Desktop-CS\native\&lt;内容哈希&gt;\
///     System.Data.SQLite.dll
///     x64\SQLite.Interop.dll
/// </code>
/// <para>然后 <c>Assembly.LoadFrom</c> 从那里加载 SQLite.dll（于是 <c>Location</c>
/// 有真实值、原生库就在它旁边的 x64\ 里），再预加载原生 dll 双保险。</para>
///
/// <para><b>为什么目录名用内容哈希</b>：换了构建就自动换目录，不会出现
/// 「新 exe 用到旧 dll」这种极难查的问题。旧目录顺手清理，不影响正确性。</para>
/// </summary>
internal static class NativeBootstrapper
{
    /// <summary>托管程序集（有 Location 要求，必须落盘）。</summary>
    private const string ManagedDllName = "System.Data.SQLite.dll";
    private const string ManagedResource = "YunX.Desktop.Native.System.Data.SQLite.dll";

    /// <summary>原生 interop（SQLite 按「程序集目录\x64\」拼路径找它）。</summary>
    private const string NativeDllName = "SQLite.Interop.dll";
    private const string NativeResource = "YunX.Desktop.Native.x64.SQLite.Interop.dll";

    private static bool _done;
    private static string? _nativePath;

    /// <summary>实际使用的原生 dll 路径（未加载为 null）。仅供诊断。</summary>
    public static string? LoadedFrom => _nativePath;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string lpFileName, IntPtr hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;
    private const uint LOAD_LIBRARY_SEARCH_USER_DIRS = 0x00000400;
    private const uint LOAD_WITH_ALTERED_SEARCH_PATH = 0x00000008;

    /// <summary>
    /// 确保 SQLite 可用。**必须在任何 SQLite 调用之前**执行，
    /// 由 <see cref="App.OnStartup"/> 最前面调用。可重复调用（幂等）。
    /// </summary>
    public static void EnsureNativeLoaded()
    {
        if (_done) return;
        _done = true;

        try
        {
            // ① 开发时（dotnet build 输出目录）两个文件本来就在旁边，直接用。
            string sideDll = Path.Combine(AppContext.BaseDirectory, ManagedDllName);
            string sideNative = Path.Combine(AppContext.BaseDirectory, "x64", NativeDllName);
            if (File.Exists(sideDll) && File.Exists(sideNative))
            {
                Register(sideNative);
                return;
            }

            // ② 单文件：从嵌入资源解压到缓存目录
            byte[]? managed = ReadResource(ManagedResource);
            byte[]? native = ReadResource(NativeResource);
            if (managed == null || native == null) return;   // net8 目标没有嵌入

            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "YunX-Desktop-CS", "native", ShortHash(managed, native));

            string managedPath = Path.Combine(root, ManagedDllName);
            string nativePath = Path.Combine(root, "x64", NativeDllName);

            WriteIfChanged(managedPath, managed);
            WriteIfChanged(nativePath, native);

            // 从磁盘加载托管程序集：这样它的 Location 有值，
            // SQLite 内部算路径时不会拿到 null。
            try
            {
                Assembly.LoadFrom(managedPath);
            }
            catch
            {
                // 已经加载过（比如重复调用）会抛，忽略即可 ——
                // 后续 Register 仍能保证原生库可用。
            }

            Register(nativePath);
            CleanupOldCache(Path.GetDirectoryName(root)!);
        }
        catch (Exception ex)
        {
            // 这里不能抛：抛出去会在启动最开始崩掉，而真正需要报错的地方
            // （打开数据库）会给出更具体的错误。
            TryLog("原生库引导失败：" + ex);
        }
    }

    /// <summary>
    /// 注册搜索路径 + 预加载原生 dll。
    ///
    /// <para><b>预加载是保底措施</b>：正常情况下 SQLite 按「程序集目录\x64\」就能找到。
    /// 但万一它换了查找逻辑、或程序集目录和我们预期不同，预加载能兜住 ——
    /// Windows 对已加载模块按**文件名**去重，我们都装进进程了，
    /// 它后面无论怎么加载都会命中同一个模块。</para>
    /// </summary>
    private static void Register(string nativePath)
    {
        string? dir = Path.GetDirectoryName(nativePath);
        if (string.IsNullOrEmpty(dir)) return;

        SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS | LOAD_LIBRARY_SEARCH_USER_DIRS);
        AddDllDirectory(dir);

        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (path.IndexOf(dir, StringComparison.OrdinalIgnoreCase) < 0)
            Environment.SetEnvironmentVariable("PATH", path + ";" + dir);

        PreloadDll(nativePath);
        _nativePath = nativePath;
    }

    private static void PreloadDll(string fullPath)
    {
        if (!File.Exists(fullPath)) return;
        if (GetModuleHandle(NativeDllName) != IntPtr.Zero) return;   // 已在内存

        // LOAD_WITH_ALTERED_SEARCH_PATH：dll 若有自己的依赖，
        // 以它所在目录为起点找，而不是去 exe 目录找。
        IntPtr h = LoadLibraryEx(fullPath, IntPtr.Zero, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (h == IntPtr.Zero) h = LoadLibrary(fullPath);

        if (h == IntPtr.Zero)
        {
            string msg = $"预加载 {fullPath} 失败，Win32 错误 {Marshal.GetLastWin32Error()}";
            Console.WriteLine("[native] " + msg);
            TryLog(msg);
            return;
        }

        Console.WriteLine("[native] 预加载成功：" + fullPath);
    }

    private static void WriteIfChanged(string path, byte[] data)
    {
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var fi = new FileInfo(path);
        if (fi.Exists && fi.Length == data.Length) return;   // 目录名已是内容哈希，大小对就够

        // 先写临时文件再替换：避免解压一半被强行结束，留下大小对但内容坏的 dll
        // （那种文件会引发「不是有效的 Win32 应用」之类完全不相干的报错）。
        string tmp = path + ".tmp";
        File.WriteAllBytes(tmp, data);
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
    }

    private static string ShortHash(byte[] a, byte[] b)
    {
        var sb = new System.Text.StringBuilder(16);
        AppendHash(sb, a);
        AppendHash(sb, b);
        return sb.ToString();
    }

    private static void AppendHash(System.Text.StringBuilder sb, byte[] data)
    {
        // 这里只需要内容指纹，不用于安全用途。用长度 + 抽样 FNV 即可，
        // 免得为一次启动引入完整 SHA。
        ulong h = 14695981039346656037UL;
        int step = Math.Max(1, data.Length / 4096);
        for (int i = 0; i < data.Length; i += step)
        {
            h ^= data[i];
            h *= 1099511628211UL;
        }
        h ^= (ulong)data.Length;
        sb.Append(h.ToString("x8"));
    }

    private static byte[]? ReadResource(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var s = asm.GetManifestResourceStream(name);
        if (s == null)
        {
            // 资源名对不上时打出真实清单：这类问题要么 LogicalName 没生效，
            // 要么被织入工具重命名了，光看代码看不出来。
            string msg = $"找不到嵌入资源 '{name}'。实际资源：[" +
                         string.Join(", ", asm.GetManifestResourceNames()) + "]";
            Console.WriteLine("[native] " + msg);
            TryLog(msg);
            return null;
        }

        var buf = new byte[s.Length];
        int read = 0;
        while (read < buf.Length)
        {
            int n = s.Read(buf, read, buf.Length - read);
            if (n <= 0) break;
            read += n;
        }
        return read == buf.Length ? buf : null;
    }

    /// <summary>删掉除当前版本外的旧解压目录。失败无所谓，只为省磁盘。</summary>
    private static void CleanupOldCache(string nativeRoot)
    {
        try
        {
            if (!Directory.Exists(nativeRoot)) return;
            string current = Path.GetFileName(
                Path.GetDirectoryName(Path.GetDirectoryName(_nativePath) ?? "") ?? "");

            foreach (var dir in Directory.GetDirectories(nativeRoot))
            {
                if (string.Equals(Path.GetFileName(dir), current, StringComparison.OrdinalIgnoreCase))
                    continue;
                try { Directory.Delete(dir, recursive: true); } catch { /* 被占用下次再说 */ }
            }
        }
        catch { /* 忽略 */ }
    }

    private static void TryLog(string msg)
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "YunX-Desktop-CS");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}");
        }
        catch { /* 日志都写不了就算了 */ }
    }
}
