using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using YunX.Desktop.ViewModels;

namespace YunX.Desktop;

/// <summary>
/// 账号页诊断：把「数据层」和「渲染层」分开看。
///
/// <para>「列表不显示」有两类完全不同的原因，表现却一样（一片空白）：
/// ① 数据没到 —— <c>CloudAccounts</c> 本身是空的（注册表没建、凭据表读异常、
///    <c>BuildCloudAccountRows</c> 没被调）；
/// ② 数据到了但没渲染 —— ItemsControl 有 9 项，但模板里的元素被裁剪/无尺寸/
///    前景与背景同色，屏幕上什么都看不见。</para>
///
/// <para>只看截图永远区分不出这两类。所以这里同时打印
/// <c>CloudAccounts.Count</c>、每一项的字段值，以及实际渲染出的像素统计
/// （非背景像素占比）。数据层从 ViewModel 读，渲染层从 RenderTargetBitmap 读。</para>
/// </summary>
internal static class AccountsPageDiag
{
    private static double _w = 1000;
    private static double _h = 700;

    /// <summary>按指定窗口尺寸跑一遍（复现用户的实际窗口大小）。</summary>
    public static int Run(string outDir, double w = 1000, double h = 700)
    {
        _w = w;
        _h = h;
        Directory.CreateDirectory(outDir);
        int problems = 0;

        Console.WriteLine("=== 账号页诊断 ===");
        Console.WriteLine();

        MainViewModel? vm = null;
        MainWindow? win = null;

        // MainWindow 的构造会触发 ViewModel 初始化（含 BuildCloudAccountRows）。
        // 初始化里有秒级网络探测，所以给它一个宽裕的等待窗口。
        win = new MainWindow();
        vm = win.DataContext as MainViewModel;

        if (vm == null)
        {
            Console.WriteLine("[FAIL] DataContext 不是 MainViewModel");
            return 1;
        }

        // 轮询等待初始化完成（CloudAccounts 被填充）
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (DateTime.UtcNow < deadline && vm.CloudAccounts.Count == 0)
        {
            Pump();
            System.Threading.Thread.Sleep(80);
        }
        Console.WriteLine($"  等待 CloudAccounts 填充耗时 = {sw.ElapsedMilliseconds} ms");

        // 初始化失败时 LogLines 里会有「初始化服务失败」，这是唯一线索 ——
        // 静默 catch 后 return 会让账号页永远空白，却没有任何用户可见的提示。
        foreach (var line in vm.LogLines.Skip(Math.Max(0, vm.LogLines.Count - 12)))
            Console.WriteLine("    [日志] " + line);
        Pump();
        System.Threading.Thread.Sleep(300);
        Pump();

        // ---------------- 1) 数据层 ----------------
        Console.WriteLine("--- 数据层 ---");
        Console.WriteLine($"  CloudAccounts.Count = {vm.CloudAccounts.Count}");
        Console.WriteLine($"  CloudStatus         = \"{vm.CloudStatus}\"");

        if (vm.CloudAccounts.Count == 0)
        {
            Console.WriteLine("  [FAIL] 列表为空 —— 这是数据层问题，与渲染无关");
            problems++;
        }
        else
        {
            foreach (var r in vm.CloudAccounts)
            {
                Console.WriteLine($"    · {r.Platform,-10} {r.DisplayName,-8} " +
                                  $"登录={r.IsLoggedIn,-5} 状态={r.Status,-4} 提示={r.LoginHint}");
                if (string.IsNullOrWhiteSpace(r.DisplayName))
                {
                    Console.WriteLine("      [FAIL] DisplayName 为空");
                    problems++;
                }
            }
        }

        // ---------------- 2) 导航到账号页 ----------------
        vm.Page = AppPage.Accounts;
        Pump();
        System.Threading.Thread.Sleep(400);
        Pump();
        Console.WriteLine();
        Console.WriteLine($"--- 渲染层（IsAccountsPage={vm.IsAccountsPage}）---");

        // ⚠ 必须真的 Show()。
        //
        // 先前用 win.Measure/Arrange 代替，结果整页渲染成纯黑、连 ItemsControl 都找不到 ——
        // 原因是 MainWindow 用了自定义 WindowChrome + 模板，内容区挂在
        // Template 的 PART 里，**只有模板被应用（Show 触发的 Loaded 之后）才会实例化**。
        // 不 Show 就量，量到的是一个没套模板的空壳，任何控件都查不到。
        // 这类「诊断工具本身的假象」比真实缺陷更浪费时间，所以照真实路径走。
        win.WindowStartupLocation = WindowStartupLocation.Manual;
        win.Left = -20000;   // 放到屏幕外，避免打断用户
        win.Top = -20000;
        // 尺寸从命令行可覆盖：用户报的场景是 912x495 的小窗，
        // 列表项在矮窗里被压缩，与默认 1000x700 表现不同。
        win.Width = _w;
        win.Height = _h;
        win.ShowInTaskbar = false;
        win.Show();

        // 先抓「刚 Show、首次布局还没跑完」的中间态。
        //
        // ★ 这一步是必须的：如果只在完全布局好之后检查，永远看不到用户实际
        //   遇到的那一帧。本地回环 + 纯 CPU 的环境下布局是几毫秒就完成的，
        //   而用户的机器上（尤其下载在跑、dwm 不稳）可能停留好几百毫秒。
        win.UpdateLayout();
        DumpRender(win, vm, "首次布局（未等待）");

        // 等 Loaded + 模板应用 + 首个渲染帧
        for (int i = 0; i < 20; i++) { Pump(); System.Threading.Thread.Sleep(50); }
        win.UpdateLayout();
        Pump();
        DumpRender(win, vm, "布局稳定后");

        var checklist = new List<string>();
        DumpRender(win, vm, "最终检查", checklist);
        problems += checklist.Count(c => c.StartsWith("FAIL"));

        win.Close();
        Pump();

        Console.WriteLine();
        Console.WriteLine(problems == 0 ? "数据层与渲染层均正常" : $"{problems} 项异常");
        return problems == 0 ? 0 : 1;
    }

