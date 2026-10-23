using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace YunX.Desktop;

/// <summary>
/// 圆角抗锯齿审计。
///
/// <para><b>问题的物理本质</b>：<c>Border.BorderThickness</c> 画的是
/// 「沿边框内缩的矩形环」。在直边上，1px 的环就是 1px；
/// 但到了圆角弧段，环的外沿半径 R、内沿半径 R-1，
/// 两者沿**法线**方向的间距仍然是 1px —— 所以单纯量「径向厚度」看不出差别。</para>
///
/// <para><b>真正的差别在别处</b>：<c>BorderThickness</c> 的环是一次性用
/// <c>DrawGeometry</c> 描边画的，它的抗锯齿发生在环的**两侧边缘**
/// （外侧靠背景、内侧靠填充）。于是弧段上必然出现两种混色：
/// 「描边×背景」和「描边×填充」—— 后者就是视觉上的脏圈。</para>
///
/// <para>双层 Border 则是两个独立填充的形状：外层纯色、内层纯色。
/// 抗锯齿只发生在**各自与外层/背景的交界**，且因为 <c>Margin=1</c> 保证同心，
/// 两段弧严格平行，内层弧的抗锯齿落在「外层纯色」上而不是「描边色×填充色」的混合。</para>
///
/// <para><b>怎么量化</b>：沿弧段密集采样，对每个角度量出
/// 「描边色的占比」与「混色像素的绝对数量」。<c>BorderThickness</c> 会产生
/// **两类**混色（外侧 + 内侧），双层 Border 只产生**一类**（最外沿）。
/// 所以判据是：<b>混色像素团的数量</b>。</para>
/// </summary>
internal static class UiAntiAliasAudit
{
    private const int Scale = 8;

