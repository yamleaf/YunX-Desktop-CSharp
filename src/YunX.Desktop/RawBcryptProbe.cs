using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace YunX.Desktop;

/// <summary>
/// 裸 BCrypt 自检：完全不经过 <c>AesGcmCompat</c>，手工跑一遍
/// OpenAlgorithm → SetProperty → GenerateSymmetricKey → Encrypt，
/// 每步打印 NTSTATUS 与关键参数。
///
/// <para><b>目的</b>：<c>AesGcmCompat</c> 在独立 console 探针里能过、
/// 在真实 WPF 程序里报 <c>0xC000000D</c>，而两边的源码与 dll 完全相同。
/// 只有把封装一层层剥开，才能看出是哪一步的参数在宿主环境里被改了。</para>
///
/// <para>特别要验证 <c>Marshal.SizeOf</c> 算出的结构体大小 ——
/// 如果宿主里 IntPtr 是 4 字节（32 位进程），cbSize 会是 48 而不是 88，
/// bcrypt 就会拒绝。</para>
/// </summary>
internal static class RawBcryptProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct AUTH_INFO
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

    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int BCryptOpenAlgorithmProvider(
        out IntPtr phAlgorithm, string pszAlgId, string? pszImplementation, int dwFlags);

    [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
    private static extern int BCryptSetProperty(
        IntPtr hObject, string pszProperty, string pbInput, int cbInput, int dwFlags);

    [DllImport("bcrypt.dll")]
    private static extern int BCryptGenerateSymmetricKey(
        IntPtr hAlgorithm, out IntPtr phKey, IntPtr pbKeyObject, int cbKeyObject,
        byte[] pbSecret, int cbSecret, int dwFlags);

    [DllImport("bcrypt.dll")]
    private static extern int BCryptEncrypt(
        IntPtr hKey, IntPtr pbInput, int cbInput, ref AUTH_INFO pPaddingInfo,
        byte[]? pbIV, int cbIV, IntPtr pbOutput, int cbOutput, out int pcbResult, int dwFlags);

    [DllImport("bcrypt.dll")]
    private static extern int BCryptDestroyKey(IntPtr hKey);

    [DllImport("bcrypt.dll")]
    private static extern int BCryptCloseAlgorithmProvider(IntPtr hAlgorithm, int dwFlags);

    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        var log = new StringBuilder();
        int fail = 0;

        void W(string s) { Console.WriteLine(s); log.AppendLine(s); }

        W("=== 裸 BCrypt 自检（不经 AesGcmCompat）===");
        W($"  IntPtr.Size = {IntPtr.Size}  (4=32位, 8=64位)");
        W($"  Marshal.SizeOf<AUTH_INFO>() = {Marshal.SizeOf(typeof(AUTH_INFO))}  (期望 88 on x64)");
        W("");

        IntPtr hAlg = IntPtr.Zero, hKey = IntPtr.Zero;
        try
        {
            int st = BCryptOpenAlgorithmProvider(out hAlg, "AES", null, 0);
            W($"  OpenAlgorithmProvider  -> 0x{st:X8}");
            if (st != 0) { fail++; return fail; }

            // cbInput 必须是字节数（含结尾 NUL，UTF-16）
            const string mode = "ChainingModeGCM";
            int cbInput = (mode.Length + 1) * 2;
            st = BCryptSetProperty(hAlg, "ChainingMode", mode, cbInput, 0);
            W($"  SetProperty(GCM)       -> 0x{st:X8}  (cbInput={cbInput})");
            if (st != 0) { fail++; return fail; }

            byte[] key = new byte[32];
            for (int i = 0; i < 32; i++) key[i] = (byte)(i + 1);
            st = BCryptGenerateSymmetricKey(hAlg, out hKey, IntPtr.Zero, 0, key, key.Length, 0);
            W($"  GenerateSymmetricKey   -> 0x{st:X8}  (keyLen={key.Length})");
            if (st != 0) { fail++; return fail; }

            // ---- 加密 ----
            byte[] nonce = new byte[12];
            for (int i = 0; i < 12; i++) nonce[i] = (byte)(0xA0 + i);
            byte[] pt = new byte[32];
            for (int i = 0; i < 32; i++) pt[i] = (byte)(0x40 + i);
            byte[] tag = new byte[16];

            IntPtr pNonce = Marshal.AllocHGlobal(nonce.Length);
            IntPtr pTag = Marshal.AllocHGlobal(tag.Length);
            IntPtr pIn = Marshal.AllocHGlobal(pt.Length);
            IntPtr pOut = Marshal.AllocHGlobal(pt.Length);
            try
            {
                Marshal.Copy(nonce, 0, pNonce, nonce.Length);
                Marshal.Copy(pt, 0, pIn, pt.Length);

                var info = new AUTH_INFO
                {
                    cbSize = Marshal.SizeOf(typeof(AUTH_INFO)),
                    dwInfoVersion = 1,
                    pbNonce = pNonce,
                    cbNonce = nonce.Length,
                    pbAuthData = IntPtr.Zero,
                    cbAuthData = 0,
                    pbTag = pTag,
                    cbTag = tag.Length,
                    pbMacContext = IntPtr.Zero,
                    cbMacContext = 0,
                    cbAAD = 0,
                    cbData = 0,
                    dwFlags = 0,
                };

                W($"  info.cbSize            = {info.cbSize}");
                W($"  info.cbNonce           = {info.cbNonce}  pbNonce=0x{pNonce.ToInt64():X}");
                W($"  info.cbTag             = {info.cbTag}  pbTag=0x{pTag.ToInt64():X}");
                W($"  pbIV = null, cbIV = 0, cbInput = {pt.Length}, cbOutput = {pt.Length}");

                st = BCryptEncrypt(hKey, pIn, pt.Length, ref info, null, 0,
                    pOut, pt.Length, out int written, 0);
                W($"  BCryptEncrypt          -> 0x{st:X8}  written={written}");
                if (st != 0) fail++;
                else
                {
                    Marshal.Copy(pTag, tag, 0, tag.Length);
                    W($"  tag = {BitConverter.ToString(tag)}");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pNonce);
                Marshal.FreeHGlobal(pTag);
                Marshal.FreeHGlobal(pIn);
                Marshal.FreeHGlobal(pOut);
            }
        }
        finally
        {
            if (hKey != IntPtr.Zero) BCryptDestroyKey(hKey);
            if (hAlg != IntPtr.Zero) BCryptCloseAlgorithmProvider(hAlg, 0);
        }

        W("");
        W(fail == 0 ? "裸 BCrypt 调用成功 —— 问题在封装层" : "裸 BCrypt 也失败 —— 问题在环境/参数");

        try
        {
            File.WriteAllText(Path.Combine(outDir, "raw-bcrypt.log"), log.ToString(),
                new UTF8Encoding(false));
        }
        catch { }

        return fail;
    }
}