    /// <summary>
    /// 量一次当前渲染状态。每个检查点都调一次 —— 这样能区分「一直正常」和
    /// 「短暂异常后自愈」，后者正是用户看到一闪而过的空白卡片的原因。
    /// </summary>
    private static void DumpRender(Window win, MainViewModel vm, string tag,
        List<string>? checklist = null)
    {
        Console.WriteLine();
        Console.WriteLine($"--- 渲染检查：{tag} ---");

        var itemsControls = FindAll<ItemsControl>(win)
            .Where(ic => ReferenceEquals(ic.ItemsSource, vm.CloudAccounts))
            .ToList();

        Console.WriteLine($"  ItemsControl 数量 = {itemsControls.Count}");
        if (itemsControls.Count == 0)
        {
            Console.WriteLine("  [FAIL] 找不到绑定 CloudAccounts 的 ItemsControl");
            checklist?.Add("FAIL no-itemscontrol " + tag);
        }

        foreach (var ic in itemsControls)
        {
            Console.WriteLine($"    Actual={ic.ActualWidth:F1}x{ic.ActualHeight:F1} " +
                              $"Items={ic.Items.Count} Visible={ic.IsVisible} Opacity={ic.Opacity:F2}");

            int zeroSized = 0, notGenerated = 0;
            for (int i = 0; i < ic.Items.Count; i++)
            {
                var c = ic.ItemContainerGenerator.ContainerFromIndex(i) as ContentPresenter;
                if (c == null) { notGenerated++; continue; }
                c.Measure(new Size(ic.ActualWidth, double.PositiveInfinity));
                if (c.DesiredSize.Width < 1 || c.DesiredSize.Height < 1) zeroSized++;
            }
            if (notGenerated > 0)
            {
                Console.WriteLine($"    [INFO] {notGenerated} 个容器尚未生成（布局未完成）");
            }
            if (zeroSized > 0)
            {
                Console.WriteLine($"    [FAIL] {zeroSized} 个容器尺寸为 0 —— 模板产出为空");
                checklist?.Add($"FAIL zero-sized({zeroSized}) {tag}");
            }
        }

        // 像素统计
        int w = (int)Math.Max(1, win.ActualWidth);
        int h = (int)Math.Max(1, win.ActualHeight);
        var bmp = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(win);
        int stride = bmp.PixelWidth * 4;
        var px = new byte[stride * bmp.PixelHeight];
        bmp.CopyPixels(px, stride, 0);

        byte bgB = px[0], bgG = px[1], bgR = px[2];
        long nonBg = 0;
        for (int i = 0; i < px.Length; i += 4)
        {
            if (Math.Abs(px[i] - bgB) + Math.Abs(px[i + 1] - bgG) + Math.Abs(px[i + 2] - bgR) > 24)
                nonBg++;
        }
        Console.WriteLine($"    尺寸={w}x{h} 背景=#{bgR:X2}{bgG:X2}{bgB:X2} " +
                          $"非背景像素={100.0 * nonBg / (px.Length / 4):F2}%");
    }

    /// <summary>把 Dispatcher 队列跑空 —— 否则绑定/布局都还没发生就去量尺寸。</summary>
    private static void Pump()
    {
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.SystemIdle);
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T t) yield return t;
            foreach (var sub in FindAll<T>(child)) yield return sub;
        }
    }
}
