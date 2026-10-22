using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace YunX.Desktop.Views;

/// <summary>
/// 轻量输入对话框（替代系统 MessageBox 的输入场景）。
///
/// <para>自己实现而不是用 <c>Microsoft.VisualBasic.Interaction.InputBox</c>：
/// 后者外观是 Win9x 风格，与深色界面完全不搭，而且无法做密码掩码。</para>
/// </summary>
public sealed class PromptDialog : Window
{
    private readonly TextBox _input;
    private readonly PasswordBox? _secret;
    private bool _ok;

    private PromptDialog(string title, string message, string? secondLabel, bool secret)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        Background = (Brush)Application.Current.Resources["SurfaceBrush"];
        WindowStyle = WindowStyle.ToolWindow;

        var root = new StackPanel { Margin = new Thickness(20) };

        root.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextBrush"],
            Margin = new Thickness(0, 0, 0, 10),
        });

        if (secret)
        {
            _secret = new PasswordBox { Height = 34 };
            if (Application.Current.Resources["InputPassword"] is Style ps)
                _secret.Style = ps;
            root.Children.Add(_secret);
            _input = new TextBox();   // 占位，未使用
        }
        else
        {
            _input = new TextBox { Height = 34 };
            if (Application.Current.Resources["InputBox"] is Style ts)
                _input.Style = ts;
            root.Children.Add(_input);
            _secret = null;
        }

        if (secondLabel != null)
        {
            // 双输入模式（账号 + 密码）：把第一个框换成普通输入，第二个用密码框
            root.Children.Clear();
            root.Children.Add(new TextBlock
            {
                Text = message, TextWrapping = TextWrapping.Wrap,
                Foreground = (Brush)Application.Current.Resources["TextBrush"],
                Margin = new Thickness(0, 0, 0, 10),
            });

            root.Children.Add(new TextBlock
            {
                Text = "账号", Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
                Margin = new Thickness(0, 0, 0, 4),
            });
            _input = new TextBox { Height = 34 };
            if (Application.Current.Resources["InputBox"] is Style ts2) _input.Style = ts2;
            root.Children.Add(_input);

            root.Children.Add(new TextBlock
            {
                Text = secondLabel,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
                Margin = new Thickness(0, 10, 0, 4),
            });
            _secret = new PasswordBox { Height = 34 };
            if (Application.Current.Resources["InputPassword"] is Style ps2) _secret.Style = ps2;
            root.Children.Add(_secret);
        }

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        var cancel = new Button { Content = "取消", MinWidth = 80 };
        if (Application.Current.Resources["GhostButton"] is Style gb) cancel.Style = gb;
        cancel.Click += (_, _) => { _ok = false; Close(); };

        var ok = new Button { Content = "确定", MinWidth = 80, Margin = new Thickness(8, 0, 0, 0) };
        if (Application.Current.Resources["PrimaryButton"] is Style pb) ok.Style = pb;
        ok.Click += (_, _) => { _ok = true; Close(); };
        ok.IsDefault = true;

        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);
        root.Children.Add(buttons);

        Content = root;

        Loaded += (_, _) =>
        {
            if (_secret != null) _secret.Focus();
            else _input.Focus();
        };

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { _ok = false; Close(); }
        };
    }

    /// <summary>单输入。</summary>
    public static string? Ask(string title, string message, bool secret = false)
    {
        var dlg = new PromptDialog(title, message, null, secret);
        dlg.Owner = Application.Current.MainWindow;
        dlg.ShowDialog();
        if (!dlg._ok) return null;
        return secret ? dlg._secret?.Password : dlg._input.Text;
    }

    /// <summary>双输入（账号 + 密码）。</summary>
    public static (string? Account, string? Password) AskTwo(string title, string firstLabel,
        string secondLabel, bool secret = true)
    {
        var dlg = new PromptDialog(title, firstLabel, secondLabel, secret: false);
        dlg.Owner = Application.Current.MainWindow;
        dlg.ShowDialog();
        if (!dlg._ok) return (null, null);
        return (dlg._input.Text, dlg._secret?.Password);
    }

    /// <summary>
    /// 多选一（登录方式选择）。
    /// 返回按钮下标；取消返回 -1。
    ///
    /// <para>为什么不用 MessageBox：它只支持「是/否/取消」三种固定文案，
    /// 而这里要「内嵌登录 / 从浏览器导入 / 手动粘贴」三个业务动作。</para>
    /// </summary>
    public static int Choose(string title, string message, params string[] options)
    {
        var win = new Window
        {
            Title = title,
            Width = 520,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = (Brush)Application.Current.Resources["SurfaceBrush"],
            WindowStyle = WindowStyle.ToolWindow,
            Owner = Application.Current.MainWindow,
        };

        int result = -1;
        var root = new StackPanel { Margin = new Thickness(20) };

        root.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextBrush"],
            Margin = new Thickness(0, 0, 0, 16),
        });

        for (int i = 0; i < options.Length; i++)
        {
            int idx = i;
            var btn = new Button
            {
                Content = options[i],
                Height = 38,
                Margin = new Thickness(0, 0, 0, 8),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(14, 0, 14, 0),
            };
            if (Application.Current.Resources["GhostButton"] is Style gb) btn.Style = gb;
            btn.Click += (_, _) => { result = idx; win.Close(); };
            root.Children.Add(btn);
        }

        var cancel = new Button
        {
            Content = "取消",
            MinWidth = 80,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
        };
        if (Application.Current.Resources["GhostButton"] is Style cb) cancel.Style = cb;
        cancel.Click += (_, _) => { result = -1; win.Close(); };
        root.Children.Add(cancel);

        win.Content = root;
        win.ShowDialog();
        return result;
    }
}
