using System.Windows;
using System.Windows.Media;

namespace YunX.Desktop.Themes;

/// <summary>
/// 由单个「种子色」推导整套强调色。
///
/// <para><b>为什么只推导强调色，不推导整个色板</b>：深色界面的背景/文本/边框是
/// 中性灰阶，换成彩色种子后仍然要遵守「背景足够暗、正文足够亮」的可读性约束，
/// 把它们也染色只会让界面发脏。真正需要跟种子色统一的只有强调色一族 ——
/// 按钮、进度条、链接、选中态。</para>
///
/// <para><b>为什么用 HSL 而不是简单调透明度</b>：把主色加 30% 白色得到的高亮色
/// 会同时提高亮度但降低饱和度，看起来发灰；正确做法是<b>保持色相与饱和度，只调亮度</b>，
/// 这样 hover 态仍然是「同一个颜色的更亮版本」而不是「同一个颜色的褪色版本」。</para>
/// </summary>
public static class SeedPalette
{
    /// <summary>默认种子色（与 Dark.xaml 里的 AccentColor 一致）。</summary>
    public static readonly Color DefaultSeed = Color.FromRgb(0x4C, 0x8B, 0xF5);

    /// <summary>推导结果。</summary>
    public sealed record Palette(
        Color Base, Color Hover, Color Pressed, Color Soft, Color SoftBorder, Color OnAccent);

    /// <summary>
    /// 由种子色推导四个层次的强调色。
    /// </summary>
    /// <param name="seed">用户选的种子色。</param>
    public static Palette Derive(Color seed)
    {
        var (h, s, l) = ToHsl(seed);

        // 灰色（含纯黑/纯白）没有色相，ToHsl 会返回 h=0 —— 直接用它会把灰色染成红色，
        // 这是非常反直觉的结果。无彩色种子统一回落到默认蓝的色相。
        if (s < 0.06)
        {
            var (dh, _, _) = ToHsl(DefaultSeed);
            h = dh;
        }

        // 深色界面下种子色可能太暗（深红）导致文字对比不足，
        // 也可能太亮（浅黄）导致白字看不清：都收进能用的亮度区间。
        // 0.42~0.62 是实测「在 #0F1115 背景上既够亮又不刺眼」的范围。
        l = Math.Clamp(l, 0.42, 0.62);
        // 饱和度过低会让强调色看起来像灰色
        s = Math.Clamp(s, 0.45, 0.95);

        Color baseColor = FromHsl(h, s, l);

        // hover：更亮；pressed：更暗（按下要有「沉下去」的感觉）
        Color hover = FromHsl(h, s, Math.Min(0.78, l + 0.10));
        Color pressed = FromHsl(h, s, Math.Max(0.30, l - 0.10));

        // soft：用于选中背景。深色界面里不能用「主色 + 低透明度」，
        // 那会让背景变亮变灰；正确做法是低亮度 + 中等饱和度。
        Color soft = FromHsl(h, Math.Min(s, 0.62), 0.17);
        Color softBorder = FromHsl(h, Math.Min(s, 0.70), 0.28);

        // 强调色上的前景：按对比度选黑或白，而不是按亮度拍一个阈值。
        // 单纯「亮度 > 0.62 就用黑字」会让浅粉、浅橙这类颜色的白字对比度只有 2:1 左右。
        Color onAccent = ContrastRatio(Colors.White, baseColor) >= ContrastRatio(
            Color.FromRgb(0x10, 0x12, 0x16), baseColor)
            ? Colors.White
            : Color.FromRgb(0x10, 0x12, 0x16);

        return new Palette(baseColor, hover, pressed, soft, softBorder, onAccent);
    }

    /// <summary>
    /// 把推导结果写进应用资源，界面上所有 <c>StaticResource AccentBrush</c> 之外
    /// 引用了这些 key 的地方会立即更新（DynamicResource 场景）。
    /// </summary>
    public static void Apply(Application app, Color seed)
    {
        var p = Derive(seed);

        SetColor(app, "AccentColor", p.Base);
        SetColor(app, "AccentHiColor", p.Hover);
        SetColor(app, "AccentPressColor", p.Pressed);
        SetColor(app, "AccentSoftColor", p.Soft);

        SetBrush(app, "AccentBrush", p.Base);
        SetBrush(app, "AccentHiBrush", p.Hover);
        SetBrush(app, "AccentPressBrush", p.Pressed);
        SetBrush(app, "AccentSoftBrush", p.Soft);
        SetBrush(app, "AccentSoftBorderBrush", p.SoftBorder);
        SetBrush(app, "OnAccentBrush", p.OnAccent);

        // 渐变也要跟着换，否则主按钮和外发光会保持旧色
        app.Resources["AccentGradient"] = MakeGradient(p.Base, p.Hover, horizontal: false);
        app.Resources["AccentGradientH"] = MakeGradient(p.Base, p.Hover, horizontal: true);
    }

    private static void SetColor(Application app, string key, Color c) => app.Resources[key] = c;

    private static void SetBrush(Application app, string key, Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        app.Resources[key] = brush;
    }

    private static LinearGradientBrush MakeGradient(Color from, Color to, bool horizontal)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = horizontal ? new Point(1, 0) : new Point(1, 1),
        };
        brush.GradientStops.Add(new GradientStop(from, 0));
        brush.GradientStops.Add(new GradientStop(to, 1));
        brush.Freeze();
        return brush;
    }

    // ================= 色彩空间 =================

    /// <summary>解析 <c>#RRGGBB</c> / <c>#AARRGGBB</c>。失败返回默认种子色。</summary>
    public static Color Parse(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex)) return DefaultSeed;
        try
        {
            return (Color)ColorConverter.ConvertFromString(hex.Trim());
        }
        catch
        {
            return DefaultSeed;
        }
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>相对亮度（WCAG 公式，用于决定前景用黑字还是白字）。</summary>
    public static double Luminance(Color c)
    {
        static double Ch(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Ch(c.R) + 0.7152 * Ch(c.G) + 0.0722 * Ch(c.B);
    }

    /// <summary>WCAG 对比度（1:1 ~ 21:1）。用于决定强调色上该用黑字还是白字。</summary>
    public static double ContrastRatio(Color a, Color b)
    {
        double la = Luminance(a);
        double lb = Luminance(b);
        double hi = Math.Max(la, lb);
        double lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    public static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2;
        double d = max - min;

        if (d < 1e-6) return (0, 0, l);

        double s = l > 0.5 ? d / (2 - max - min) : d / (max + min);

        double h;
        if (Math.Abs(max - r) < 1e-6) h = (g - b) / d + (g < b ? 6 : 0);
        else if (Math.Abs(max - g) < 1e-6) h = (b - r) / d + 2;
        else h = (r - g) / d + 4;

        return (h * 60, s, l);
    }

    public static Color FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);

        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = l - c / 2;

        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromRgb(
            (byte)Math.Round((r + m) * 255),
            (byte)Math.Round((g + m) * 255),
            (byte)Math.Round((b + m) * 255));
    }
}
