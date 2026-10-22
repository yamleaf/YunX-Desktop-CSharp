namespace YunX.Core.Util;

/// <summary>人读的大小/速度/时间格式化。与项目里所有 UI、日志共用一套，避免各处格式不一致。</summary>
public static class Humanize
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static string Size(long bytes)
    {
        if (bytes <= 0) return "0 B";
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < Units.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{(long)v} B" : $"{v:0.00} {Units[i]}";
    }

    /// <summary>速度：始终带 /s 后缀，便于日志里一眼区分「大小」和「速度」。</summary>
    public static string Speed(double bytesPerSec)
    {
        if (bytesPerSec <= 1) return "—";
        return Size((long)bytesPerSec) + "/s";
    }

    /// <summary>剩余时间：超过 9 小时或无法估算时返回 --:--（不显示离谱的数字）。</summary>
    public static string Eta(double seconds)
    {
        if (seconds <= 0 || seconds > 86400 * 9 || double.IsNaN(seconds) || double.IsInfinity(seconds))
            return "--:--";
        int total = (int)seconds;
        int h = total / 3600, m = total % 3600 / 60, s = total % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m:00}:{s:00}";
    }
}
