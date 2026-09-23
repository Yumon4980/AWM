using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AltTabReplacer;

/// <summary>
/// 把"网格可用宽度"换算成方形单元格边长（宽 / 列数，再扣掉单元格外边距）。
/// 列数默认从 <see cref="Core.KeyMap.Cols"/> 取；XAML 里可用 <c>ConverterParameter</c>
/// 显式覆盖（少数不需要跟随网格列数的场景）。
/// </summary>
public sealed class CellSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double width = value is double d ? d : 0;
        double cols = Core.KeyMap.Cols;
        if (parameter is string s
            && double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var p)
            && p > 0)
            cols = p;

        // 单元格 Margin=2（每边），故扣掉 4；下限避免窗口极窄时算出 0/负值。
        return Math.Max(48, width / cols - 4);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → Visibility；ConverterParameter="invert" 时取反。</summary>
public sealed class BoolToVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool b = value is bool v && v;
        if (parameter is string s && string.Equals(s, "invert", StringComparison.OrdinalIgnoreCase))
            b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
