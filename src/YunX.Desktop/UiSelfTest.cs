using System.IO;
using System.Text;
using System.Windows;
using YunX.Core.Util;
using YunX.Desktop.ViewModels;

namespace YunX.Desktop;

/// <summary>
/// 界面链路自检：在真实 WPF 环境下驱动 MainViewModel 走完
/// 「解析 → 入队 → 下载 → 完成」全流程，验证 UI 层逻辑（而非仅引擎）。
///
/// 用法：YunX-Desktop.exe --selftest [url]
/// </summary>
internal static class UiSelfTest
{
    private static readonly List<string> Lines = new();

    private static void Emit(string text)
    {
        Lines.Add(text);
        try { Console.WriteLine(text); } catch { /* WinExe 可能没有控制台 */ }
    }

    public static async Task<int> RunAsync(string? url)
    {
        Emit("=== 界面链路自检 ===");

        int failed = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Emit($"  [{(ok ? "OK" : "FAIL")}] {name}" + (detail.Length > 0 ? "  " + detail : ""));
            if (!ok) failed++;
        }

        // MainViewModel 的构造会启动网络初始化（在 UI 线程的 SynchronizationContext 上）
        var vm = new MainViewModel();

        string input = url ?? "https://github.com/octocat/Hello-World/blob/master/README";
        vm.InputText = input;
        Emit($"  输入: {input}");

        Check("输入后 HasParsed 为 false", !vm.HasParsed);
        Check("解析命令可用", vm.ParseCommand.CanExecute(null));

