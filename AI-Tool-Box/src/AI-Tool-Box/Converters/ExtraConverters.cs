using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace AI_Tool_Box.Converters;

/// <summary>将枚举值与 int 互转（用于 RadioButtons.SelectedIndex 绑定枚举）。</summary>
public class EnumToIntConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is Enum e) return System.Convert.ToInt32(e);
        return 0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        if (value is int i && targetType.IsEnum) return Enum.ToObject(targetType, i);
        return value;
    }
}

/// <summary>double 进度 (0..1) 转百分比文本（"0%" ～ "100%"，保留 1 位小数）。</summary>
public class ProgressToPercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        if (value is double d)
        {
            if (d <= 0) return "0%";
            if (d >= 1) return "100%";
            return $"{d * 100:0.#}%";
        }
        return "0%";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

/// <summary>bool → Visibility（Invert=true 时 true→Collapsed，用于"未完成才显示取消按钮"）。</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>是否反转布尔值。</summary>
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var b = value is bool v && v;
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
