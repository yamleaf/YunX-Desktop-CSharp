using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using YunX.Desktop.ViewModels;

namespace YunX.Desktop.Converters;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not Visibility.Visible;
}

/// <summary>
/// 百分比 → 进度条填充宽度。
/// 用 MultiBinding：[0]=百分比，[1]=容器实际宽度。避免用固定宽度导致窗口缩放时比例失真。
/// </summary>
public sealed class PercentToWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return 0d;
        if (values[0] is not double pct) return 0d;
        if (values[1] is not double width || double.IsNaN(width)) return 0d;
        return Math.Max(0, Math.Min(width, width * pct / 100.0));
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter,
        CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>任务状态 → 颜色：下载中用强调色、完成绿色、失败红色、取消灰色。</summary>
public sealed class StateToBrushConverter : IValueConverter
{
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x4C, 0x8B, 0xF5));
    private static readonly Brush Ok = new SolidColorBrush(Color.FromRgb(0x3F, 0xB9, 0x50));
    private static readonly Brush Err = new SolidColorBrush(Color.FromRgb(0xF8, 0x51, 0x49));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x8B, 0x95, 0xA7));

    static StateToBrushConverter()
    {
        Accent.Freeze();
        Ok.Freeze();
        Err.Freeze();
        Muted.Freeze();
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value switch
        {
            TaskState.Done => Ok,
            TaskState.Failed => Err,
            TaskState.Cancelled => Muted,
            TaskState.Running => Accent,
            _ => Muted,
        };

    public object ConvertBack(object value, Type targetType, object parameter,
        CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>字符串为空 → Visible（用于输入框占位提示）。</summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>非 null / 非空字符串 → Visible（用于错误提示）。</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s
            ? (s.Length > 0 ? Visibility.Visible : Visibility.Collapsed)
            : (value == null ? Visibility.Collapsed : Visibility.Visible);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 枚举值与 ConverterParameter 比较 → bool。
/// 用于把「当前分区」绑到导航项的选中态，避免为每个分区各写一个 IsXxxPage 样式。
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return false;
        return string.Equals(value.ToString(), parameter.ToString(),
            StringComparison.OrdinalIgnoreCase);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool 取反（用于「未运行时才显示某控件」）。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is not true;
}

/// <summary>字符串非空 → Visible 的反向：为空时显示（用于「有内容才显示某块」）。</summary>
public sealed class StringNotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => string.IsNullOrEmpty(value as string) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 任务状态 → 图标几何体。
/// 直接把资源字典里的 Geometry 取出来返回，避免为每个状态写一段 DataTrigger。
/// </summary>
public sealed class StateToGeoConverter : IValueConverter
{
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        string key = value switch
        {
            TaskState.Done => "IconCheck",
            TaskState.Failed => "IconError",
            TaskState.Cancelled => "IconStop",
            TaskState.Running => "IconDownload",
            _ => "IconClock",
        };

        // 从应用级资源里找；找不到时给个空几何体而不是 null，避免 Path 渲染异常
        return Application.Current?.TryFindResource(key) as Geometry
               ?? Geometry.Empty;
    }

    public object ConvertBack(object value, Type targetType, object parameter,
        CultureInfo culture) => throw new NotSupportedException();
}
