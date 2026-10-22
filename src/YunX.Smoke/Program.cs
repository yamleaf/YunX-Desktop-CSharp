// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 Lyaco
//
// YunX-Desktop-CSharp —— 基于 YunX-Desktop（AGPL-3.0）的 C# / WPF 重构版。
// 本程序是自由软件：你可以依据 GNU AGPL-3.0 的条款重新分发或修改它。
// 完整协议见仓库根目录 LICENSE。

using System.Text;
using YunX.Core.Cloud;
using YunX.Core.Download;
using YunX.Core.Mirrors;
using YunX.Core.Net;
using YunX.Core.Util;
using YunX.GitHub;

namespace YunX.Smoke;

/// <summary>
/// 验证程序：单元断言 + 真实网络下载。
///
/// 用法：
///   dotnet run --project src/YunX.Smoke              # 只跑离线断言
///   dotnet run --project src/YunX.Smoke -- live      # 追加真实网络验证
///   dotnet run --project src/YunX.Smoke -- live tail # 追加收尾塌缩对比实验
/// </summary>
internal static class Program
{
    private static int _failed;
    private static int _passed;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        Console.WriteLine("==================================================");
        Console.WriteLine(" YunX C# 重构版 — 核心验证");
        Console.WriteLine("==================================================");

        RunOfflineTests();

        bool live = args.Contains("live");
        bool tail = args.Contains("tail");

        if (live || tail)
        {
            await RunLiveTestsAsync(includeTailExperiment: tail).ConfigureAwait(false);
        }

