using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace YunX.Desktop.Views;

/// <summary>
/// 内嵌浏览器登录窗口（WebView2）。
///
/// <para><b>为什么用 WebView2 而不是原项目的 JCEF</b>：Win10/11 自带 Edge 内核，
/// 不需要随包分发 100MB+ 的 Chromium；原项目为此引入 <c>jcefmaven</c> + natives，
/// 光浏览器运行时就让安装包膨胀数倍。</para>
///
/// <para><b>Cookie 提取</b>：登录成功后调 <c>CoreWebView2.CookieManager.GetCookiesAsync</c>
/// 拿目标域下的全部 Cookie，拼接成标准 Cookie 头。</para>
///
/// <para><b>自动检测</b>：轮询 Cookie 变化 —— 当目标域出现<b>关键 Cookie</b>
/// （各平台不同，见 <see cref="KeyCookies"/>）就认为登录完成，自动关闭窗口。</para>
/// </summary>
public sealed partial class BrowserLoginWindow : Window
{
    private readonly WebView2 _webView;
    private readonly string[] _domains;
    private readonly string _platformName;
    private System.Windows.Threading.DispatcherTimer? _pollTimer;
    private bool _initialized;

    /// <summary>登录成功后提取到的 Cookie 头值。</summary>
    public string CapturedCookie { get; private set; } = "";

    /// <summary>
    /// 各平台的「登录成功标志」Cookie。
    /// 登录完成后这些 Cookie 才会出现，用它判断比看页面跳转可靠得多。
    /// </summary>
    private static readonly Dictionary<string, string[]> KeyCookies = new(StringComparer.OrdinalIgnoreCase)
    {
        ["夸克网盘"] = new[] { "__uid", "__puus", "__pus" },
        ["UC 网盘"] = new[] { "__uid", "__puus" },
        ["百度网盘"] = new[] { "BDUSS", "STOKEN" },
        ["139 网盘"] = new[] { "YMALIAS", "USS", "Os_SSo_Sid" },
        ["迅雷网盘"] = new[] { "access_token", "sessionid" },
        ["123 云盘"] = new[] { "authorToken" },
    };

    public BrowserLoginWindow(string platformName, string startUrl, string[] cookieDomains)
    {
        _platformName = platformName;
        _domains = cookieDomains.Length > 0 ? cookieDomains : new[] { new Uri(startUrl).Host };

        Title = $"登录 {platformName}";
        Width = 980;
        Height = 720;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.Resources["BgBrush"];

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 顶部提示条
        var bar = new Border
        {
            Background = (Brush)Application.Current.Resources["SurfaceBrush"],
            Padding = new Thickness(16, 10, 16, 10),
            BorderBrush = (Brush)Application.Current.Resources["BorderBrush"],
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
        var barGrid = new Grid();
        var hint = new TextBlock
        {
            Text = $"在内嵌浏览器中完成 {platformName} 登录，登录成功后会自动保存凭证",
            Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
        };
        var manualBtn = new Button
        {
            Content = "手动完成", HorizontalAlignment = HorizontalAlignment.Right,
            Padding = new Thickness(14, 5, 14, 5), FontSize = 12,
        };
        if (Application.Current.Resources["GhostButton"] is Style gb) manualBtn.Style = gb;
        manualBtn.Click += async (_, _) => await CaptureAndCloseAsync().ConfigureAwait(true);

        barGrid.Children.Add(hint);
        barGrid.Children.Add(manualBtn);
        bar.Child = barGrid;

        _webView = new WebView2();
        Grid.SetRow(_webView, 1);
        Grid.SetRow(bar, 0);

        // 底部状态
        var footer = new Border
        {
            Background = (Brush)Application.Current.Resources["SurfaceBrush"],
            Padding = new Thickness(16, 8, 16, 8),
            BorderBrush = (Brush)Application.Current.Resources["BorderBrush"],
            BorderThickness = new Thickness(0, 1, 0, 0),
        };
        footer.Child = new TextBlock
        {
            Text = "提示：若自动检测不生效，登录完成后点右上角「手动完成」",
            Foreground = (Brush)Application.Current.Resources["TextDimBrush"],
            FontSize = 11,
        };
        Grid.SetRow(footer, 2);

        grid.Children.Add(bar);
        grid.Children.Add(_webView);
        grid.Children.Add(footer);
        Content = grid;

        Loaded += async (_, _) => await InitializeAsync(startUrl).ConfigureAwait(true);
        Closed += (_, _) => _pollTimer?.Stop();
    }

    private async Task InitializeAsync(string startUrl)
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            // 用户数据目录放在应用数据目录下，保留登录态（避免每次都要重新登录）
            string userDataDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "YunX-Desktop-CS", "webview");
            Directory.CreateDirectory(userDataDir);