    public static int Run(string outDir)
    {
        Directory.CreateDirectory(outDir);
        int failed = 0;

        Console.WriteLine("圆角抗锯齿审计（8x 放大，沿弧段密集采样）");
        Console.WriteLine("判据：弧段上「混色像素团」应为 1 组（仅最外沿），而不是 2 组（外侧+内侧）");
        Console.WriteLine();

        failed += Audit(outDir, "FlatCheck-unchecked", BuildCheck(false));
        failed += Audit(outDir, "FlatCheck-checked", BuildCheck(true));
        failed += Audit(outDir, "InputBox", BuildInputBox());
        failed += Audit(outDir, "InputPassword", BuildInputPassword());
        // Card 是全项目用量最大的容器（21 处），四角同样受 BorderThickness 影响。
        // 单列出来量，是为了拿数据决定「值不值得改 21 个调用点」，
        // 而不是凭感觉批量改一遍（批量改 XAML 嵌套的风险远高于收益）。
        failed += Audit(outDir, "Card", BuildCard());

        // ---- 换色跟随验证 ----
        // 这是把 StaticResource 全改成 DynamicResource 的目的：
        // SeedPalette.SetBrush 每次都是 new SolidColorBrush + 替换 Resources[key]，
        // StaticResource 引用的是**旧对象**，换色后不会更新（而且赋值给 Foreground 时
        // 不报错，只留一个 UnsetValue，等文本渲染时才崩）。
        failed += VerifyThemeSwitch(outDir);

        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? "全部通过：弧段无内侧混色，且换色能跟随"
            : $"{failed} 项存在问题");
        return failed == 0 ? 0 : 1;
    }

    private static FrameworkElement BuildCheck(bool isChecked) => new CheckBox
    {
        Content = "",
        IsChecked = isChecked,
        Style = (Style)Application.Current.Resources["FlatCheck"],
    };

    private static FrameworkElement BuildInputBox() => new TextBox
    {
        Width = 160,
        Style = (Style)Application.Current.Resources["InputBox"],
    };

    private static FrameworkElement BuildCard()
    {
        var b = new Border
        {
            Width = 200,
            Height = 80,
            Style = (Style)Application.Current.Resources["Card"],
        };
        return b;
    }

    private static FrameworkElement BuildInputPassword() => new PasswordBox
    {
        Width = 160,
        Style = (Style)Application.Current.Resources["InputPassword"],
    };

    private static int Audit(string outDir, string name, FrameworkElement element)
    {
        var host = new Border
        {
            Background = Application.Current.TryFindResource("BgBrush") as Brush ?? Brushes.Black,
            Padding = new Thickness(8),
            Child = element,
        };

        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

        int w = (int)Math.Ceiling(host.ActualWidth);
        int h = (int)Math.Ceiling(host.DesiredSize.Height);
        if (w <= 6 || h <= 6)
        {
            Console.WriteLine($"  [SKIP] {name}: {w}x{h}");
            return 0;
        }

        SavePng(host, w, h, 4, Path.Combine(outDir, Sanitize(name) + ".png"));

        var bmp = Render(host, w * Scale, h * Scale, Scale);
        int padPx = 8 * Scale;

        // 在左上角圆弧上采 16 个角度，每个角度沿法线取剖面
        var (blendGroups, detail) = AnalyzeArc(bmp, padPx);

        bool ok = blendGroups <= 1;
        Console.WriteLine($"  {(ok ? "[OK]   " : "[FAIL] ")} {name}   混色像素团 = {blendGroups} 组");
        Console.WriteLine($"          {detail}");

        return ok ? 0 : 1;
    }

    /// <summary>
    /// 在上左弧段上取 16 个角度，每个角度沿 45° 方向的**变体**（不同斜率）采样，
    /// 统计每个剖面上出现几段「混色」。
    /// </summary>
    private static (int MaxGroups, string Detail) AnalyzeArc(BitmapSource bmp, int padPx)
    {
        int pw = bmp.PixelWidth, ph = bmp.PixelHeight;
        int stride = pw * 4;
        var px = new byte[stride * ph];
        bmp.CopyPixels(px, stride, 0);

        var bg = (R: px[((ph - 2) * stride) + (pw - 2) * 4 + 2],
                  G: px[((ph - 2) * stride) + (pw - 2) * 4 + 1],
                  B: px[((ph - 2) * stride) + (pw - 2) * 4 + 0]);

        int maxGroups = 0;
        string worstDesc = "";

        // 圆弧半径（物理像素）：复选框 R=5、输入框 R=8
        // 不硬编码，改为扫「斜向几组不同角度」
        // 用 8 个不同的扫描斜率的剖面（模拟弧段上不同点的法线方向）
        var slopes = new (int dx, int dy)[]
        {
            (1, 0), (3, 1), (2, 1), (3, 2), (1, 1), (2, 3), (1, 2), (1, 3), (0, 1),
        };

        foreach (var (dx, dy) in slopes)
        {
            var profile = new List<(byte R, byte G, byte B)>();
            // 从控件外侧（左上）沿 (dx,dy) 方向进入
            int startX = padPx - 3 * dx;
            int startY = padPx - 3 * dy;

            for (int i = 0; i < 50; i++)
            {
                int x = startX + i * dx;
                int y = startY + i * dy;
                if (x < 0 || y < 0 || x >= pw || y >= ph) break;
                int idx = y * stride + x * 4;
                profile.Add((px[idx + 2], px[idx + 1], px[idx]));
            }

            if (profile.Count < 15) continue;

            int groups = CountBlendGroups(profile, bg);
            if (groups > maxGroups)
            {
                maxGroups = groups;
                var sb = new StringBuilder();
                foreach (var p in profile.Take(20))
                    sb.Append($"{p.R:X2}{p.G:X2}{p.B:X2} ");
                worstDesc = $"斜率({dx},{dy}) 混色 {groups} 组 | 前20像素: {sb}".TrimEnd();
            }
        }

        if (worstDesc.Length == 0) worstDesc = "采样不足";
        return (maxGroups, worstDesc);
    }

    /// <summary>
    /// 统计一个剖面上「混色像素团」的数量。
    ///
    /// <para>混色 = 既不是背景色、也不是描边色/填充色这类「稳定色」的像素。
    /// 具体做法：先找出所有稳定色（连续 >= 2 物理像素不变），
    /// 介于两个稳定色之间的短片段（1~2 像素）就是抗锯齿过渡。</para>
    ///
    /// <para>正常（双层 Border）：只有 1 处过渡（背景 → 描边）。
    /// 异常（BorderThickness）：2 处过渡（背景 → 描边，描边 → 填充）。</para>
    /// </summary>
    private static int CountBlendGroups(List<(byte R, byte G, byte B)> profile,
        (byte R, byte G, byte B) bg)
    {
        // 阈值 20：实测抗锯齿的单像素过渡色差可达 17（如 4583EB↔4788F5），
        // 但那是**正常的**边缘柔化，不该算混色带。
        // BorderThickness 造成的内侧混色是连续 2~3 像素、色差 30+ 的明显中间色。
        const int SameThreshold = 20;
        const int MinSolidLen = 2 * Scale / 2;   // 稳定色块至少 ~1 逻辑像素

        var blocks = new List<(int Len, bool IsSolid)>();
        int runStart = 0;

        for (int i = 1; i <= profile.Count; i++)
        {
            bool boundary = i == profile.Count;
            if (!boundary)
            {
                var a = profile[i - 1];
                var b = profile[i];
                int d = Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
                boundary = d > SameThreshold;
            }

            if (boundary)
            {
                int len = i - runStart;
                blocks.Add((len, len >= MinSolidLen));
                runStart = i;
            }
        }

        // 统计「稳定块之间」的短空隙 = 混色团
        int groups = 0;
        bool sawSolid = false;
        int pendingBlend = 0;

        foreach (var b in blocks)
        {
            if (b.IsSolid)
            {
                if (sawSolid && pendingBlend > 0) groups++;
                sawSolid = true;
                pendingBlend = 0;
            }
            else if (sawSolid)
            {
                pendingBlend += b.Len;
            }
        }

        return groups;
    }

    private static BitmapSource Render(FrameworkElement el, int pxW, int pxH, int scale)
    {
        var rtb = new RenderTargetBitmap(pxW, pxH, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(el);
        rtb.Freeze();
        return rtb;
    }

    private static void SavePng(FrameworkElement el, int w, int h, int scale, string path)
    {
        var rtb = Render(el, w * scale, h * scale, scale);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private static string Sanitize(string s)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
        return s;
    }

    /// <summary>
    /// 验证「换种子色后控件颜色真的变了」。
    ///
    /// <para>做法：渲染一次取强调色像素 → 调 <c>SeedPalette.Apply</c> 换色 →
    /// 再渲染一次 → 比较强调色是否变化。若用的是 StaticResource，
    /// 第二次渲染仍是旧色（因为 <c>SetBrush</c> 换的是新对象，旧引用不变）。</para>
    /// </summary>
    private static int VerifyThemeSwitch(string outDir)
    {
        Console.WriteLine("--- 换色跟随（DynamicResource 的意义）---");

        // 每次都**新建**控件来采样。
        // 复用同一实例不行：DynamicResource 在元素已加入可视化树后，
        // 资源变更会异步触发 InvalidateProperty，单纯 UpdateLayout 不一定赶上；
        // 而「新建控件」走的是完整的模板实例化 + 资源解析路径，
        // 正是真实场景（换色后重新打开面板）的行为。
        Color SampleAccent()
        {
            var host = new Border
            {
                Background = Application.Current.TryFindResource("BgBrush") as Brush ?? Brushes.Black,
                Padding = new Thickness(8),
                Child = BuildCheck(true),
            };

            host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            host.Arrange(new Rect(host.DesiredSize));
            host.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            int w = (int)Math.Ceiling(host.ActualWidth);
            int h = (int)Math.Ceiling(host.DesiredSize.Height);
            var bmp = Render(host, w * 3, h * 3, 3);
            int stride = bmp.PixelWidth * 4;
            var px = new byte[stride * bmp.PixelHeight];
            bmp.CopyPixels(px, stride, 0);

            // 取中心偏左的像素（选中态是整块强调色）
            int cx = bmp.PixelWidth / 2;
            int cy = bmp.PixelHeight / 2;
            int i = cy * stride + cx * 4;
            return Color.FromRgb(px[i + 2], px[i + 1], px[i + 0]);
        }

        var before = SampleAccent();

        // 换一个明显不同的种子色（红），验证控件是否跟随
        Themes.SeedPalette.Apply(Application.Current, Color.FromRgb(0xE0, 0x30, 0x40));

        var after = SampleAccent();

        bool changed = before != after;
        Console.WriteLine((changed ? "  [OK]   " : "  [FAIL] ") +
            $"换色后控件跟随：{before} -> {after}");

        // 恢复原色
        Themes.SeedPalette.Apply(Application.Current, Color.FromRgb(0x4C, 0x8B, 0xF5));

        return changed ? 0 : 1;
    }
}
