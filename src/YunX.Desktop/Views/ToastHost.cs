// SPDX-License-Identifier: AGPL-3.0-or-later
// Copyright (C) 2026 Lyaco

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace YunX.Desktop.Views;

/// <summary>
/// 右下角浮层通知（Toast）。
///
/// <para><b>为什么需要它</b>：解析失败可以用模态弹窗拦住用户，但下载失败不行 ——
/// 批量下载 28 个资产可能同时失败 5 个，连续弹 5 个模态框会把用户逼疯。
/// 浮层不打断操作、可堆叠、会自动消失，适合"告知但不阻断"的场景。</para>
///
/// <para><b>与托盘气泡的区别</b>：托盘气泡依赖系统通知区域，Windows 的
/// 「专注助手」会静默吞掉它，而且样式完全不受控。这里是窗口内自绘，可靠得多。</para>
/// </summary>
public sealed class ToastHost : Grid
{
    private const int MaxVisible = 4;
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromSeconds(6);

    private readonly StackPanel _stack;

    public ToastHost()
    {
        // 不参与命中测试：浮层浮在界面之上，但不能挡住底下按钮的点击。
        // 单个 Toast 自己会把 IsHitTestVisible 打开（要支持点"关闭"/点卡片跳转）。
        IsHitTestVisible = false;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(0, 0, 20, 20);

        _stack = new StackPanel
        {
            Orientation = Orientation.Vertical,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Children.Add(_stack);
    }

    /// <summary>
    /// 弹一条通知。
    /// </summary>
    /// <param name="title">标题（一行，讲清是什么失败了）</param>
    /// <param name="detail">细节（可多行，讲清为什么 / 怎么处理）</param>
    /// <param name="isError">true = 红色边框（失败），false = 常规（提示）</param>
    /// <param name="lifetime">停留时长；null 用默认 6 秒。传 <see cref="TimeSpan.MaxValue"/> 表示不自动消失。</param>
    public void Show(string title, string detail, bool isError = true,
        TimeSpan? lifetime = null)
    {
        // 超出上限先移除最旧的：堆太多会把界面盖住，而且用户根本看不过来
        while (_stack.Children.Count >= MaxVisible)
            _stack.Children.RemoveAt(0);

        var card = BuildCard(title, detail, isError);
        _stack.Children.Add(card);

        // 滑入 + 淡入：直接"啪"地出现太生硬
        card.Opacity = 0;
        var slide = new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(180))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180));
        card.RenderTransform = new TranslateTransform(16, 0);
        ((TranslateTransform)card.RenderTransform).BeginAnimation(TranslateTransform.XProperty, slide);
        card.BeginAnimation(OpacityProperty, fade);

        var life = lifetime ?? DefaultLifetime;
        if (life != TimeSpan.MaxValue)
        {
            var timer = new DispatcherTimer { Interval = life };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Dismiss(card);
            };
            timer.Start();
        }
    }

    /// <summary>淡出并移除。重复调用安全。</summary>
    private void Dismiss(Border card)
    {
        if (!_stack.Children.Contains(card)) return;

        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) => _stack.Children.Remove(card);
        card.BeginAnimation(OpacityProperty, fade);

        // 鼠标悬停时不自动消失（用户正要看内容），移开后再计时
        card.MouseEnter -= OnCardEnter;
        card.MouseLeave -= OnCardLeave;
    }

    private void OnCardEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is Border b) b.BeginAnimation(OpacityProperty, null);
    }

    private void OnCardLeave(object sender, System.Windows.Input.MouseEventArgs e) { }

    private Border BuildCard(string title, string detail, bool isError)
    {
        var accent = (Brush)(Application.Current.Resources[
            isError ? "ErrBrush" : "AccentBrush"] ?? Brushes.OrangeRed);

        var content = new StackPanel();

        content.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            Foreground = (Brush)Application.Current.Resources["TextBrush"],
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 340,
        });

        if (!string.IsNullOrWhiteSpace(detail))
        {
            content.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 11.5,
                Foreground = (Brush)Application.Current.Resources["TextMutedBrush"],
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 340,
                Margin = new Thickness(0, 4, 0, 0),
            });
        }

        var close = new Button
        {
            Content = "✕",
            FontSize = 10,
            Width = 18,
            Height = 18,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = (Brush)Application.Current.Resources["TextDimBrush"],
            Cursor = System.Windows.Input.Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            ToolTip = "关闭",
        };
        close.Click += (_, _) =>
        {
            if (close.Parent is Grid g && g.Parent is Border own) Dismiss(own);
        };

        var grid = new Grid();
        grid.Children.Add(content);
        grid.Children.Add(close);

        var card = new Border
        {
            Background = (Brush)Application.Current.Resources["SurfaceBrush"],
            BorderBrush = accent,
            // 左侧色条：一眼区分错误/提示，比只靠边框明显
            BorderThickness = new Thickness(3, 1, 1, 1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(13, 11, 26, 11),
            Margin = new Thickness(0, 6, 0, 0),
            MinWidth = 240,
            MaxWidth = 380,
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = grid,
            // 卡片本身要能点（关闭按钮），所以打开命中测试
            IsHitTestVisible = true,
        };

        card.MouseEnter += OnCardEnter;
        card.MouseLeave += OnCardLeave;

        var shadow = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 14,
            ShadowDepth = 0,
            Opacity = 0.45,
            Color = Colors.Black,
        };
        card.Effect = shadow;

        return card;
    }
}
