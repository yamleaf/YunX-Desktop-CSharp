using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using YunX.Core.Compat;
using YunX.Core.Security;

namespace YunX.Desktop;

/// <summary>
/// 加密自检：在真实程序环境里跑最小 GCM 往返，定位 net48 上的 BCrypt 失败。
///
/// <para><b>为什么需要在宿主程序里跑</b>：同一份 <c>AesGcmCompat.cs</c>
/// 在独立 console 探针里全部通过，在这里却报 <c>BCryptEncrypt 0xC000000D</c>。
/// 差异只在宿主，所以必须把探针搬进宿主。</para>
///
/// <para><b>另一个坑</b>：早先的版本用 lambda 捕获局部函数 <c>W</c>，
/// Release + 优化下编译器生成的闭包类带 <c>internal</c> 可见性，
/// 访问 <c>YunX.Core</c> 的 internal 类型时直接 <c>MethodAccessException</c> ——
/// 与 <c>InternalsVisibleTo</c> 配合有坑。所以这里全部改成显式静态方法。</para>
/// </summary>
internal static class CryptoProbe
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetModuleFileName(IntPtr hModule, StringBuilder lpFilename, int nSize);

    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var log = new StringBuilder();
        int failed = 0;

        void W(string s) { Console.WriteLine(s); log.AppendLine(s); }

        W("=== 加密自检（真实程序环境）===");
        W($"  运行时 = {(IntPtr.Size == 8 ? "x64" : "x86")}, CLR = {Environment.Version}");
        W($"  OS = {Environment.OSVersion}");
        W("");

        // bcrypt.dll 实际加载自哪里？如果 Costura 解包了一份到临时目录，
        // 用的就不是系统实现，行为会不一致。
        W("--- bcrypt.dll 加载来源 ---");
        foreach (var name in new[] { "bcrypt.dll", "bcryptprimitives.dll", "ncrypt.dll" })
        {
            IntPtr h = GetModuleHandle(name);
            if (h == IntPtr.Zero) { W($"  {name}: 未加载"); continue; }
            var sb = new StringBuilder(1024);
            GetModuleFileName(h, sb, sb.Capacity);
            W($"  {name}: {sb}");
        }
        W($"  BaseDirectory = {AppDomain.CurrentDomain.BaseDirectory}");
        W("");

        failed += TryStep(W, "AES-128 字面量密钥往返", StepAes128);
        failed += TryStep(W, "AES-256 字面量密钥往返", StepAes256);
        failed += TryStep(W, "CredentialCipher 全新初始化（临时目录）", StepFreshCipher);
        failed += TryStep(W, "真实 credential.key 副本初始化", StepRealKey);

        W("");
        W(failed == 0 ? "自检全部通过" : $"{failed} 项失败");

        try
        {
            File.WriteAllText(Path.Combine(outDir, "crypto-probe.log"), log.ToString(),
                new UTF8Encoding(false));
        }
        catch { }

        return failed;
    }

    private static void StepAes128()
    {
        byte[] key = new byte[16];
        for (int i = 0; i < 16; i++) key[i] = (byte)(i + 1);
        RoundTrip(key, 32);
    }

    private static void StepAes256()
    {
        byte[] key = new byte[32];
        for (int i = 0; i < 32; i++) key[i] = (byte)(0x80 + i);
        RoundTrip(key, 32);
    }

    private static void StepFreshCipher()
    {
        string tmp = Path.Combine(Path.GetTempPath(), "yunx-crypto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var c = new CredentialCipher(tmp, KeyProtectionLevel.MachineBound);
            c.Initialize();
            string enc = c.Encrypt("probe");
            if (c.Decrypt(enc) != "probe") throw new Exception("往返不一致");
            c.Dispose();
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    private static void StepRealKey()
    {
        string src = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "YunX-Desktop-CS", "credential.key");
        if (!File.Exists(src)) throw new Exception("原目录无 credential.key");

        string tmp = Path.Combine(Path.GetTempPath(), "yunx-real-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            File.Copy(src, Path.Combine(tmp, "credential.key"));
            var c = new CredentialCipher(tmp, KeyProtectionLevel.MachineBound);
            c.Initialize();
            string enc = c.Encrypt("probe");
            if (c.Decrypt(enc) != "probe") throw new Exception("往返不一致");
            c.Dispose();
        }
        finally { try { Directory.Delete(tmp, true); } catch { } }
    }

    /// <summary>最小 GCM 往返。独立静态方法，避免闭包类型的可见性问题。</summary>
    private static void RoundTrip(byte[] key, int len)
    {
        byte[] nonce = new byte[12];
        byte[] pt = new byte[len];
        for (int i = 0; i < len; i++) pt[i] = (byte)(0x40 + i);
        byte[] ct = new byte[len];
        byte[] tag = new byte[16];

        using (var g = new AesGcmCompat(key, 16))
        {
            g.Encrypt(nonce, pt, ct, tag);
        }

        byte[] back = new byte[len];
        using (var g2 = new AesGcmCompat(key, 16))
        {
            g2.Decrypt(nonce, ct, tag, back);
        }

        for (int i = 0; i < len; i++)
            if (back[i] != pt[i]) throw new Exception("往返不一致");
    }

    private static int TryStep(Action<string> log, string name, Action action)
    {
        try { action(); log("  [OK]   " + name); return 0; }
        catch (Exception ex)
        {
            log($"  [FAIL] {name}\n         {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
