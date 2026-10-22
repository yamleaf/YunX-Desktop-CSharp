namespace YunX.Core.Download;

/// <summary>
/// 下载可调参数（对应原项目「实验性功能」页）。
///
/// <para><b>双档范围设计</b>：每项都有「合理范围」与「硬上限」两档。
/// 超出合理范围时 UI 会弹确认框提醒（仍可启用，但内存/速度/稳定性风险由使用者承担）。
/// 这里把两档范围集中定义，避免阈值散落在各处、UI 与引擎不一致。</para>
/// </summary>
public sealed class DownloadTuning
{
    /// <summary>读缓冲（字节）。原项目默认 64KB。</summary>
    public int ReadBufferBytes { get; set; } = 64 * 1024;

    /// <summary>慢连接抢占阈值（字节/秒）。原项目默认 12KB/s。</summary>
    public double PreemptFloorBps { get; set; } = 12 * 1024;

    /// <summary>慢连接判定时长（毫秒）。原项目默认 15 秒，即连接跑够这么久才判慢。</summary>
    public double PreemptMinAgeMs { get; set; } = 15_000;

    /// <summary>是否允许 ALPN 协商 h2。默认关闭（只用 HTTP/1.1）。</summary>
    public bool EnableHttp2 { get; set; }

    /// <summary>按平台分别设置的线程数（平台 Key → 线程数）。</summary>
    public Dictionary<string, int> ThreadsPerPlatform { get; set; } = new();

    /// <summary>取指定平台的线程数；未单独配置时用 <paramref name="fallback"/>。</summary>
    public int ThreadsFor(string platform, int fallback = 8)
        => ThreadsPerPlatform.TryGetValue(platform, out int n) ? n : fallback;

    // ================= 范围定义 =================

    /// <summary>读缓冲合理范围（KB）。</summary>
    public static readonly (int Min, int Max) ReadBufferReasonableKb = (16, 1024);
    /// <summary>读缓冲硬上限（KB）。超过 1MB 时每条在飞连接都会占这么多内存。</summary>
    public static readonly (int Min, int Max) ReadBufferHardKb = (16, 4096);

    public static readonly (double Min, double Max) PreemptFloorReasonableKbps = (4, 1024);
    public static readonly (double Min, double Max) PreemptFloorHardKbps = (4, 10240);

    public static readonly (double Min, double Max) PreemptAgeReasonableSec = (5, 120);
    public static readonly (double Min, double Max) PreemptAgeHardSec = (5, 600);

    public static readonly (int Min, int Max) ThreadsHard = (1, 512);

    /// <summary>一次范围校验结果。</summary>
    public sealed record RangeCheck(bool Ok, bool WithinReasonable, string Message);

    public static RangeCheck CheckReadBuffer(int kb)
    {
        if (kb < ReadBufferHardKb.Min || kb > ReadBufferHardKb.Max)
            return new RangeCheck(false, false,
                $"读缓冲需在 {ReadBufferHardKb.Min}–{ReadBufferHardKb.Max} KB 之间");
        bool reasonable = kb >= ReadBufferReasonableKb.Min && kb <= ReadBufferReasonableKb.Max;
        return new RangeCheck(true, reasonable, reasonable ? "" :
            $"读缓冲 ${kb} KB 超出合理范围（{ReadBufferReasonableKb.Min}–{ReadBufferReasonableKb.Max} KB）：" +
            "每条在飞连接都会占用这么多内存，堆压力显著上升");
    }

    public static RangeCheck CheckPreemptFloor(double kbps)
    {
        if (kbps < PreemptFloorHardKbps.Min || kbps > PreemptFloorHardKbps.Max)
            return new RangeCheck(false, false,
                $"抢占阈值需在 {PreemptFloorHardKbps.Min}–{PreemptFloorHardKbps.Max} KB/s 之间");
        bool reasonable = kbps >= PreemptFloorReasonableKbps.Min
                          && kbps <= PreemptFloorReasonableKbps.Max;
        return new RangeCheck(true, reasonable, reasonable ? "" :
            $"抢占阈值 {kbps} KB/s 超出合理范围：阈值过高会导致连接被频繁重连，速度反而下降");
    }

    public static RangeCheck CheckPreemptAge(double seconds)
    {
        if (seconds < PreemptAgeHardSec.Min || seconds > PreemptAgeHardSec.Max)
            return new RangeCheck(false, false,
                $"判定时长需在 {PreemptAgeHardSec.Min}–{PreemptAgeHardSec.Max} 秒之间");
        bool reasonable = seconds >= PreemptAgeReasonableSec.Min
                          && seconds <= PreemptAgeReasonableSec.Max;
        return new RangeCheck(true, reasonable, reasonable ? "" :
            $"判定时长 {seconds} 秒超出合理范围：过短会误杀正常爬坡的连接");
    }

    /// <summary>恢复一组安全的默认值（设置页「一键重置」）。</summary>
    public static DownloadTuning CreateDefault() => new();

    /// <summary>把当前值裁剪到硬范围内（配置被手工改坏时的兜底）。</summary>
    public void ClampToHardLimits()
    {
        ReadBufferBytes = Math.Clamp(ReadBufferBytes,
            ReadBufferHardKb.Min * 1024, ReadBufferHardKb.Max * 1024);
        PreemptFloorBps = Math.Clamp(PreemptFloorBps,
            PreemptFloorHardKbps.Min * 1024, PreemptFloorHardKbps.Max * 1024);
        PreemptMinAgeMs = Math.Clamp(PreemptMinAgeMs,
            PreemptAgeHardSec.Min * 1000, PreemptAgeHardSec.Max * 1000);

        foreach (var key in ThreadsPerPlatform.Keys.ToList())
        {
            ThreadsPerPlatform[key] = Math.Clamp(ThreadsPerPlatform[key],
                ThreadsHard.Min, ThreadsHard.Max);
        }
    }
}
