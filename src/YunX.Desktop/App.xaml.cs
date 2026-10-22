// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 Lyaco
//
// YunX-Desktop-CSharp —— 基于 YunX-Desktop（AGPL-3.0）的 C# / WPF 重构版。
// 本程序是自由软件：你可以依据 GNU AGPL-3.0 的条款重新分发或修改它。
// 完整协议见仓库根目录 LICENSE。

using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace YunX.Desktop;

public partial class App : Application
{
    /// <summary>启动/运行期致命错误的落盘位置（便于无交互环境下排查）。</summary>
    private static readonly string CrashLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "YunX-Desktop-CS", "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrash("AppDomain", args.ExceptionObject as Exception);

        // 自定义种子色：必须在建窗之前应用。
        // 主窗口的 XAML 在解析时会查找 Accent* 资源，晚了就来不及。
        // （Dark.xaml 里这些 key 都用 DynamicResource 引用，因此运行中改也能生效。）
        ApplySeedColor();

        // XAML 加载自检：只建窗口不跑业务，用于快速定位模板/资源错误
        if (e.Args.Contains("--xdump"))
        {
            try
            {
                var w = new MainWindow();
                EmitX("XAML 加载成功，标题=" + w.Title);
                w.Close();
                Shutdown(0);
            }
            catch (Exception ex)
            {
                EmitX("XAML 加载失败：");
                EmitX(ex.ToString());
                WriteCrash("XamlDump", ex);
                Shutdown(1);
            }
            return;
        }

        // 无界面自检模式：在真实 WPF 环境里驱动 ViewModel 走完「解析 → 下载」链路
        if (e.Args.Contains("--selftest"))
        {
            string? url = e.Args.SkipWhile(a => a != "--selftest").Skip(1).FirstOrDefault();
            _ = RunSelfTestAsync(url);
            return;
        }

        // 手动建窗（而非 StartupUri）以便捕获 XAML 解析异常 —— 这类异常发生在
        // Application 加载 StartupUri 期间，全局处理器来不及接住，表现为进程静默退出。
        Views.SplashWindow? splash = null;
        try
        {
            // 单文件自包含包冷启动要先解包（72MB），这段时间给个反馈，
            // 否则用户会以为没启动而再点一次。
            splash = new Views.SplashWindow();
            splash.Show();

            var window = new MainWindow();

            // 主窗口 Loaded 时闪屏淡出：主窗口自己在 Loaded 里做 0→1 淡入，
            // 两者同时发生，看起来就是「闪屏化开成主窗口」。
            window.Loaded += (_, _) => splash.FadeOutAndClose();

            window.Show();
        }
        catch (Exception ex)
        {
            splash?.Close();
            WriteCrash("Startup", ex);
            MessageBox.Show(
                "启动失败：\n\n" + ex.GetType().Name + ": " + ex.Message +
                "\n\n详细堆栈已写入：\n" + CrashLogPath,
                "YunX Desktop", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // 后台任务未观察异常（async void / 未 await）不应拖垮进程
        TaskScheduler.UnobservedTaskException += (_, args) => args.SetObserved();

        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrash("Dispatcher", args.Exception);
            MessageBox.Show(
                $"界面出现异常：\n\n{args.Exception.GetType().Name}: {args.Exception.Message}",
                "YunX Desktop", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
    }

    /// <summary>
    /// 退出兜底。
    ///
    /// <para>正常路径是 <c>MainWindow.OnClosing</c> → <c>Vm.Shutdown()</c> → <c>Dispose()</c>。
    /// 但有些路径不会经过窗口关闭：<c>Shutdown()</c> 被直接调用、启动期异常后退出、
    /// 或用户从任务管理器结束（最后一种拦不住，SQLite 的 WAL 本就能恢复）。
    /// 这里再调一次 <c>Dispose</c>，它是幂等的。</para>
    /// </summary>
    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            (MainWindow?.DataContext as ViewModels.MainViewModel)?.Dispose();
        }
        catch (Exception ex)
        {
            WriteCrash("OnExit", ex);
        }

        base.OnExit(e);
    }

    /// <summary>
    /// 启动时应用用户选的种子色。
    /// 读设置失败就用默认蓝 —— 界面配色不该阻断启动。
    /// </summary>
    private void ApplySeedColor()
    {
        try
        {
            var settings = new Services.SettingsStore().Load();
            var seed = Themes.SeedPalette.Parse(settings.SeedColor);
            Themes.SeedPalette.Apply(this, seed);
        }
        catch (Exception ex)
        {
            WriteCrash("SeedColor", ex);
        }
    }

    private static void WriteCrash(string source, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {source}\n{ex}\n\n");
        }
        catch
        {
            // 日志写不进去也不能再抛
        }
    }

    /// <summary>写诊断文本到 %APPDATA%（WinExe 无控制台，只能落盘）。</summary>
    private static void EmitX(string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath, text + Environment.NewLine);
        }
        catch { /* ignore */ }
    }

    /// <summary>自检模式：跑完即退出并打印结果。</summary>
    private async Task RunSelfTestAsync(string? url)
    {
        int code;
        try
        {
            code = await UiSelfTest.RunAsync(url).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            WriteCrash("SelfTest", ex);
            Console.WriteLine("自检异常：" + ex);
            code = 1;
        }
        Shutdown(code);
    }
}