        Console.WriteLine();
        Console.WriteLine("==================================================");
        if (_failed > 0)
        {
            Console.WriteLine($" 失败 {_failed} 项 / 通过 {_passed} 项");
            return 1;
        }
        Console.WriteLine($" 全部通过 ✓  共 {_passed} 项");
        return 0;
    }

    // =====================================================================
    //  断言辅助
    // =====================================================================

    private static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { _passed++; Console.WriteLine($"  [OK]   {name}{(detail.Length > 0 ? "  " + detail : "")}"); }
        else { _failed++; Console.WriteLine($"  [FAIL] {name}{(detail.Length > 0 ? "  " + detail : "")}"); }
    }

    private static void Section(string title) =>
        Console.WriteLine($"\n== {title} ==");

    // =====================================================================
    //  离线断言
    // =====================================================================

    private static void RunOfflineTests()
    {
        TestChunkPlanner();
        TestIntervalSet();
        TestFileNamePolicy();
        TestUrlParser();
        TestHumanize();
        TestCloudParsing();
        TestNewDrives();
        TestBrowserCookies();
        TestCredentialCipher();
        TestDatabase();
        TestMarkdown();
        TestTuningRanges();
        TestMagnetLink();
        TestTaskPersistence();
        TestViewModelNotifications();
        TestXamlResourceTypes();
        EngineTests.Run((n, ok, d) => Check(n, ok, d), Section);
    }

    /// <summary>
    /// 关键回归测试：块数反算不能产生极小的尾块。
    /// 原 Python 版在这里出过 bug —— 17,840,399 字节 / 8 线程算出 17 块，最后一块只有 15 字节。
    /// </summary>
    private static void TestChunkPlanner()
    {
        Section("分片规划（块数反算 + 尾部收缩）");

        const long total = 17_840_399;
        long chunk = ChunkPlanner.ComputeChunkSize(total, 8);
        int n = ChunkPlanner.ComputeChunkCount(total, chunk);
        long lastChunk = total - (long)(n - 1) * chunk;

        Console.WriteLine($"   17,840,399 B / 8 线程 → {chunk:N0} B × {n} 块，尾块 {lastChunk:N0} B");
        Check("尾块不小于 1MB（原版此处仅 15 字节）", lastChunk >= 1024 * 1024,
            $"尾块={lastChunk:N0}");
        Check("块数接近 线程数×2", n is >= 14 and <= 18, $"n={n}");
        Check("块大小落在 [256KB, 4MB]", chunk is >= ChunkPlanner.MinChunk and <= ChunkPlanner.MaxChunk);

        // 多个尺寸扫一遍，确保任何大小都不产生碎片尾块
        bool allGood = true;
        long worstTail = long.MaxValue;
        long worstTotal = 0;
        foreach (long size in new long[]
                 {
                     100_000, 1_000_000, 5_000_000, 17_840_399, 100_000_000,
                     1_073_741_824, 4_294_967_296,
                 })
        {
            long c = ChunkPlanner.ComputeChunkSize(size, 8);
            int cnt = ChunkPlanner.ComputeChunkCount(size, c);
            long tailSize = size - (long)(cnt - 1) * c;
            if (tailSize < 64 * 1024 && size > 1_000_000) allGood = false;
            if (tailSize < worstTail) { worstTail = tailSize; worstTotal = size; }
        }
        Check("各尺寸下尾块都不退化", allGood,
            $"最小尾块 {worstTail / 1024} KB（总大小 {worstTotal / 1024 / 1024} MB）");

        // 尾部收缩
        long baseBlock = 4 * 1024 * 1024;
        long tailBlock = ChunkPlanner.TailBlockSize(10 * 1024 * 1024, 8, baseBlock);
        Check("尾部收缩把块压到 remaining/workers", tailBlock == 10 * 1024 * 1024 / 8,
            $"{tailBlock:N0} B");

        long tooSmall = ChunkPlanner.TailBlockSize(100_000, 8, baseBlock);
        Check("收缩不低于 64KB 下限", tooSmall >= ChunkPlanner.MinTailChunk, $"{tooSmall:N0} B");

        // ★ 回归防护：线程数越多，块数不能反而变少。
        //
        //   曾经 `if (chunk < MinChunk) return total;` 会把 2MB 文件在 8 线程下
        //   压成 1 块（1 线程），比 1 线程还慢 —— 用户设的线程数被反着用。
        //   这里直接断言"并行度随线程数单调不减"。
        long midTotal = 2 * 1024 * 1024;
        int prevChunks = 0;
        bool monotonic = true;
        string trace = "";
        foreach (int w in new[] { 1, 2, 4, 8, 16 })
        {
            long cs = ChunkPlanner.ComputeChunkSize(midTotal, w);
            int cc = ChunkPlanner.ComputeChunkCount(midTotal, cs);
            if (cc < prevChunks) monotonic = false;
            prevChunks = cc;
            trace += $"{w}线程→{cc}块  ";
        }
        Check("小块文件也不放弃并行（块数随线程数不减）", monotonic, trace.Trim());

        int chunks8 = ChunkPlanner.ComputeChunkCount(midTotal,
            ChunkPlanner.ComputeChunkSize(midTotal, 8));
        Check("2MB / 8 线程仍能切出多块", chunks8 >= 4, $"{chunks8} 块");

        // 极小文件不该被切成碎片
        long tiny = ChunkPlanner.ComputeChunkSize(13, 8);
        Check("极小文件整块装下", tiny == 13, $"{tiny} B");

        // ★ 慢分片换线：抢占时必须记住"这条线不行"，重连才能换别的。
        //
        //   原来抢占只是断线重连，attempt 归零后又选回同一条慢线 ——
        //   白付一次 TCP+TLS 握手，速度分毫不动。
        //   实测背景：cdn.gh-proxy.org 的 8 条连接里 5 条只有 20~44 KB/s，
        //   而 gh-proxy.com 同期 190~250 KB/s。
        var swState = new YunX.Core.Download.ChunkState(0, 1024 * 1024, 0);
        swState.CurrentLine = "慢线";
        swState.RequestPreempt();
        Check("抢占时记住来源线路",
            swState.PreemptedFrom == "慢线" && swState.AvoidLines().Contains("慢线"),
            $"avoid=[{string.Join(",", swState.AvoidLines())}]");

        swState.RequestPreempt();
        Check("重复拉黑不产生重复项", swState.AvoidLines().Length == 1,
            $"{swState.AvoidLines().Length} 条");

        swState.ClearAvoid();
        Check("可清空避开列表", swState.AvoidLines().Length == 0, "已清空");
    }

    private static void TestIntervalSet()
    {
        Section("已完成区间集合（断点续传基础）");

        var set = new IntervalSet();
        set.Add(0, 99);
        set.Add(200, 299);
        Check("不连续区间各自独立", set.Snapshot().Count == 2, $"{set.Snapshot().Count} 段");

        set.Add(100, 199);   // 补上中间的空洞 → 应合并成一段
        var snap = set.Snapshot();
        Check("相邻区间自动合并", snap.Count == 1 && snap[0].Start == 0 && snap[0].End == 299,
            $"{snap.Count} 段");
        Check("总字节数正确", set.TotalBytes == 300, $"{set.TotalBytes}");

        var s2 = new IntervalSet();
        s2.Add(0, 49);
        s2.Add(100, 149);
        s2.Add(50, 99);
        Check("乱序插入后仍是单段", s2.Snapshot().Count == 1, $"{s2.Snapshot().Count} 段");

        // 重复添加同一区间不应重复计数
        var s3 = new IntervalSet();
        s3.Add(0, 99);
        s3.Add(0, 99);
        Check("重复区间不重复计数", s3.TotalBytes == 100, $"{s3.TotalBytes}");

        Check("包含判断", set.Contains(150) && !set.Contains(300));
        Check("查找所在区间的末尾", set.EndOfContaining(150) == 299);
        Check("不在区间内返回 -1", set.EndOfContaining(500) == -1);

        // 从序列化形态还原（模拟重启）
        var restored = IntervalSet.FromPairs(new[] { (0L, 99L), (200L, 299L) });
        Check("从持久化形态还原", restored.TotalBytes == 200 && restored.Snapshot().Count == 2);
    }

    private static void TestFileNamePolicy()
    {
        Section("文件名与路径安全");

        Check("非法字符替换",
            FileNamePolicy.SanitizeFileName("a<b>c:d\"e|f?g*.zip") == "a_b_c_d_e_f_g_.zip",
            FileNamePolicy.SanitizeFileName("a<b>c:d\"e|f?g*.zip"));
        Check("Windows 保留名加前缀", FileNamePolicy.SanitizeFileName("CON.txt").StartsWith('_'));
        Check("空名兜底", FileNamePolicy.SanitizeFileName("") == "download.bin");
        Check("超长名保留扩展名", FileNamePolicy.SanitizeFileName(new string('a', 300) + ".zip").EndsWith(".zip"));

        // 目录穿越：这是原 Python 版缺失的防护
        Check("拒绝 ../ 穿越",
            !FileNamePolicy.SanitizeRelativePath("../../windows/system32/x.dll").Contains(".."));
        Check("拒绝绝对路径",
            FileNamePolicy.SanitizeRelativePath(@"C:\Windows\x.dll") == "x.dll",
            FileNamePolicy.SanitizeRelativePath(@"C:\Windows\x.dll"));
        Check("保留合法子目录",
            FileNamePolicy.SanitizeRelativePath("docs/img/a.png") == "docs/img/a.png",
            FileNamePolicy.SanitizeRelativePath("docs/img/a.png"));
        Check("丢弃 . 与 .. 段",
            FileNamePolicy.SanitizeRelativePath("./a/../b/c.txt") == "a/b/c.txt",
            FileNamePolicy.SanitizeRelativePath("./a/../b/c.txt"));

        string tmp = Path.Combine(Path.GetTempPath(), "yunx_contain_test");
        Directory.CreateDirectory(tmp);
        Check("包含判断（内部）", FileNamePolicy.IsContained(tmp, Path.Combine(tmp, "a.txt")));
        Check("包含判断（越界）", !FileNamePolicy.IsContained(tmp, Path.Combine(tmp, "..", "x.txt")));

        Check("URL 脱敏丢弃查询串",
            !FileNamePolicy.RedactUrl("https://api.github.com/repo?token=secret").Contains("secret"));
    }

    private static void TestUrlParser()
    {
        Section("GitHub 链接解析");

        var cases = new (string Input, TargetKind Kind, string Owner, string Repo)[]
        {
            ("https://github.com/torvalds/linux", TargetKind.Repo, "torvalds", "linux"),
            ("github.com/yt-dlp/yt-dlp", TargetKind.Repo, "yt-dlp", "yt-dlp"),
            ("yt-dlp/yt-dlp", TargetKind.Repo, "yt-dlp", "yt-dlp"),
            ("git@github.com:foo/bar.git", TargetKind.Repo, "foo", "bar"),
            ("https://github.com/o/r/tree/main/src", TargetKind.Tree, "o", "r"),
            ("https://github.com/o/r/blob/master/a/b.py", TargetKind.File, "o", "r"),
            ("https://raw.githubusercontent.com/o/r/main/x.sh", TargetKind.File, "o", "r"),
            ("https://github.com/o/r/releases", TargetKind.Releases, "o", "r"),
            ("https://github.com/o/r/releases/tag/v1.2.3", TargetKind.Releases, "o", "r"),
            ("https://github.com/o/r/releases/latest", TargetKind.Releases, "o", "r"),
            ("https://github.com/o/r/releases/download/v1/app.zip", TargetKind.Asset, "o", "r"),
            ("https://github.com/o/r/archive/refs/heads/main.zip", TargetKind.Archive, "o", "r"),
            ("https://codeload.github.com/o/r/zip/refs/heads/dev", TargetKind.Archive, "o", "r"),
            ("https://gist.github.com/user/abc123", TargetKind.Gist, "user", "abc123"),
            ("  https://github.com/foo/bar/  ", TargetKind.Repo, "foo", "bar"),
        };

        int ok = 0;
        foreach (var (input, kind, owner, repo) in cases)
        {
            try
            {
                var t = GitHubUrlParser.Parse(input);
                if (t.Kind == kind && t.Owner == owner && t.Repo == repo) ok++;
                else Console.WriteLine($"     ! {input} → {t.Kind} {t.Owner}/{t.Repo}");
            }
            catch (Exception e)
            {
                Console.WriteLine($"     ! {input} → 异常 {e.GetType().Name}: {e.Message}");
            }
        }
        Check($"{cases.Length} 种链接全部正确解析", ok == cases.Length, $"{ok}/{cases.Length}");

        var blob = GitHubUrlParser.Parse("https://github.com/o/r/blob/main/a/b.py");
        Check("blob 拆分出 ref 与多级路径", blob.Ref == "main" && blob.Path == "a/b.py",
            $"{blob.Ref} | {blob.Path}");

        var blobBranch = GitHubUrlParser.Parse("https://github.com/o/r/blob/dev/x.md");
        Check("单段分支识别正确", blobBranch.Ref == "dev" && blobBranch.SuggestedName == "x.md");

        Check("默认分支归档 URL",
            GitHubUrlParser.ArchiveUrl("o", "r", null) == "https://codeload.github.com/o/r/zip/HEAD");
        Check("指定分支归档 URL 折叠 refs/heads",
            GitHubUrlParser.ArchiveUrl("o", "r", "refs/heads/main") ==
            "https://codeload.github.com/o/r/zip/refs/heads/main");
        Check("raw URL 构造",
            GitHubUrlParser.RawUrl("o", "r", "main", "/a.txt") ==
            "https://raw.githubusercontent.com/o/r/main/a.txt");

        int rejected = 0;
        foreach (var bad in new[] { "", "hello world", "https://gitlab.com/a/b" })
        {
            try { GitHubUrlParser.Parse(bad); }
            catch (ParseException) { rejected++; }
        }
        Check("拒绝非法输入", rejected == 3, $"{rejected}/3");
    }

    private static void TestHumanize()
    {
        Section("格式化与 URL 分类");

        Check("大小格式化", Humanize.Size(1536) == "1.50 KB", Humanize.Size(1536));
        Check("时间格式化", Humanize.Eta(3725) == "1:02:05", Humanize.Eta(3725));
        Check("无法估算时返回占位", Humanize.Eta(-1) == "--:--");

        Check("分类 raw",
            UrlClassifier.Classify("https://raw.githubusercontent.com/o/r/m/a") == UrlKind.Raw);
        Check("分类 archive",
            UrlClassifier.Classify("https://codeload.github.com/o/r/zip/HEAD") == UrlKind.Archive);
        Check("分类 asset",
            UrlClassifier.Classify("https://github.com/o/r/releases/download/v1/a.zip") == UrlKind.Asset);
        Check("分类 api", UrlClassifier.Classify("https://api.github.com/repos/o/r") == UrlKind.Api);

        // codeload → archive 改写（给返回 403 的镜像用）
        string? conv = UrlClassifier.CodeloadToArchive(
            "https://codeload.github.com/curl/curl/zip/refs/heads/master");
        Check("codeload 改写为 archive",
            conv == "https://github.com/curl/curl/archive/refs/heads/master.zip", conv ?? "null");

        // 线路拼装
        var prefix = new Mirror { Id = "p", Name = "p", Kind = MirrorKind.Prefix, Arg = "https://ghfast.top/" };
        Check("前缀线路拼接",
            prefix.Build("https://raw.githubusercontent.com/o/r/m/a") ==
            "https://ghfast.top/https://raw.githubusercontent.com/o/r/m/a");

        var jsd = new Mirror
        {
            Id = "j", Name = "j", Kind = MirrorKind.JsDelivr, Arg = "cdn.jsdelivr.net",
            Scopes = new[] { UrlKind.Raw },
        };
        Check("jsDelivr 转换",
            jsd.Build("https://raw.githubusercontent.com/o/r/main/a.txt") ==
            "https://cdn.jsdelivr.net/gh/o/r@main/a.txt");
        Check("jsDelivr 不处理归档",
            jsd.Build("https://codeload.github.com/o/r/zip/HEAD") == null);

        var arch = new Mirror
        {
            Id = "a", Name = "a", Kind = MirrorKind.PrefixArchive, Arg = "https://ghfast.top/",
            Scopes = new[] { UrlKind.Archive },
        };
        Check("归档专用线路自动改写",
            arch.Build("https://codeload.github.com/o/r/zip/refs/heads/main") ==
            "https://ghfast.top/https://github.com/o/r/archive/refs/heads/main.zip");
    }




    /// <summary>
    /// XAML 资源类型检查：Brush 属性不能被赋 Color 资源。
    ///
    /// <para><b>为什么值得单独立一条断言</b>：这个 bug 已经出现过<b>两次</b>
    /// （一次在输入框模板、一次在图标按钮的 hover 态），两次都是编译通过、
    /// 启动也不报错，只在<b>鼠标移上去触发 Trigger 的瞬间</b>抛
    /// <c>XamlParseException: "#FF333A47" 不是属性 "BorderBrush" 的有效值</c>。
    /// 靠人工点界面发现它的成本太高，静态扫一遍只要几毫秒。</para>
    /// </summary>
    private static void TestXamlResourceTypes()
    {
        Section("XAML 资源类型（Color vs Brush）");

        // 从输出目录向上找仓库根（含 .sln 的那一层）。
        // 只在开发/CI 环境跑得通；发布后源码不在，跳过即可。
        string? root = null;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++)
        {
            if (dir.GetFiles("YunX.sln").Length > 0) { root = dir.FullName; break; }
            dir = dir.Parent;
        }

        if (root == null)
        {
            Console.WriteLine("     · 源码目录不可用，跳过（发布环境属正常）");
            return;
        }

        string desktop = Path.Combine(root, "src", "YunX.Desktop");
        var xamls = Directory.GetFiles(desktop, "*.xaml", SearchOption.AllDirectories);
        Check("找到 XAML 文件", xamls.Length >= 3, xamls.Length + " 个");

        // Brush 类属性
        string[] brushProps = { "BorderBrush", "Background", "Fill", "Foreground", "Stroke" };

        // 匹配「Brush 属性 = {StaticResource XxxColor}」和跨行的 Setter 形态
        var inlineBad = new System.Text.RegularExpressions.Regex(
            @"(BorderBrush|Background|Fill|Foreground|Stroke)\s*=\s*""\{(?:Static|Dynamic)Resource\s+\w+Color\}");
        var setterBrushProp = new System.Text.RegularExpressions.Regex(
            @"Property=""(BorderBrush|Background|Fill|Foreground|Stroke)""");
        var colorValue = new System.Text.RegularExpressions.Regex(
            @"Value=""\{(?:Static|Dynamic)Resource\s+\w+Color\}");

        // 反向：Color 资源 / SolidColorBrush.Color 引用了 Brush
        var reverseBad = new System.Text.RegularExpressions.Regex(
            @"(<Color x:Key=""\w+"">\s*\{(?:Static|Dynamic)Resource)|(\bColor=""\{(?:Static|Dynamic)Resource\s+\w+Brush\})");

        var offenders = new List<string>();

        foreach (var file in xamls)
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                if (inlineBad.IsMatch(line))
                    offenders.Add(Path.GetFileName(file) + ":" + (i + 1) + "  " + line.Trim());

                if (setterBrushProp.IsMatch(line) && i + 1 < lines.Length &&
                    colorValue.IsMatch(lines[i + 1]))
                {
                    offenders.Add(Path.GetFileName(file) + ":" + (i + 1) + "  " +
                                  line.Trim() + " -> " + lines[i + 1].Trim());
                }

                if (reverseBad.IsMatch(line))
                    offenders.Add("[反向] " + Path.GetFileName(file) + ":" + (i + 1) + "  " +
                                  line.Trim());
            }
        }

        foreach (var o in offenders) Console.WriteLine("     ! " + o);
        Check("Brush 属性未误用 Color 资源（双向）", offenders.Count == 0,
            offenders.Count + " 处");

        // ---------- 引用的 StaticResource 必须真的存在 ----------
        // 这类错误编译期完全查不出（XAML 的 {StaticResource} 是运行期解析），
        // 一直到用户点开那个页面才抛「找不到资源」，然后整块 UI 加载失败。
        // 本轮就新增过 IconFile，正好用这条守住。
        var defined = new HashSet<string>(StringComparer.Ordinal);
        var defineRx = new System.Text.RegularExpressions.Regex(@"x:Key=""([^""]+)""");
        var useRx = new System.Text.RegularExpressions.Regex(
            @"\{StaticResource\s+([^\}]+)\}");

        foreach (var file in xamls)
        {
            foreach (System.Text.RegularExpressions.Match m in
                     defineRx.Matches(File.ReadAllText(file)))
                defined.Add(m.Groups[1].Value.Trim());
        }

        // 系统内置资源（Brushes.xxx、SystemColors 等）不在我们的字典里，白名单放行
        string[] builtinPrefixes = { "System", "Brushes.", "Colors.", "Fonts." };
        var missing = new List<string>();

        foreach (var file in xamls)
        {
            var lines = File.ReadAllLines(file);
            for (int i = 0; i < lines.Length; i++)
            {
                foreach (System.Text.RegularExpressions.Match m in useRx.Matches(lines[i]))
                {
                    string key = m.Groups[1].Value.Trim();
                    if (key.StartsWith("ComponentResourceKey", StringComparison.Ordinal)) continue;
                    if (builtinPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal))) continue;
                    if (defined.Contains(key)) continue;

                    missing.Add($"{Path.GetFileName(file)}:{(i + 1)}  {key}");
                }
            }
        }

        foreach (var m in missing.Distinct().Take(12)) Console.WriteLine("     ! " + m);
        Check("引用的 StaticResource 都已定义", missing.Count == 0,
            missing.Count == 0 ? "" : missing.Count + " 处未定义");

        // ---------- 横向按钮组之间必须有间距 ----------
        // WPF 的 StackPanel 没有 Spacing 属性，「全选」「清空」两个按钮不写 Margin
        // 就会挤成一个词。这类问题编译期查不出、日志里看不见，只能盯着界面发现。
        // 这里静态扫一遍：同一个横向 StackPanel 里除第一个以外的 Button 必须带 Margin。
        var stackRx = new System.Text.RegularExpressions.Regex(
            @"<StackPanel[^>]*Orientation=""Horizontal""");
        var crowded = new List<string>();

        foreach (var file in xamls)
        {
            // 窗口按钮（最小化/最大化/关闭）连排是 Windows 惯例，白名单跳过
            var lines2 = File.ReadAllLines(file);
            for (int i = 0; i < lines2.Length; i++)
            {
                if (!stackRx.IsMatch(lines2[i])) continue;

                // 找到该 StackPanel 的结束行（按深度配对）
                int depth = 0, end = i;
                for (int j = i; j < lines2.Length; j++)
                {
                    depth += System.Text.RegularExpressions.Regex.Matches(lines2[j], "<StackPanel").Count;
                    depth -= System.Text.RegularExpressions.Regex.Matches(lines2[j], "</StackPanel>").Count;
                    if (depth == 0 && j > i) { end = j; break; }
                }

                // 窗口按钮那组：chrome 属性可能写在开标签的后续行，也可能在上一行。
                // 直接看整段范围里有没有该属性更可靠。
                string block = string.Join("\n", lines2.Skip(i).Take(end - i + 1));
                if (block.Contains("IsHitTestVisibleInChrome", StringComparison.Ordinal)) continue;

                int btnIndex = 0;
                for (int k = i; k <= end; k++)
                {
                    if (!lines2[k].Contains("<Button", StringComparison.Ordinal)) continue;

                    // 收集该 Button 的完整开标签：从 <Button 到遇到 '>' 为止
                    var tagBuilder = new System.Text.StringBuilder();
                    for (int t = k; t <= Math.Min(k + 10, end); t++)
                    {
                        string cur = lines2[t];
                        tagBuilder.Append(cur);
                        // 该行的 '>' 若为标签结束（不在属性值里），则停止
                        int gt = cur.IndexOf('>');
                        if (gt >= 0)
                        {
                            // 简单判定：'>' 之后没有其它内容，或紧跟 '<'，视为开标签结束
                            string after = cur[(gt + 1)..].Trim();
                            if (after.Length == 0 || after.StartsWith("<", StringComparison.Ordinal))
                                break;
                        }
                    }
                    string tag = tagBuilder.ToString();

                    btnIndex++;
                    if (btnIndex == 1) continue;      // 第一个不需要左边距

                    if (!tag.Contains("Margin=", StringComparison.Ordinal))
                        crowded.Add($"{Path.GetFileName(file)}:{k + 1} 第 {btnIndex} 个按钮无 Margin");
                }
            }
        }

        foreach (var c in crowded.Take(10)) Console.WriteLine("     ! " + c);
        Check("横向按钮组未出现紧贴（除窗口按钮外）", crowded.Count == 0,
            crowded.Count == 0 ? "" : crowded.Count + " 处");
    }


    /// <summary>本轮新增的三个网盘：识别、提取码、签名算法格式。</summary>
    private static void TestNewDrives()
    {
        Section("新增网盘（蓝奏 / 115 / 光鸭 / 星海口令）");

        var client = new System.Net.Http.HttpClient();
        var registry = Core.Cloud.CloudDriveFactory.Create(client, _ => null);

        // ---------- 蓝奏：域名族很杂，必须全覆盖 ----------
        var lanzou = registry.Find(Core.Cloud.CloudPlatforms.Lanzou);
        Check("蓝奏适配器已注册", lanzou != null);

        var lanzouCases = new[]
        {
            "https://pan.lanzoui.com/iAbCdEf123",
            "https://wwx.lanzoux.com/b0123456",
            "https://www.lanzou.com/iXyZ789",
            "https://ww1.lanzouf.com/iQQ11",
            "https://lanzoup.com/iAA22",
        };
        int lzOk = 0;
        foreach (var url in lanzouCases)
        {
            if (lanzou!.CanHandle(url) && lanzou.ParseShareLink(url)?.ShareId.Length > 0) lzOk++;
            else Console.WriteLine($"     ! 未识别 {url}");
        }
        Check($"{lanzouCases.Length} 种蓝奏域名可识别", lzOk == lanzouCases.Length, $"{lzOk}/{lanzouCases.Length}");

        Check("蓝奏非蓝奏域名不误认",
            !lanzou!.CanHandle("https://pan.baidu.com/s/1abc"));

        // 域名族正则不能把 lanzou 前缀的假域名当成蓝奏
        Check("蓝奏拒绝仿冒域名",
            !lanzou.CanHandle("https://lanzou.evil-site.com/i123"));

        // acw_sc__v2：40 位 hex 入 → 40 位 hex 出，且与输入不同
        const string arg1 = "0123456789ABCDEF0123456789ABCDEF01234567";
        var acw = Core.Cloud.Drives.LanzouDrive.AcwScV2(arg1);
        Check("acw_sc__v2 输出 40 位 hex", acw.Length == 40 && acw.All(Uri.IsHexDigit), acw);
        Check("acw_sc__v2 与输入不同", acw != arg1.ToLowerInvariant());
        Check("acw_sc__v2 稳定可复现", acw == Core.Cloud.Drives.LanzouDrive.AcwScV2(arg1));

        // ---------- 115 ----------
        var p115 = registry.Find(Core.Cloud.CloudPlatforms.Pan115);
        Check("115 适配器已注册", p115 != null);

        var s115a = p115!.ParseShareLink("https://115.com/s/sws8lxs36jf");
        Check("115 主域解析", s115a?.ShareId == "sws8lxs36jf", s115a?.ShareId ?? "null");

        var s115b = p115.ParseShareLink("https://115cdn.com/s/swabc12345?password=a1b2");
        Check("115cdn 域 + password 参数",
            s115b?.ShareId == "swabc12345" && s115b.Passcode == "a1b2",
            $"{s115b?.ShareId}/{s115b?.Passcode}");

        var s115c = p115.ParseShareLink("https://115rc.com/s/swxyz98765");
        Check("115rc 域解析", s115c?.ShareId == "swxyz98765", s115c?.ShareId ?? "null");

        // 口令形态：中划线后直接是提取码，没有 /s/
        var s115d = p115.ParseShareLink("https://115.com/sws8lxs36jf-n307/");
        Check("115 口令形态（code-pwd）",
            s115d?.ShareId == "sws8lxs36jf" && s115d.Passcode == "n307",
            $"{s115d?.ShareId}/{s115d?.Passcode}");

        // 提取码可能只在文案里
        var s115e = p115.ParseShareLink("链接 https://115.com/s/swqwe12345 访问码：k9m2");
        Check("115 从文案取访问码", s115e?.Passcode == "k9m2", s115e?.Passcode ?? "null");

        Check("115 非 115 链接不误认",
            !p115.CanHandle("https://pan.quark.cn/s/abc123"));

        // ---------- 光鸭 ----------
        var gy = registry.Find(Core.Cloud.CloudPlatforms.GuangYa);
        Check("光鸭适配器已注册", gy != null);

        var gy1 = gy!.ParseShareLink("https://www.guangyapan.com/s/AbC-123_xyz");
        Check("光鸭分享 ID 提取", gy1?.ShareId == "AbC-123_xyz", gy1?.ShareId ?? "null");

        var gy2 = gy.ParseShareLink("https://www.guangyapan.com/s/xyz789?pwd=ab12");
        Check("光鸭从 ?pwd= 取提取码", gy2?.Passcode == "ab12", gy2?.Passcode ?? "null");

        Check("光鸭非本站链接不误认",
            !gy.CanHandle("https://pan.quark.cn/s/abc123"));

        // ---------- 星海：不是网盘，只是口令 → pwdId ----------
        Check("口令判定：纯中文算口令",
            Core.Cloud.StarSeaCommandResolver.LooksLikeCommand("绝对有效 咐置松君杂货铺叩苓"));
        Check("口令判定：含链接不算",
            !Core.Cloud.StarSeaCommandResolver.LooksLikeCommand("看看 https://pan.quark.cn/s/x"));
        Check("口令判定：超长不算",
            !Core.Cloud.StarSeaCommandResolver.LooksLikeCommand(new string('口', 200)));

        Check("星海合成夸克链接",
            Core.Cloud.StarSeaCommandResolver.ShareLinkFor("qk", "abc123")
                == "https://pan.quark.cn/s/abc123");
        Check("星海合成 UC 链接",
            Core.Cloud.StarSeaCommandResolver.ShareLinkFor("UC", "abc123")
                == "https://drive.uc.cn/s/abc123");
        Check("星海未知平台兜底夸克",
            Core.Cloud.StarSeaCommandResolver.ShareLinkFor("", "abc123")
                == "https://pan.quark.cn/s/abc123");

        // 注册表里应有 9 个平台
        Check("平台总数 9 个", registry.All.Count == 9, registry.All.Count.ToString());

        // 识别优先级：蓝奏不能抢走 115 的链接
        Check("识别优先级：115 链接不被蓝奏吞",
            registry.Detect("https://115.com/s/sws8lxs36jf")?.Platform == Core.Cloud.CloudPlatforms.Pan115);
    }

    /// <summary>浏览器 Cookie 导入：不能崩，找不到要给明确原因。</summary>
    private static void TestBrowserCookies()
    {
        Section("浏览器 Cookie 导入");

        // 用一个绝不存在的域名，验证「找不到」路径给出可读原因而不是抛异常
        var none = Core.Windows.BrowserCookieReader.Import(new[] { "no-such-domain-xyz-123.invalid" });
        Check("不存在的域返回 0 条", none.Count == 0, none.Count.ToString());
        Check("不存在的域给出原因文案", !string.IsNullOrEmpty(none.Note), none.Note ?? "null");

        // 真实域名：有浏览器登录态就能取到，没有也不该崩
        var real = Core.Windows.BrowserCookieReader.Import(new[] { "quark.cn", "115.com", "baidu.com" });
        Check("真实域调用不抛异常", true,
            real.Count > 0 ? $"取到 {real.Count} 条（{real.Source}）" : $"0 条（{real.Note}）");

        if (real.Count > 0)
        {
            Check("Cookie 头格式含等号", real.CookieHeader.Contains('='));
            Check("Cookie 头无换行", !real.CookieHeader.Contains('\n'));
        }
    }

    /// <summary>分享链接与提取码提取（对应原项目「自动匹配提取码」）。</summary>
    private static void TestCloudParsing()
    {
        Section("网盘链接 / 提取码解析");

        // 原项目很实用的能力：从整段文本里同时抠出链接和提取码
        const string text = "我用夸克网盘分享了「某资源」，点击链接即可保存。\n" +
                            "链接：https://pan.quark.cn/s/abc123def456  提取码：k9m2";
        var (url, pwd) = ShareTextParser.Parse(text);
        Check("从整段文本提取 URL", url == "https://pan.quark.cn/s/abc123def456", url ?? "null");
        Check("从整段文本提取提取码", pwd == "k9m2", pwd ?? "null");

        var (_, pwd2) = ShareTextParser.Parse("https://pan.baidu.com/s/1abcd 密码: 8x2z");
        Check("识别「密码:」写法", pwd2 == "8x2z", pwd2 ?? "null");

        var (_, pwd3) = ShareTextParser.Parse("https://pan.baidu.com/s/1abcd?pwd=zz99");
        Check("识别 query 中的 pwd", pwd3 == "zz99", pwd3 ?? "null");

        // 裸 4 位码要能识别，但不能把年份当提取码
        var (_, pwd4) = ShareTextParser.Parse("链接 https://pan.xunlei.com/s/xyz 提取码 abcd");
        Check("识别无冒号的提取码", pwd4 == "abcd", pwd4 ?? "null");

        Check("中文口令识别",
            ShareTextParser.LooksLikeChineseCommand("绝对有效 咐置松君杂货铺叩苓"));
        Check("含链接的不算口令",
            !ShareTextParser.LooksLikeChineseCommand("看看 https://pan.baidu.com/s/1x"));

        // 各平台的链接解析
        var registry = Core.Cloud.CloudDriveFactory.Create(new System.Net.Http.HttpClient(),
            _ => null);

        var cases = new (string Input, string Platform, string Id)[]
        {
            ("https://pan.quark.cn/s/abc123def456", Core.Cloud.CloudPlatforms.Quark, "abc123def456"),
            ("https://drive.uc.cn/s/uc12345678", Core.Cloud.CloudPlatforms.UC, "uc12345678"),
            ("https://www.123pan.com/s/pan123abc", Core.Cloud.CloudPlatforms.Pan123, "pan123abc"),
            ("https://pan.baidu.com/s/1abcdefg", Core.Cloud.CloudPlatforms.Baidu, "1abcdefg"),
            ("https://pan.xunlei.com/s/xl12345678", Core.Cloud.CloudPlatforms.Xunlei, "xl12345678"),
        };

        int ok = 0;
        foreach (var (input, platform, id) in cases)
        {
            var drive = registry.Detect(input);
            if (drive == null)
            {
                Console.WriteLine($"     ! {input} → 未识别");
                continue;
            }
            var share = drive.ParseShareLink(input);
            if (drive.Platform == platform && share?.ShareId == id) ok++;
            else Console.WriteLine($"     ! {input} → {drive.Platform}/{share?.ShareId}");
        }
        Check($"{cases.Length} 种网盘链接正确识别", ok == cases.Length, $"{ok}/{cases.Length}");

        // 迅雷 captcha 签名是 10 层 md5，验证它稳定且格式正确
        var sign = Core.Cloud.Drives.XunleiDrive.BuildCaptchaSign(
            "0123456789abcdef0123456789abcdef01234567", "1700000000000");
        Check("迅雷 captcha_sign 格式", sign.StartsWith("1.") && sign.Length == 2 + 32,
            sign[..Math.Min(10, sign.Length)] + "…");

        // 123 签名头
        var (ak, av) = Core.Cloud.Drives.Pan123Drive.MakeSign("/b/api/share/download/info",
            1700000000);
        Check("123 auth-key 为 8 位十六进制", ak.Length <= 8 && ak.All(Uri.IsHexDigit), ak);
        Check("123 auth-value 三段式", av.Count(c => c == '-') == 2, av);

        // 139 的 mcloud-sign
        var (h139, _) = Core.Cloud.Drives.C139Drive.SignHeader("{\"a\":1}");
        Check("139 mcloud-sign 三段式", h139.Count(c => c == ',') == 2);
    }

    /// <summary>凭证加密：三档保护等级 + 换密钥后不可解。</summary>
    private static void TestCredentialCipher()
    {
        Section("凭证加密（AES-256-GCM + 三档密钥保护）");

        string dir = Path.Combine(Path.GetTempPath(), "yunx_cipher_" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (var level in new[]
                     {
                         Core.Security.KeyProtectionLevel.Portable,
                         Core.Security.KeyProtectionLevel.MachineBound,
                         Core.Security.KeyProtectionLevel.UserBound,
                     })
            {
                string sub = Path.Combine(dir, level.ToString());
                var cipher = new Core.Security.CredentialCipher(sub, level);
                cipher.Initialize();

                const string secret = "BDUSS=abc123; STOKEN=xyz789; 中文也要能加密";
                string enc = cipher.Encrypt(secret);
                string? dec = cipher.Decrypt(enc);

                Check($"{level} 加解密往返", dec == secret);
                Check($"{level} 密文不是明文", !enc.Contains("BDUSS"));
            }

            // 换密钥后旧密文必须解不开（而不是返回垃圾）
            string d1 = Path.Combine(dir, "rotate1");
            var c1 = new Core.Security.CredentialCipher(d1,
                Core.Security.KeyProtectionLevel.Portable);
            c1.Initialize();
            string enc1 = c1.Encrypt("hello");

            string d2 = Path.Combine(dir, "rotate2");
            var c2 = new Core.Security.CredentialCipher(d2,
                Core.Security.KeyProtectionLevel.Portable);
            c2.Initialize();
            Check("不同密钥解不开旧密文", c2.Decrypt(enc1) == null);

            // 篡改密文应被 GCM 认证拒绝
            byte[] raw = Convert.FromBase64String(enc1);
            raw[^1] ^= 0xFF;
            Check("篡改密文被拒绝", c1.Decrypt(Convert.ToBase64String(raw)) == null);

            // 切换保护等级只重新包裹主密钥，数据仍可解
            var c3 = new Core.Security.CredentialCipher(Path.Combine(dir, "level"),
                Core.Security.KeyProtectionLevel.Portable);
            c3.Initialize();
            string data = c3.Encrypt("cross-level");
            c3.ChangeLevel(Core.Security.KeyProtectionLevel.MachineBound);
            Check("切换等级后旧数据仍可解", c3.Decrypt(data) == "cross-level");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    /// <summary>数据库：任务持久化、历史去重、收藏、凭证。</summary>
    private static void TestDatabase()
    {
        Section("SQLite 持久化");

        string dir = Path.Combine(Path.GetTempPath(), "yunx_db_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string dbPath = Path.Combine(dir, "test.db");

        try
        {
            using var db = Core.Data.AppDatabase.OpenWithHealing(dbPath);
            var tasks = new Core.Data.TaskRepository(db);

            var t = new Core.Data.PersistedTask
            {
                Url = "https://example.com/a.zip",
                FileName = "a.zip",
                SaveDir = dir,
                TotalBytes = 1000,
                DoneBytes = 400,
                State = Core.Data.PersistedTaskState.Paused,
            };
            tasks.Upsert(t);
            Check("任务写入并获得自增 ID", t.Id > 0, t.Id.ToString());

            var restored = tasks.LoadRestorable();
            Check("未完成任务可恢复", restored.Count == 1 && restored[0].DoneBytes == 400,
                $"{restored.Count} 条");

            tasks.UpdateProgress(t.Id, 800, 1000);
            var after = tasks.LoadRestorable();
            Check("进度更新生效", after[0].DoneBytes == 800, after[0].DoneBytes.ToString());

            // ===== 已完成任务的历史保留（v1.0 修复：原先完成即 DELETE，历史全丢）=====
            var doneTask = new Core.Data.PersistedTask
            {
                Url = "https://example.com/done.zip",
                FileName = "done.zip",
                SaveDir = dir,
                TotalBytes = 2048,
                DoneBytes = 2048,
                State = Core.Data.PersistedTaskState.Done,
            };
            tasks.Upsert(doneTask);
            Check("已完成任务能写入库", doneTask.Id > 0, doneTask.Id.ToString());

            Check("已完成任务不参与恢复", tasks.LoadRestorable().All(x => x.State != Core.Data.PersistedTaskState.Done),
                "恢复列表里不应出现 Done");

            var completed = tasks.LoadCompleted(10);
            Check("已完成任务可当历史读出", completed.Count == 1 && completed[0].FileName == "done.zip",
                $"{completed.Count} 条");

            // 取消也要参与恢复（取消时断点是刻意保留的）
            var cancelled = new Core.Data.PersistedTask
            {
                Url = "https://example.com/c.zip",
                FileName = "c.zip",
                SaveDir = dir,
                TotalBytes = 500,
                DoneBytes = 100,
                State = Core.Data.PersistedTaskState.Cancelled,
            };
            tasks.Upsert(cancelled);
            Check("已取消任务仍可恢复（断点保留）",
                tasks.LoadRestorable().Any(x => x.State == Core.Data.PersistedTaskState.Cancelled),
                "取消应当可续传");

            // ===== 只保留最近 N 条已完成 =====
            foreach (var i in Enumerable.Range(1, 5))
            {
                tasks.Upsert(new Core.Data.PersistedTask
                {
                    Url = $"https://example.com/h{i}.zip",
                    FileName = $"h{i}.zip",
                    SaveDir = dir,
                    TotalBytes = 100,
                    DoneBytes = 100,
                    State = Core.Data.PersistedTaskState.Done,
                });
                Thread.Sleep(5);   // 让 updated_at 有区分度，排序才确定
            }
            Check("已完成共 6 条（1 条 done + 5 条 h）", tasks.LoadCompleted(100).Count == 6,
                tasks.LoadCompleted(100).Count.ToString());

            int pruned = tasks.PruneCompleted(3);
            var kept = tasks.LoadCompleted(100);
            Check("裁剪到最近 3 条", kept.Count == 3, $"{kept.Count} 条");
            Check("裁剪返回删除数量", pruned == 3, pruned.ToString());
            // 注意：updated_at 是**秒级**时间戳，同一秒内插入的多条记录时间相同。
            // 所以"最近"靠 id 兜底排序（id 单调递增，与插入顺序一致）。
            // 这里断言"保留的是 id 最大的三条"，而不是按文件名猜。
            Check("保留的是最后写入的 h3/h4/h5",
                kept.Select(x => x.FileName).OrderBy(x => x).SequenceEqual(new[] { "h3.zip", "h4.zip", "h5.zip" }),
                string.Join(",", kept.Select(x => x.FileName)));
            Check("裁剪不影响未完成任务",
                tasks.LoadRestorable().Count(x => x.State != Core.Data.PersistedTaskState.Done) >= 2,
                "未完成/已取消的应还在");

            // 历史去重：同链接 1 小时内不重复
            var history = new Core.Data.HistoryRepository(db);
            history.Add("https://a/1", "repo", "标题A", "abcd");
            history.Add("https://a/1", "repo", "标题A2", "abcd");
            var hist = history.Query(null);
            Check("同链接 1 小时内去重", hist.Count == 1, $"{hist.Count} 条");
            Check("去重时更新标题", hist[0].Title == "标题A2", hist[0].Title);
            Check("历史保留提取码", hist[0].Passcode == "abcd");

            history.Add("https://a/2", "repo", "标题B", "");
            Check("不同链接分别记录", history.Query(null).Count == 2);
            Check("历史搜索", history.Query("标题B").Count == 1);
            history.Clear();
            Check("清空历史", history.Query(null).Count == 0);

            // ===== 链接规范化去重（v1.0：不再只靠 1 小时窗口）=====
            // 同一个仓库的不同写法必须算同一条，否则历史里全是重复项
            history.Add("https://github.com/rust-lang/rust", "Repo", "rust @ master", "");
            history.Add("https://github.com/rust-lang/rust/", "Repo", "rust @ master", "");
            history.Add("http://github.com/rust-lang/rust", "Repo", "rust @ master", "");
            history.Add("https://www.github.com/rust-lang/rust", "Repo", "rust @ master", "");
            Check("同链接的 4 种写法只记 1 条",
                history.Query(null).Count == 1, $"{history.Query(null).Count} 条");

            // 路径不同则是不同记录，不能被误合并
            history.Add("https://github.com/rust-lang/rust/releases", "Releases", "rust · Release", "");
            Check("带 /releases 视为不同记录",
                history.Query(null).Count == 2, $"{history.Query(null).Count} 条");

            // 主机名大小写不敏感
            history.Add("https://GitHub.com/rust-lang/rust", "Repo", "rust @ master", "");
            Check("主机名大小写不影响去重",
                history.Query(null).Count == 2, $"{history.Query(null).Count} 条");

            // 规范化函数本身的断言
            Check("NormalizeKey 去协议/斜杠/www",
                Core.Data.HistoryRepository.NormalizeKey("https://www.a.com/b/") == "a.com/b",
                Core.Data.HistoryRepository.NormalizeKey("https://www.a.com/b/"));
            Check("NormalizeKey 保留路径语义",
                Core.Data.HistoryRepository.NormalizeKey("https://a.com/b") !=
                Core.Data.HistoryRepository.NormalizeKey("https://a.com/c"));
            Check("NormalizeKey magnet 只留 infohash",
                Core.Data.HistoryRepository.NormalizeKey(
                    "magnet:?xt=urn:btih:ABCDEF0123456&dn=电影") == "magnet:abcdef0123456",
                Core.Data.HistoryRepository.NormalizeKey(
                    "magnet:?xt=urn:btih:ABCDEF0123456&dn=电影"));

            history.Clear();
            Check("清空历史（规范化用例收尾）", history.Query(null).Count == 0);

            // 收藏
            var fav = new Core.Data.FavoriteRepository(db);
            fav.Add("https://fav/1", "常用");
            Check("收藏写入", fav.Exists("https://fav/1"));
            Check("收藏去重", fav.Add("https://fav/1", "改名") && fav.List().Count == 1);
            fav.MarkUsed("https://fav/1");
            Check("使用次数累加", fav.List()[0].UseCount == 1);
            Check("取消收藏", fav.Remove("https://fav/1") && !fav.Exists("https://fav/1"));

            // 收藏的显示名：没标题时压成短名，否则侧边栏只能看到被截断的 "https://github.com/…"
            fav.Add("https://github.com/rust-lang/rust", "");
            Check("无标题时 GitHub 链接压成 owner/repo",
                fav.List()[0].DisplayLabel == "rust-lang/rust", fav.List()[0].DisplayLabel);
            fav.Remove("https://github.com/rust-lang/rust");

            fav.Add("https://github.com/BurntSushi/ripgrep/releases/latest", "");
            Check("带深层路径也只取 owner/repo",
                fav.List()[0].DisplayLabel == "BurntSushi/ripgrep", fav.List()[0].DisplayLabel);
            fav.Remove("https://github.com/BurntSushi/ripgrep/releases/latest");

            fav.Add("https://pan.quark.cn/s/abc123", "");
            Check("非 GitHub 链接去掉协议前缀",
                fav.List()[0].DisplayLabel == "pan.quark.cn/s/abc123", fav.List()[0].DisplayLabel);
            fav.Remove("https://pan.quark.cn/s/abc123");

            // 历史脏数据：旧版本把输入框占位符当标题存进去了，显示层要兜住
            fav.Add("https://github.com/torvalds/linux", "等待输入链接");
            Check("占位符标题自动兜底为短名",
                fav.List()[0].DisplayLabel == "torvalds/linux", fav.List()[0].DisplayLabel);
            fav.Remove("https://github.com/torvalds/linux");

            // 语义已变更：只有**用户自定义**的名字才优先显示，程序顺带写入的 label 不再算数
            // （用户要的是"自定义了就显示自定义，否则显示网址"，不拿程序猜的标题冒充）
            fav.Add("https://github.com/nodejs/node", "已解析的真实标题");
            Check("非自定义的 label 不参与显示，仍显示网址短名",
                fav.List()[0].DisplayLabel == "nodejs/node", fav.List()[0].DisplayLabel);
            fav.Remove("https://github.com/nodejs/node");

            // ===== 自定义命名（v1.0 新增）=====
            fav.Add("https://github.com/golang/go", "");
            Check("未命名时显示网址短名",
                fav.List()[0].DisplayLabel == "golang/go", fav.List()[0].DisplayLabel);
            Check("未命名时 LabelCustom=false", !fav.List()[0].LabelCustom);

            Check("重命名成功", fav.Rename("https://github.com/golang/go", "Go 语言源码"));
            var renamed = fav.List()[0];
            Check("显示自定义名", renamed.DisplayLabel == "Go 语言源码", renamed.DisplayLabel);
            Check("标记为自定义", renamed.LabelCustom);

            // 关键：重新收藏同一个链接不能把用户起的名覆盖掉
            fav.Add("https://github.com/golang/go", "自动生成的短名");
            Check("重新收藏不覆盖自定义名",
                fav.List()[0].DisplayLabel == "Go 语言源码", fav.List()[0].DisplayLabel);

            // 清空自定义名 → 退回网址
            fav.Rename("https://github.com/golang/go", "");
            Check("清除自定义名后显示网址",
                fav.List()[0].DisplayLabel == "golang/go", fav.List()[0].DisplayLabel);
            Check("清除后标记回到非自定义", !fav.List()[0].LabelCustom);

            // 重命名不存在的收藏应返回 false
            Check("重命名不存在的收藏返回 false",
                !fav.Rename("https://nonexistent/x", "随便"));

            // 清空全部
            fav.Add("https://a/1", "一");
            fav.Add("https://a/2", "二");
            int cleared = fav.Clear();
            Check("清空全部收藏", cleared >= 2 && fav.List().Count == 0, $"删除 {cleared} 条");

            // 凭证（加密）
            var cipher = new Core.Security.CredentialCipher(dir,
                Core.Security.KeyProtectionLevel.Portable);
            cipher.Initialize();
            var creds = new Core.Data.CredentialRepository(db, cipher);
            creds.Save("quark", "Cookie=abc", "extra-info");
            var loaded = creds.Load("quark");
            Check("凭证加密存取", loaded?.Value == "Cookie=abc", loaded?.Value ?? "null");
            Check("凭证 extra 字段", loaded?.Extra == "extra-info");
            Check("凭证平台列表", creds.Platforms().Contains("quark"));
            creds.Delete("quark");
            Check("删除凭证", creds.Load("quark") == null);

            // 认证备份导出/导入
            creds.Save("baidu", "BDUSS=xxx");
            string backup = Path.Combine(dir, "auth.yunx");
            Core.Data.AuthBackup.Export(db, cipher, "test-password", backup);
            Check("备份文件生成", File.Exists(backup) && new FileInfo(backup).Length > 100);

            creds.Delete("baidu");
            Check("导出后已清空", creds.Load("baidu") == null);
            int n = Core.Data.AuthBackup.Import(db, cipher, "test-password", backup);
            Check("备份导入恢复凭证", n == 1 && creds.Load("baidu")?.Value == "BDUSS=xxx",
                $"恢复 {n} 个");

            bool wrongRejected = false;
            try { Core.Data.AuthBackup.Import(db, cipher, "wrong-password", backup); }
            catch (InvalidDataException) { wrongRejected = true; }
            Check("错误口令被拒绝", wrongRejected);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    /// <summary>Markdown 解析（README 渲染）。</summary>
    private static void TestMarkdown()
    {
        Section("Markdown 解析（README 渲染）");

        var parser = new GitHub.MarkdownParser();

        const string md = """
            # 标题一

            这是一个段落，含 **粗体**、*斜体*、`行内代码` 和 [链接](https://example.com)。

            ## 标题二

            ```csharp
            var x = 1;
            ```

            - 项目一
            - 项目二

            > 这是引用

            | 列A | 列B |
            | --- | ---: |
            | 1 | 2 |

            ---

            ![图片](https://example.com/a.png)
            """;

        var blocks = parser.Parse(md);
        Check("解析出多个块", blocks.Count >= 8, $"{blocks.Count} 块");

        var headings = blocks.Where(b => b.Kind == GitHub.MdBlockKind.Heading).ToList();
        Check("识别标题级别", headings.Count == 2 && headings[0].Level == 1 &&
                          headings[1].Level == 2);
        Check("标题文本正确", headings[0].Text == "标题一", headings[0].Text);

        var code = blocks.FirstOrDefault(b => b.Kind == GitHub.MdBlockKind.CodeBlock);
        Check("代码块带语言", code?.Language == "csharp", code?.Language ?? "null");
        Check("代码块内容", code?.Text.Contains("var x = 1") == true);

        var list = blocks.FirstOrDefault(b => b.Kind == GitHub.MdBlockKind.List);
        Check("无序列表", list != null && !list.OrderedList && list.Rows.Count == 2,
            $"{list?.Rows.Count} 项");

        var quote = blocks.FirstOrDefault(b => b.Kind == GitHub.MdBlockKind.Quote);
        Check("引用块", quote?.Text.Contains("这是引用") == true);

        var table = blocks.FirstOrDefault(b => b.Kind == GitHub.MdBlockKind.Table);
        Check("表格行列", table != null && table.Rows.Count == 2 && table.Rows[0].Count == 2,
            $"{table?.Rows.Count} 行");
        Check("表格对齐解析", table?.Alignments.Count == 2 && table.Alignments[1] == "right",
            string.Join(",", table?.Alignments ?? new List<string>()));

        Check("分割线", blocks.Any(b => b.Kind == GitHub.MdBlockKind.Rule));
        Check("独立图片", blocks.Any(b => b.Kind == GitHub.MdBlockKind.Image));

        // 行内格式
        var inlines = GitHub.MarkdownParser.ParseInline(
            "前 **粗** 中 *斜* 后 `码` [链](http://a)");
        Check("行内粗体", inlines.Any(i => i.Kind == GitHub.MdInlineKind.Bold && i.Text == "粗"));
        Check("行内斜体", inlines.Any(i => i.Kind == GitHub.MdInlineKind.Italic && i.Text == "斜"));
        Check("行内代码", inlines.Any(i => i.Kind == GitHub.MdInlineKind.Code && i.Text == "码"));
        Check("行内链接", inlines.Any(i => i.Kind == GitHub.MdInlineKind.Link && i.Url == "http://a"));

        // 相对图片地址改写
        string rewritten = GitHub.MarkdownParser.RewriteRelativeUrls(
            "![](./docs/a.png) 和 ![x](https://cdn/x.png)", "o", "r", "main");
        Check("相对图片改写为 raw 绝对地址",
            rewritten.Contains("raw.githubusercontent.com/o/r/main/docs/a.png"));
        Check("绝对地址不改写", rewritten.Contains("https://cdn/x.png"));
    }

    /// <summary>
    /// 检查「计算属性漏通知」这一类 bug。
    ///
    /// <para><b>为什么用源码检查而不是单元测试</b>：这类缺陷发生在 WPF 绑定层 ——
    /// setter 改了 backing field 却忘了 <c>OnPropertyChanged(nameof(计算属性))</c>，
    /// 界面就永远显示旧值。它编译期无警告、日志里无痕迹，只有盯着界面才看得出。
    /// 本项目就真实踩过两次：</para>
    /// <list type="bullet">
    ///   <item><c>Phase</c> 不通知 <c>StateText</c> → 状态永远停在「探测中」；</item>
    ///   <item><c>ResultPath</c> 是自动属性 → 下载完成后「打开」按钮不出现。</item>
    /// </list>
    ///
    /// <para>Smoke 是控制台项目、不引用 WPF 的 Desktop 工程，没法直接实例化 VM，
    /// 所以退一步做源码级契约检查：只要这些 setter 里出现了对应的通知语句就算通过。
    /// 不够优雅，但能挡住回归，且零依赖。</para>
    /// </summary>
    private static void TestViewModelNotifications()
    {
        Section("视图模型通知契约");

        string vmPath = FindSourceFile("YunX.Desktop", "ViewModels",
            "DownloadTaskViewModel.cs");
        if (vmPath.Length == 0)
        {
            Check("找到 DownloadTaskViewModel.cs", false, "源码文件缺失，跳过");
            return;
        }

        string src = File.ReadAllText(vmPath);

        Check("Phase setter 连带通知 StateText",
            HasNotificationNear(src, "public string Phase", "nameof(StateText)"));

        Check("ResultPath setter 连带通知 HasResult",
            HasNotificationNear(src, "public string? ResultPath", "nameof(HasResult)"));

        Check("State setter 通知 CanResume（继续按钮可见性）",
            HasNotificationNear(src, "public TaskState State", "nameof(CanResume)"));

        Check("Done/Speed 变更会刷新派生文本",
            HasNotificationNear(src, "public long Done", "NotifyDerived"));

        // ---------- 任务生命周期的不变量（源码契约） ----------
        // MainViewModel 依赖 WPF，Smoke 是控制台项目引用不了。但这一轮修的几个
        // 阻断性缺陷都有很明确的代码特征，用文本断言守住回归是划算的。
        string vmMain = FindSourceFile("YunX.Desktop", "ViewModels", "MainViewModel.cs");
        if (vmMain.Length == 0)
        {
            Check("找到 MainViewModel.cs", false, "源码文件缺失，跳过");
            return;
        }
        string mv = File.ReadAllText(vmMain);

        // P0-1：暂停后继续必须能跑 —— 判据不能再是「CTS 已取消」
        Check("RunTaskAsync 用显式状态判断能否执行",
            mv.Contains("if (vm.Cancelled || vm.RemoveRequested) return;", StringComparison.Ordinal));
        // 只匹配**代码行**，不匹配注释：修复说明里会引用旧写法做对照，
        // 直接 Contains 会把它误判成"缺陷还在"。
        Check("RunTaskAsync 不再用旧 CTS 是否取消来拦截",
            !mv.Split('\n').Any(l =>
                !l.TrimStart().StartsWith("//", StringComparison.Ordinal)
                && l.Contains("if (vm.Cts?.IsCancellationRequested == true) return;",
                    StringComparison.Ordinal)));
        Check("每轮执行都新建 CTS（取消后不复用）",
            mv.Contains("vm.Cts = new CancellationTokenSource();", StringComparison.Ordinal));

        // P1-8：排队任务取消/删除必须有独立于 CTS 的信号
        Check("CancelTask 立显式取消标志",
            mv.Contains("vm.Cancelled = true;", StringComparison.Ordinal));
        Check("DeleteTask 立显式移除标志",
            mv.Contains("vm.RemoveRequested = true;", StringComparison.Ordinal));

        // P0-2：持久化主键必须用 DbId，不能拿进程内 TaskId
        Check("PersistTask 用 DbId 作为数据库主键",
            HasNotificationNear(mv, "private void PersistTask", "Id = vm.DbId"));
        Check("PersistTask 不再静默吞异常",
            !mv.Contains("// 持久化失败不应中断下载", StringComparison.Ordinal));

        // P1-7：暂停/取消不能触发网盘转存清理
        Check("暂停不触发分享清理",
            mv.Contains("TaskState.Paused or TaskState.Cancelled", StringComparison.Ordinal));

        // 恢复：必须还原上下文，且不能一律 Queued
        Check("恢复时还原断点路径等上下文",
            mv.Contains("PartPath = payload?.PartPath", StringComparison.Ordinal));
        Check("恢复不再一律设为 Queued",
            !mv.Contains("TaskState.Queued,\n                };", StringComparison.Ordinal));

        // 输入变化必须让**所有**来源类型的旧结果失效。
        // 只看 _lastResolved 是不够的：磁力与网盘都不写它，
        // 结果是"改了输入框还能按旧结果下载"，下错内容。
        Check("输入失效判据覆盖磁力/网盘",
            mv.Contains("|| _magnetUrl.Length > 0", StringComparison.Ordinal)
            && mv.Contains("|| _lastCloud != null", StringComparison.Ordinal));

        // 磁力必须与内置引擎分派开：BT 不是 HTTP 分片
        Check("磁力走独立执行路径",
            mv.Contains("IsMagnetSource", StringComparison.Ordinal)
            && mv.Contains("RunMagnetTaskAsync", StringComparison.Ordinal));

        // ---------- 启动不被 PBKDF2 卡住 ----------
        // CredentialCipher.Initialize 要做 210,000 轮 PBKDF2（纯 CPU，数百毫秒）。
        // 它必须**不能**在 UI 线程上跑：SettingsStore 是字段初始化器、AppServices 在
        // InitializeAsync 里 new —— 两处都得避开同步构造。
        string storeSrc = FindSourceFile("YunX.Desktop", "Services", "SettingsStore.cs");
        if (storeSrc.Length > 0)
        {
            string st = File.ReadAllText(storeSrc);
            Check("SettingsStore 不在构造里同步初始化加密器",
                !st.Contains("Cipher.Initialize();", StringComparison.Ordinal)
                || st.Contains("private YunX.Core.Security.CredentialCipher? GetCipher()",
                    StringComparison.Ordinal),
                "应为懒初始化（GetCipher）");
        }

        Check("AppServices 在线程池上构造（避开 PBKDF2 阻塞 UI）",
            mv.Contains("Task.Run(() => new AppServices(", StringComparison.Ordinal));
        Check("纯本地数据先于联网探测装配",
            mv.IndexOf("BuildCloudAccountRows();", StringComparison.Ordinal) >= 0
            && mv.IndexOf("BuildCloudAccountRows();", StringComparison.Ordinal)
               < mv.IndexOf("await _provider.SetModeAsync(", StringComparison.Ordinal));
        Check("网络栈未就绪时解析给出提示而非空引用",
            mv.Contains("NetworkStackReady", StringComparison.Ordinal));
    }

    /// <summary>
    /// 判断某个属性定义之后、下一个属性定义之前，是否出现了指定的通知调用。
    /// 用「下一行起 60 行窗口」做为界定，足够覆盖这些短 setter。
    /// </summary>
    private static bool HasNotificationNear(string src, string memberSig, string notify)
    {
        int at = src.IndexOf(memberSig, StringComparison.Ordinal);
        if (at < 0) return false;

        // 到下一个 XML 文档注释或下一个 public 成员为止
        int next = src.IndexOf("\n    public ", at + memberSig.Length,
            StringComparison.Ordinal);
        string window = next > at
            ? src[at..next]
            : src[at..Math.Min(src.Length, at + 2000)];

        return window.Contains(notify, StringComparison.Ordinal);
    }

    /// <summary>从当前工作目录向上找到 src/&lt;project&gt;/&lt;sub&gt;/&lt;file&gt;。</summary>
    private static string FindSourceFile(string project, string sub, string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", project, sub, file);
            if (File.Exists(candidate)) return candidate;
        }
        return "";
    }

    /// <summary>
    /// 任务持久化的往返：INSERT → 回写主键 → UPDATE → 能读回。
    ///
    /// <para><b>守住的缺陷</b>：`TaskRepository.Upsert` 的 UPDATE 语句里写了
    /// <c>updated_at=$upd</c>，但参数绑定时只加了 <c>$now</c>（只有 INSERT 分支用）。
    /// 于是只要 <c>Id &gt; 0</c> 就必然抛
    /// <c>InvalidOperationException: Must add values for the following parameters: $upd</c>，
    /// 而这个异常被上层 <c>PersistTask</c> 的空 catch 完全吞掉 ——
    /// 持久化一次都没成功过，且零日志零痕迹。</para>
    ///
    /// <para>这条测试直接覆盖 INSERT 与 UPDATE 两个分支，任何一边参数没绑全都会红。</para>
    /// </summary>
    private static void TestTaskPersistence()
    {
        Section("任务持久化（INSERT / UPDATE 往返）");

        string dbPath = Path.Combine(Path.GetTempPath(),
            "yunx-persist-" + Guid.NewGuid().ToString("N")[..8], "t.db");
        try
        {
            using var db = new Core.Data.AppDatabase(dbPath);
            var repo = new Core.Data.TaskRepository(db);

            // ---- INSERT 分支（Id = 0）----
            var row = new Core.Data.PersistedTask
            {
                Id = 0,
                Url = "https://example.com/a.bin",
                FileName = "a.bin",
                SaveDir = @"C:\dl",
                TotalBytes = 1000,
                DoneBytes = 100,
                State = Core.Data.PersistedTaskState.Paused,
                PayloadJson = "{\"k\":1}",
            };

            bool insertOk = true;
            string insertErr = "";
            try { repo.Upsert(row); }
            catch (Exception e) { insertOk = false; insertErr = e.Message; }

            Check("首次写入（Id=0）不抛异常", insertOk, insertErr);
            Check("写入后回写真实主键", row.Id > 0, $"Id={row.Id}");

            var loaded = repo.LoadRestorable();
            Check("能读回刚写入的任务", loaded.Count == 1,
                $"行数={loaded.Count}");
            Check("字段往返正确",
                loaded.Count == 1
                && loaded[0].FileName == "a.bin"
                && loaded[0].DoneBytes == 100
                && loaded[0].State == Core.Data.PersistedTaskState.Paused,
                loaded.Count == 1 ? $"{loaded[0].FileName}/{loaded[0].DoneBytes}" : "-");

            // ---- UPDATE 分支（Id > 0）—— 这里是 $upd 缺陷的现场 ----
            // 注意：状态用 Paused 而不是 Failed —— LoadRestorable 只取 state IN (0,1,2)，
            // Failed 会被过滤掉，那会让「更新没落库」和「落库了但恢复时被过滤」混淆。
            row.DoneBytes = 900;
            row.State = Core.Data.PersistedTaskState.Paused;
            bool updateOk = true;
            string updateErr = "";
            try { repo.Upsert(row); }
            catch (Exception e) { updateOk = false; updateErr = e.Message; }

            Check("按主键更新（Id>0）不抛异常（$upd 必须绑定）", updateOk, updateErr);

            var after = repo.LoadRestorable();
            Check("更新真的落库（不是 0 行受影响）",
                after.Count == 1 && after[0].DoneBytes == 900,
                after.Count == 1 ? $"done={after[0].DoneBytes}" : $"行数={after.Count}");

            // ---- 不存在的 Id 不应炸，只是没有行被改 ----
            bool ghostOk = true;
            string ghostErr = "";
            try
            {
                repo.Upsert(new Core.Data.PersistedTask
                {
                    Id = 99999, Url = "ghost", FileName = "g.bin", SaveDir = @"C:\dl",
                    State = Core.Data.PersistedTaskState.Paused,
                });
            }
            catch (Exception e) { ghostOk = false; ghostErr = e.Message; }

            Check("更新不存在的 Id 不抛异常", ghostOk, ghostErr);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, true); } catch { }
        }
    }

    /// <summary>
    /// 磁力链接识别与解析。
    ///
    /// <para>磁力不是标准 URL：没有 authority 段，<c>?</c> 后是 <c>&amp;</c> 连接的参数，
    /// <c>dn</c> 里可能带空格与中文。这些用例直接覆盖真实磁力的各种形态，
    /// 并验证「不会把普通 URL 误判成磁力」。</para>
    /// </summary>
    private static void TestMagnetLink()
    {
        Section("磁力链接解析");

        const string hex40 = "C12FE1C06BBA254A9A8F1A4B7D3E5C9A0B1D2E3F";

        // ---- 识别 ----
        Check("识别标准磁力", Core.Download.MagnetLink.IsMagnet(
            $"magnet:?xt=urn:btih:{hex40}&dn=Test"));
        Check("识别大小写混写的 scheme", Core.Download.MagnetLink.IsMagnet(
            $"MAGNET:?xt=urn:btih:{hex40}"));
        Check("前后空白不影响识别", Core.Download.MagnetLink.IsMagnet(
            $"  magnet:?xt=urn:btih:{hex40}  "));
        Check("光杆 magnet: 不算", !Core.Download.MagnetLink.IsMagnet("magnet:"));
        Check("普通 https 链接不算", !Core.Download.MagnetLink.IsMagnet("https://a.com/x"));
        Check("空值不算", !Core.Download.MagnetLink.IsMagnet("") &&
                          !Core.Download.MagnetLink.IsMagnet(null));

        // ---- info hash ----
        Check("提取 40 位 hex info hash",
            Core.Download.MagnetLink.InfoHash($"magnet:?xt=urn:btih:{hex40}") == hex40);

        const string base32 = "MFRGGZDFMZTWQ2LKNNWG23TPOBYXE43U";
        Check("提取 32 位 base32 info hash",
            Core.Download.MagnetLink.InfoHash($"magnet:?xt=urn:btih:{base32}&dn=x") == base32);

        // ---- 显示名 ----
        Check("优先用 dn 作显示名",
            Core.Download.MagnetLink.DisplayName($"magnet:?xt=urn:btih:{hex40}&dn=Ubuntu%2024.04")
            == "Ubuntu 24.04");

        Check("dn 里的换行压成空格",
            !Core.Download.MagnetLink.DisplayName(
                $"magnet:?xt=urn:btih:{hex40}&dn=a%0Ab%0Dc").Contains('\n'));

        string noDn = Core.Download.MagnetLink.DisplayName($"magnet:?xt=urn:btih:{hex40}");
        Check("没有 dn 时用 hash 前缀占位",
            noDn.StartsWith("磁力_", StringComparison.Ordinal) && noDn.Length > 3, noDn);

        string longDn = Core.Download.MagnetLink.DisplayName(
            $"magnet:?xt=urn:btih:{hex40}&dn=" + new string('x', 200));
        Check("超长 dn 被截断（不撑爆界面）", longDn.Length <= 80, longDn.Length + " 字符");

        // ---- 去重 key ----
        Check("同一磁力（不同 dn/tracker）归一化后相同",
            Core.Download.MagnetLink.Normalize($"magnet:?xt=urn:btih:{hex40}&dn=A&tr=udp://a")
            == Core.Download.MagnetLink.Normalize($"magnet:?xt=urn:btih:{hex40}&dn=B&tr=udp://b"));
        Check("归一化大小写不敏感",
            Core.Download.MagnetLink.Normalize($"magnet:?xt=urn:btih:{hex40}")
            == Core.Download.MagnetLink.Normalize($"magnet:?xt=urn:btih:{hex40.ToLowerInvariant()}"));
        Check("不同 info hash 不会被当成同一任务",
            Core.Download.MagnetLink.Normalize($"magnet:?xt=urn:btih:{hex40}")
            != Core.Download.MagnetLink.Normalize($"magnet:?xt=urn:btih:{new string('A', 40)}"));

        // ---- 结构校验：非法 hash 绝不能提交给引擎 ----
        // Gopeed 底层的 anacrolix/torrent 遇到全零/残缺 info hash 会 panic 退出，
        // 连带弄死同引擎上正在跑的其它任务。实测踩到过。
        Check("合法 hex hash 通过结构校验",
            Core.Download.MagnetLink.IsStructurallyValid($"magnet:?xt=urn:btih:{hex40}"));
        Check("全零 hash 被拒（会让引擎 panic）",
            !Core.Download.MagnetLink.IsStructurallyValid(
                "magnet:?xt=urn:btih:0000000000000000000000000000000000000000"));
        Check("长度不对的 hash 被拒",
            !Core.Download.MagnetLink.IsStructurallyValid("magnet:?xt=urn:btih:ABCDEF"));
        Check("没有 xt 的磁力被拒",
            !Core.Download.MagnetLink.IsStructurallyValid("magnet:?dn=onlyname"));
        Check("合法 base32 hash 通过",
            Core.Download.MagnetLink.IsStructurallyValid($"magnet:?xt=urn:btih:{base32}"));
    }

    /// <summary>实验性参数的双档范围校验。</summary>
    private static void TestTuningRanges()
    {
        Section("实验性参数范围校验");

        var ok = Core.Download.DownloadTuning.CheckReadBuffer(96);
        Check("96KB 在合理范围", ok.Ok && ok.WithinReasonable);

        var warn = Core.Download.DownloadTuning.CheckReadBuffer(2048);
        Check("2048KB 可启用但超合理范围", warn.Ok && !warn.WithinReasonable);

        var reject = Core.Download.DownloadTuning.CheckReadBuffer(9999);
        Check("9999KB 超硬上限被拒", !reject.Ok);

        var f1 = Core.Download.DownloadTuning.CheckPreemptFloor(12);
        Check("12KB/s 阈值合理", f1.Ok && f1.WithinReasonable);
        var f2 = Core.Download.DownloadTuning.CheckPreemptFloor(5000);
        Check("5000KB/s 超合理范围", f2.Ok && !f2.WithinReasonable);
        var f3 = Core.Download.DownloadTuning.CheckPreemptFloor(99999);
        Check("99999KB/s 超硬上限", !f3.Ok);

        var a1 = Core.Download.DownloadTuning.CheckPreemptAge(15);
        Check("15 秒判定时长合理", a1.Ok && a1.WithinReasonable);
        var a2 = Core.Download.DownloadTuning.CheckPreemptAge(300);
        Check("300 秒超合理范围", a2.Ok && !a2.WithinReasonable);

        // ClampToHardLimits 兜底
        var t = new Core.Download.DownloadTuning
        {
            ReadBufferBytes = 999 * 1024 * 1024,
            PreemptFloorBps = 999_999 * 1024,
        };
        t.ClampToHardLimits();
        Check("越界值被裁剪到硬上限", t.ReadBufferBytes <= 4096 * 1024);
    }

    // =====================================================================
    //  在线验证
    // =====================================================================

    private static async Task RunLiveTestsAsync(bool includeTailExperiment)
    {
        Section("网络环境检测");

        var provider = new HttpClientProvider();
        await HostsBypass.EnableAsync(m => Console.WriteLine("  " + m)).ConfigureAwait(false);
        await provider.SetModeAsync(NetworkMode.Auto, m => Console.WriteLine("  " + m))
            .ConfigureAwait(false);

        var pool = new MirrorPool(MirrorRegistry.CreateDefault(),
            provider.CreateClient(forDownload: true), provider);

        var api = new GitHubApiClient(provider, () => null)
        {
            Log = m => Console.WriteLine("  [api] " + m),
        };
        api.OnUnauthorized = () => Console.WriteLine("  [api] 401 自愈触发");

        var resolver = new ContentResolver(api, provider)
        {
            Log = m => Console.WriteLine("  [res] " + m),
        };

        Section("线路测速");
        const string sampleUrl =
            "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";

        var ranked = await pool.RankAsync(sampleUrl, force: true,
            log: m => Console.WriteLine("  " + m)).ConfigureAwait(false);

        foreach (var r in ranked.Take(6))
        {
            Console.WriteLine($"     {r.MirrorName,-24} {(r.Ok ? "OK " : "ERR")} " +
                              $"{Humanize.Speed(r.Speed),12}  lat={r.Latency:0.00}s {r.Error}");
        }
        Check("至少一条线路可用", ranked.Any(r => r.Ok),
            $"{ranked.Count(r => r.Ok)}/{ranked.Count} 可用");

        // ★ 回归防护：太慢的线路必须排在正常线路之后。
        //
        //   探测只发 Range: bytes=0-0（1 字节），拿到 206 就判"可用"。
        //   实测存在这种线路：1 字节能秒回、下 512KB 要 32 秒 —— 它"可用"
        //   却会把 8 个 worker 全拖死，用户看到的就是"几百 K 在爬"。
        //   判据见 ProbeResult.UnusableBps。
        var ordered = await pool.OrderedUrlsAsync(sampleUrl, force: false).ConfigureAwait(false);
        int firstSlow = ordered.FindIndex(u =>
            ranked.FirstOrDefault(r => r.MirrorName == u.Name) is { } rr
            && rr.Ok && !rr.Tiny && rr.Speed < ProbeResult.UnusableBps);
        int lastFast = -1;
        for (int i = 0; i < ordered.Count; i++)
        {
            var rr = ranked.FirstOrDefault(r => r.MirrorName == ordered[i].Name);
            if (rr is { Ok: true, Tiny: false } && rr.Speed >= ProbeResult.UnusableBps)
                lastFast = i;
        }
        Check("太慢的线路排在正常线路之后",
            firstSlow < 0 || lastFast < 0 || firstSlow > lastFast,
            $"慢线首个位置={firstSlow}，快线最后位置={lastFast}");

        Section("链接解析（真实 API）");
        foreach (var input in new[]
                 {
                     "https://github.com/octocat/Hello-World",
                     "https://github.com/octocat/Hello-World/blob/master/README",
                     "https://github.com/yt-dlp/yt-dlp/releases",
                 })
        {
            try
            {
                var content = await resolver.ResolveAsync(input, pool).ConfigureAwait(false);
                Console.WriteLine($"     {content.Title}");
                Console.WriteLine($"       → {content.Items.Count} 项，{content.Note}");
                Check($"解析 {input.Split('/').Take(5).Last()}", content.Items.Count > 0);
            }
            catch (Exception e)
            {
                Check($"解析 {input}", false, $"{e.GetType().Name}: {e.Message}");
            }
        }

        Section("真实下载（17MB，8 线程）");
        string outDir = Path.Combine(Path.GetTempPath(), "yunx_csharp_test");
        Directory.CreateDirectory(outDir);

        var timings = new List<(long Start, long Size, double At)>();
        var opts = new DownloadOptions
        {
            Threads = 8,
            PreemptSlowChunks = true,
            OnChunkCompleted = (start, size, at) =>
            {
                lock (timings) timings.Add((start, size, at));
            },
        };

        var engine = new DownloadEngine(provider, pool);
        var lastLog = DateTime.UtcNow;

        var result = await engine.DownloadAsync(
            sampleUrl, outDir, "yt-dlp-test.exe", opts,
            new Progress<DownloadProgress>(p =>
            {
                if ((DateTime.UtcNow - lastLog).TotalMilliseconds < 800) return;
                lastLog = DateTime.UtcNow;
                Console.WriteLine($"     {p.Phase} {p.Percent,5:0.0}%  " +
                                  $"{Humanize.Size(p.BytesDone)}/{Humanize.Size(p.TotalBytes)}  " +
                                  $"{Humanize.Speed(p.SpeedBytesPerSec)}  " +
                                  $"ETA {Humanize.Eta(p.EtaSeconds)}");
            }),
            m => Console.WriteLine("     " + m)).ConfigureAwait(false);

        Console.WriteLine();
        Console.WriteLine($"   大小 {Humanize.Size(result.Size)}  耗时 {result.ElapsedSeconds:0.0}s  " +
                          $"平均 {Humanize.Speed(result.AverageSpeedBps)}");
        Console.WriteLine($"   模式 {result.Mode}  块数 {result.ChunkCount}  " +
                          $"线路 {result.MirrorName}  换连接 {result.Preemptions} 次");

        Check("文件已落地", File.Exists(result.FilePath));
        Check("大小与远端一致", result.Size == 17_840_399, $"{result.Size}");
        Check("PE 魔数正确（无错位）", await IsMzAsync(result.FilePath).ConfigureAwait(false));
        Check("无 .part 残留", !File.Exists(result.FilePath + ".part"));
        Check("无 .part.json 残留", !File.Exists(result.FilePath + ".part.json"));
        Check("走了分块多线程", result.Mode == "multi", result.Mode);

        // ---- 收尾塌缩分析 ----
        if (timings.Count > 0)
        {
            AnalyzeTail(timings, result.ElapsedSeconds);
        }

        if (includeTailExperiment)
        {
            await RunPreemptComparisonAsync(provider, pool, sampleUrl, outDir).ConfigureAwait(false);
        }
    }

    /// <summary>分析分片完成时刻分布，量化「收尾塌缩」。</summary>
    private static void AnalyzeTail(List<(long Start, long Size, double At)> timings, double wall)
    {
        Section("收尾塌缩分析");

        var sorted = timings.OrderBy(t => t.At).ToList();
        Console.WriteLine($"   共 {sorted.Count} 个分片完成：");
        foreach (var (start, size, at) in sorted)
        {
            double pct = at / wall * 100;
            Console.WriteLine($"     起点 {start,12:N0}  块 {Humanize.Size(size),10}  " +
                              $"完成于 {at,6:0.0}s ({pct,5:0.0}%)");
        }

        if (sorted.Count < 4) return;

        var times = sorted.Select(t => t.At).ToList();
        int idx90 = Math.Max(0, (int)(times.Count * 0.9) - 1);
        double p90 = times[idx90];
        double last = times[^1];
        double tailShare = (last - p90) / Math.Max(last, 0.001);

        Console.WriteLine();
        Console.WriteLine($"   90% 分片在 {p90:0.0}s 前完成，总耗时 {last:0.0}s");
        Console.WriteLine($"   最后 10% 分片占全程 {tailShare * 100:0}%");
        Check("收尾占比 < 25%（Python 版实测为 31%）", tailShare < 0.25,
            $"{tailShare * 100:0}%");
    }

    /// <summary>
    /// 对照实验：关掉慢连接抢占 vs 打开，看耗时与收尾差异。
    /// </summary>
    private static async Task RunPreemptComparisonAsync(HttpClientProvider provider,
        MirrorPool pool, string url, string outDir)
    {
        Section("慢连接抢占 A/B 对照");

        foreach (bool preempt in new[] { false, true })
        {
            string name = $"ab-{(preempt ? "on" : "off")}.exe";
            string target = Path.Combine(outDir, name);
            if (File.Exists(target)) File.Delete(target);

            var timings = new List<(long, long, double)>();
            var opts = new DownloadOptions
            {
                Threads = 8,
                PreemptSlowChunks = preempt,
                OnChunkCompleted = (s, sz, at) => { lock (timings) timings.Add((s, sz, at)); },
            };

            var engine = new DownloadEngine(provider, pool);
            var r = await engine.DownloadAsync(url, outDir, name, opts,
                null, null).ConfigureAwait(false);

            double tailShare = 0;
            if (timings.Count >= 4)
            {
                var t = timings.OrderBy(x => x.Item3).Select(x => x.Item3).ToList();
                int i90 = Math.Max(0, (int)(t.Count * 0.9) - 1);
                tailShare = (t[^1] - t[i90]) / Math.Max(t[^1], 0.001);
            }

            Console.WriteLine($"   抢占={(preempt ? "开" : "关"),-3} " +
                              $"耗时 {r.ElapsedSeconds,6:0.0}s  " +
                              $"平均 {Humanize.Speed(r.AverageSpeedBps),12}  " +
                              $"换连接 {r.Preemptions} 次  " +
                              $"收尾占比 {tailShare * 100,3:0}%");
        }

        Console.WriteLine("   （注入的慢连接是随机的，两次结果有波动属正常）");
    }

    private static async Task<bool> IsMzAsync(string path)
    {
        await using var fs = File.OpenRead(path);
        var buf = new byte[2];
        int n = await fs.ReadAsync(buf.AsMemory(0, 2)).ConfigureAwait(false);
        return n == 2 && buf[0] == 0x4D && buf[1] == 0x5A;   // "MZ"
    }
}