        // 等到日志里出现"网络"相关初始化完成（最多 20s）
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && vm.NetModeText.Contains("检测中"))
            await Task.Delay(200);
        Check("网络初始化完成", !vm.NetModeText.Contains("检测中"), vm.NetModeText);

        // ---- 解析 ----
        vm.ParseCommand.Execute(null);
        deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline && !vm.HasParsed)
            await Task.Delay(200);

        Check("解析成功", vm.HasParsed, vm.ParsedTitle);
        Emit($"     类型: {vm.ParsedKind}");
        Emit($"     标题: {vm.ParsedTitle}");
        Emit($"     备注: {vm.ParsedNote}");

        if (!vm.HasParsed)
        {
            DumpLog(vm);
            return 1;
        }

        // ---- 下载 ----
        Check("下载命令可用", vm.DownloadCommand.CanExecute(null));

        int before = vm.Tasks.Count;
        vm.DownloadCommand.Execute(null);
        deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && vm.Tasks.Count == before)
            await Task.Delay(200);

        Check("任务已入队", vm.Tasks.Count > before, $"{vm.Tasks.Count} 个");
        Check("空状态提示已隐藏", vm.Tasks.Count > 0 && vm.HasTasks);

        if (vm.Tasks.Count > before)
        {
            var task = vm.Tasks[^1];
            Emit($"     文件: {task.FileName}");
            Emit($"     初始状态: {task.StateText}");

            deadline = DateTime.UtcNow.AddSeconds(120);
            while (DateTime.UtcNow < deadline && task.IsRunning)
                await Task.Delay(300);

            Emit($"     最终状态: {task.StateText}");
            Emit($"     进度: {task.ProgressText}");
            Emit($"     结果路径: {task.ResultPath ?? "(无)"}");

            Check("任务完成", task.State == TaskState.Done, task.Error);
            Check("结果文件存在",
                task.ResultPath != null && File.Exists(task.ResultPath),
                task.ResultPath ?? "");
            if (task.ResultPath != null && File.Exists(task.ResultPath))
            {
                long size = new FileInfo(task.ResultPath).Length;
                Check("文件非空", size > 0, Humanize.Size(size));
            }
        }

        // ---- 网盘解析接通性 ----
        //
        // 这一组不是在验证「能不能真的下到网盘文件」（那需要真实分享与本机登录态），
        // 而是验证一件关键的事：**网盘链接不再被当成 GitHub 仓库**。
        // 早期版本里网盘适配器从未被 UI 调用，粘一个夸克链接会去查
        // api.github.com/repos/pan.quark.cn/s 并报「仓库不存在（404）」。
        Emit("");
        Emit("  ---- 网盘解析接通性 ----");

        // 用一个结构合法但几乎必然不存在的分享 ID，期望得到「网盘专属」的错误
        vm.InputText = "https://pan.quark.cn/s/zz00nonexist00zz";
        vm.ParseCommand.Execute(null);

        deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline && vm.ParsedKind == "解析中…")
            await Task.Delay(200);

        Emit($"     类型: {vm.ParsedKind}");
        Emit($"     标题: {vm.ParsedTitle}");

        Check("夸克链接未被当成 GitHub 仓库",
            !vm.ParsedTitle.Contains("仓库不存在"),
            vm.ParsedTitle);

        bool recognizdAsCloud =
            vm.ParsedKind.Contains("夸克") || vm.ParsedKind.Contains("需要") ||
            vm.ParsedKind.Contains("失败") || vm.ParsedKind.Contains("异常") ||
            vm.ParsedKind.Contains("无法识别");
        Check("夸克链接走的是网盘链路", recognizdAsCloud, vm.ParsedKind);

        // 错误信息必须是**面向用户的中文提示**，不能是 .NET 的技术异常文本。
        // 这一条曾经真的抓到过 bug：Content-Type 拼错导致 HttpClient 抛
        // "The format of value 'application/json;charset=UTF-8' is invalid."，
        // 而当时上面那条「未被当成 GitHub 仓库」因为异常文本里也没有"仓库不存在"，
        // 是侥幸通过的 —— 所以这里必须加语言判定。
        bool hasChinese = vm.ParsedTitle.Any(c => c >= 0x4E00 && c <= 0x9FFF);
        Check("错误信息是中文用户提示（非技术异常）", hasChinese, vm.ParsedTitle);
        Check("错误信息不含技术异常痕迹",
            !vm.ParsedTitle.Contains("Exception") &&
            !vm.ParsedTitle.Contains("System.") &&
            !vm.ParsedTitle.Contains("is invalid"), vm.ParsedTitle);

        // 非网盘输入仍应回落到 GitHub 链路（不能被网盘吞掉）
        vm.InputText = "octocat/Hello-World";
        vm.ParseCommand.Execute(null);
        deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline && vm.ParsedKind == "解析中…")
            await Task.Delay(200);

        Check("owner/repo 简写仍走 GitHub", !vm.IsCloudSource, vm.ParsedKind);

        // ---- 种子色推导（纯计算，与网络无关，放这里因为它属于 UI 层）----
        Emit("");
        Emit("  ---- 强调色推导 ----");

        var palette = Themes.SeedPalette.Derive(Themes.SeedPalette.DefaultSeed);
        Check("默认种子色推导出蓝色",
            palette.Base.B > palette.Base.R && palette.Base.B > palette.Base.G,
            Themes.SeedPalette.ToHex(palette.Base));
        Check("hover 比 base 亮",
            Themes.SeedPalette.ToHsl(palette.Hover).L > Themes.SeedPalette.ToHsl(palette.Base).L);
        Check("pressed 比 base 暗",
            Themes.SeedPalette.ToHsl(palette.Pressed).L < Themes.SeedPalette.ToHsl(palette.Base).L);
        Check("soft 足够暗（可作选中背景）",
            Themes.SeedPalette.ToHsl(palette.Soft).L < 0.30,
            Themes.SeedPalette.ToHex(palette.Soft));

        // 色相必须保持不变：换种子色时用户预期的就是「同一个颜色的不同明度」
        var baseH = Themes.SeedPalette.ToHsl(palette.Base).H;
        var hoverH = Themes.SeedPalette.ToHsl(palette.Hover).H;
        Check("hover 与 base 色相一致", Math.Abs(baseH - hoverH) < 1.5,
            $"{baseH:0.0} vs {hoverH:0.0}");

        // 极端输入不能产出看不出颜色或用不了的结果
        var dark = Themes.SeedPalette.Derive(System.Windows.Media.Color.FromRgb(10, 10, 10));
        Check("近黑种子色被抬亮", Themes.SeedPalette.ToHsl(dark.Base).L >= 0.40,
            Themes.SeedPalette.ToHex(dark.Base));

        // 灰色没有色相：不能被染成红色（ToHsl 对无彩色返回 h=0）
        Check("纯灰种子色不变成红色",
            Math.Abs(Themes.SeedPalette.ToHsl(dark.Base).H - 217.6) < 30,
            $"h={Themes.SeedPalette.ToHsl(dark.Base).H:0.0} {Themes.SeedPalette.ToHex(dark.Base)}");

        // 真正要保证的：强调色上的文字始终有足够对比度（WCAG AA 大字 3:1）
        foreach (var (name, seed) in new (string, System.Windows.Media.Color)[]
                 {
                     ("白", System.Windows.Media.Colors.White),
                     ("黑", System.Windows.Media.Colors.Black),
                     ("黄", System.Windows.Media.Color.FromRgb(0xE8, 0xD4, 0x4D)),
                     ("深红", System.Windows.Media.Color.FromRgb(0x7A, 0x10, 0x10)),
                     ("浅粉", System.Windows.Media.Color.FromRgb(0xFF, 0xC8, 0xDC)),
                     ("默认蓝", Themes.SeedPalette.DefaultSeed),
                 })
        {
            var p2 = Themes.SeedPalette.Derive(seed);
            double ratio = Themes.SeedPalette.ContrastRatio(p2.OnAccent, p2.Base);
            Check($"{name}种子色前景对比度 ≥ 3:1", ratio >= 3.0,
                $"{ratio:0.00}:1 前景 {Themes.SeedPalette.ToHex(p2.OnAccent)} / 底色 {Themes.SeedPalette.ToHex(p2.Base)}");
        }

        // HEX 解析容错：非法值必须回落到默认，而不是抛异常
        Check("非法 HEX 回落默认色",
            Themes.SeedPalette.Parse("not-a-color") == Themes.SeedPalette.DefaultSeed);
        Check("带 # 的 HEX 正确解析",
            Themes.SeedPalette.Parse("#22C1A4") == System.Windows.Media.Color.FromRgb(0x22, 0xC1, 0xA4));

        // ---- 设置项连通性 ----
        Emit("");
        Emit("  ---- 设置项 ----");
        Check("关闭行为有 3 个选项", vm.CloseBehaviorLabels.Length == 3);
        Check("种子色预设 6 个", vm.SeedPresets.Count == 6);
        Check("分片校验默认开启", vm.VerifyChunks);
        Check("缓存目录默认空（=下载目录）", vm.CacheDir.Length == 0, vm.CacheDirText);
        Check("数据库路径已就绪", vm.DatabasePathText.Length > 3, vm.DatabasePathText);
        Check("调试页事件流可访问", vm.DebugEvents != null);

        // 暂停语义：Paused 必须可「继续」，Cancelled 必须不可。
        // 曾经 PauseTask 先把状态设成 Paused，随后引擎抛出的取消异常又把它改写成
        // Cancelled，于是点暂停 = 点取消，「继续」按钮永远灰着。
        var probe = new DownloadTaskViewModel
        {
            Url = "https://example.com/f.bin", FileName = "f.bin", TaskId = -1,
        };
        probe.State = TaskState.Paused;
        Check("暂停后可以继续", probe.CanResume && probe.StateText == "已暂停");
        probe.State = TaskState.Cancelled;
        Check("取消后不能继续", !probe.CanResume && probe.StateText == "已取消");
        Check("暂停请求可被消费（不会串到下一次取消）",
            probe.TryHoldShareRef() && !probe.TryHoldShareRef() && probe.TryDropShareRef()
            && !probe.TryDropShareRef());

        // 托盘图标：hIcon 为空时注册出来是一块空白占位，只能靠句柄是否有效来验
        var trayIcon = YunX.Core.Windows.TrayIcon.LoadAppIcon(16);
        Check("托盘图标可从 exe 提取", trayIcon != IntPtr.Zero,
            trayIcon == IntPtr.Zero ? "hIcon=0（托盘会显示空白）" : $"hIcon=0x{trayIcon.ToInt64():X}");

        Emit("");
        DumpLog(vm);

        Emit(failed == 0 ? "\n界面链路全部通过 ✓" : $"\n失败 {failed} 项");

        // WinExe 没有控制台，把结果落盘以便外部读取
        try
        {
            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "YunX-Desktop-CS", "selftest.log");
            Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
            File.WriteAllText(logPath, string.Join(Environment.NewLine, Lines),
                new UTF8Encoding(false));
        }
        catch
        {
            // 落盘失败不影响退出码
        }

        return failed == 0 ? 0 : 1;
    }

    private static void DumpLog(MainViewModel vm)
    {
        Emit("  --- 运行日志 ---");
        foreach (var line in vm.LogLines.TakeLast(18))
            Emit("  " + line);
    }
}