            var env = await CoreWebView2Environment.CreateAsync(null, userDataDir)
                .ConfigureAwait(true);
            await _webView.EnsureCoreWebView2Async(env).ConfigureAwait(true);

            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = false;

            _webView.Source = new Uri(startUrl);

            // 每 1.5 秒检查一次登录态
            _pollTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1.5),
            };
            _pollTimer.Tick += async (_, _) => await CheckLoginAsync().ConfigureAwait(true);
            _pollTimer.Start();
        }
        catch (Exception e)
        {
            MessageBox.Show(
                "内嵌浏览器初始化失败：\n\n" + e.Message +
                "\n\n请确认已安装 Microsoft Edge WebView2 运行时（Win10/11 通常自带）。\n" +
                "也可以改用「手动粘贴 Cookie」方式登录。",
                "登录", MessageBoxButton.OK, MessageBoxImage.Warning);
            DialogResult = false;
            Close();
        }
    }

    /// <summary>检查目标域是否已出现关键 Cookie。</summary>
    private async Task CheckLoginAsync()
    {
        if (_webView.CoreWebView2 == null) return;

        try
        {
            var cookies = await _webView.CoreWebView2.CookieManager
                .GetCookiesAsync("https://" + _domains[0]).ConfigureAwait(true);

            if (cookies.Count == 0) return;

            var names = cookies.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var required = KeyCookies.TryGetValue(_platformName, out var keys)
                ? keys : Array.Empty<string>();

            // 没有已知关键 Cookie 的平台：只要有任意 Cookie 就认为已登录（用户点手动完成更准确）
            bool ready = required.Length > 0
                ? required.Any(n => names.Contains(n))
                : cookies.Count >= 3;

            if (ready) await CaptureAndCloseAsync().ConfigureAwait(true);
        }
        catch
        {
            // 轮询失败静默跳过
        }
    }

    /// <summary>提取 Cookie 并关闭。</summary>
    private async Task CaptureAndCloseAsync()
    {
        _pollTimer?.Stop();

        try
        {
            if (_webView.CoreWebView2 == null)
            {
                DialogResult = false;
                Close();
                return;
            }

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var domain in _domains)
            {
                List<CoreWebView2Cookie> cookies;
                try
                {
                    cookies = await _webView.CoreWebView2.CookieManager
                        .GetCookiesAsync("https://" + domain).ConfigureAwait(true);
                }
                catch
                {
                    continue;
                }

                foreach (var c in cookies)
                    map[c.Name] = c.Value;   // 同名取后者（通常是更具体的域）
            }

            if (map.Count == 0)
            {
                MessageBox.Show("没有提取到任何 Cookie，请确认已登录成功。",
                    "登录", MessageBoxButton.OK, MessageBoxImage.Warning);
                DialogResult = false;
                Close();
                return;
            }

            CapturedCookie = string.Join("; ", map.Select(kv => $"{kv.Key}={kv.Value}"));
            DialogResult = true;
            Close();
        }
        catch (Exception e)
        {
            MessageBox.Show("提取 Cookie 失败：" + e.Message, "登录",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            DialogResult = false;
            Close();
        }
    }
}
