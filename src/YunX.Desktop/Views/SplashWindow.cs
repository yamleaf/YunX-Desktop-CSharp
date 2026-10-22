using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace YunX.Desktop.Views;

/// <summary>
/// 启动闪屏。
///
/// <para><b>它存在的理由</b>：单文件自包含发布（72MB）在冷启动时要先解包到临时目录，
/// 再加上 XAML 解析与主题加载，首次显示窗口可能要 1~3 秒。
/// 这段时间里如果没有反馈，用户会以为程序没启动而双击第二次。</para>
///
/// <para>视觉上用「无边框圆角 + 半透明」的浮层，而不是传统全屏 logo 页 ——
/// 后者要维持 2 秒以上才不显得闪，而这里只需要遮住解包那一瞬间。</para>
/// </summary>
public sealed class SplashWindow : Window
{
    private readonly Border _root;
    private readonly TextBlock _status;
    private readonly Border _barFill;
    private readonly Grid _barHost;

    public SplashWindow()
    {
        Width = 400;
        Height = 190;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;

        Brush surface = GetBrush("SurfaceBrush", Color.FromRgb(0x14, 0x17, 0x1E));
        Brush border = GetBrush("BorderHiBrush", Color.FromRgb(0x25, 0x2A, 0x35));
        Brush accent = GetBrush("AccentBrush", Color.FromRgb(0x4C, 0x8B, 0xF5));
        Brush text = GetBrush("TextBrush", Color.FromRgb(0xE6, 0xE9, 0xEF));
        Brush muted = GetBrush("TextMutedBrush", Color.FromRgb(0x8B, 0x95, 0xA7));

        var stack = new StackPanel { Margin = new Thickness(0, 30, 0, 0) };

        // logo：用一个圆形底 + 矢量图形，避免依赖外部图片资源
        var logoRing = new Grid
        {
            Width = 46,
            Height = 46,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        logoRing.Children.Add(new Ellipse
        {
            Fill = new SolidColorBrush(Color.FromArgb(0x2E, 0x4C, 0x8B, 0xF5)),
        });
        if (Application.Current.TryFindResource("IconBolt") is Geometry bolt)
        {
            logoRing.Children.Add(new Path
            {
                Data = bolt,
                Fill = accent,
                Width = 20,
                Height = 20,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
        stack.Children.Add(logoRing);

        stack.Children.Add(new TextBlock
        {
            Text = "YunX Desktop",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = text,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 14, 0, 0),
            FontFamily = new FontFamily("Microsoft YaHei UI"),
        });

        string version = typeof(SplashWindow).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";
        stack.Children.Add(new TextBlock
        {
            Text = $"v{version} · 高速下载器",
            FontSize = 11.5,
            Foreground = muted,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 5, 0, 0),
        });

        // 不确定进度条：一条循环滑动的细条，比转圈更不打断视线
        _barHost = new Grid
        {
            Height = 3,
            Width = 180,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 22, 0, 0),
            ClipToBounds = true,
        };
        _barHost.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x30, 0x8B, 0x95, 0xA7)),
            CornerRadius = new CornerRadius(2),
        });
        _barFill = new Border
        {
            Width = 60,
            HorizontalAlignment = HorizontalAlignment.Left,
            Background = accent,
            CornerRadius = new CornerRadius(2),
        };
        _barHost.Children.Add(_barFill);
        stack.Children.Add(_barHost);

        _status = new TextBlock
        {
            Text = "正在加载…",
            FontSize = 11,
            Foreground = muted,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 12, 0, 0),
        };
        stack.Children.Add(_status);

        _root = new Border
        {
            Background = surface,
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = stack,
        };
        _root.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.Black,
            BlurRadius = 28,
            ShadowDepth = 6,
            Opacity = 0.6,
        };

        Content = _root;

        Loaded += (_, _) => StartBarAnimation();
    }

    private static Brush GetBrush(string key, Color fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    /// <summary>进度条来回滑动：0 → 右端 → 左端循环。</summary>
    private void StartBarAnimation()
    {
        double travel = Math.Max(60, _barHost.Width - _barFill.Width);

        var anim = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(1100),
            RepeatBehavior = RepeatBehavior.Forever,
        };
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(travel, KeyTime.FromPercent(0.5),
            new CubicEase { EasingMode = EasingMode.EaseInOut }));
        anim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1),
            new CubicEase { EasingMode = EasingMode.EaseInOut }));

        _barFill.BeginAnimation(Canvas.LeftProperty, null);
        var transform = new TranslateTransform();
        _barFill.RenderTransform = transform;
        transform.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    /// <summary>更新状态文案（初始化各阶段调用）。</summary>
    public void SetStatus(string text)
    {
        _status.Text = text;
    }

    /// <summary>淡出并关闭。</summary>
    public void FadeOutAndClose()
    {
        var anim = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
        };
        anim.Completed += (_, _) =>
        {
            try
            {
                Close();
            }
            catch
            {
                // 已经关了
            }
        };
        BeginAnimation(OpacityProperty, anim);
    }
}
