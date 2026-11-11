using Microsoft.UI.Xaml.Data;

namespace AI_Tool_Box.Converters;

/// <summary>Boolean → String 转换器：True → TrueValue（默认 "True"），False → FalseValue（默认 "False"）。
/// 可选参数 "A|B"：当 ConverterParameter="A|B" 时 True→A, False→B（覆盖 TrueValue/FalseValue 优先级低）。</summary>
public sealed class BoolToStringConverter : IValueConverter
{
    /// <summary>True 时返回的字符串。</summary>
    public string TrueValue { get; set; } = "True";

    /// <summary>False 时返回的字符串。</summary>
    public string FalseValue { get; set; } = "False";

    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var b = value is bool bl && bl;
        if (parameter is string s && !string.IsNullOrEmpty(s))
        {
            var parts = s.Split('|');
            if (parts.Length == 2)
            {
                return b ? parts[0] : parts[1];
            }
        }
        return b ? TrueValue : FalseValue;
    }

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}

/// <summary>Boolean 取反转换器：IsListening=True → 启动/停止按钮是否为"停止"（非取反：只反转 bool）。</summary>
public sealed class InverseBoolConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, string language)
        => value is bool b ? !b : false;

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => value is bool b ? !b : false;
}

/// <summary>long 文件大小 → 友好字符串（B/KB/MB/GB）。</summary>
public sealed class FileSizeStringConverter : IValueConverter
{
    /// <inheritdoc/>
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var num = value switch
        {
            long l => l,
            int i => i,
            short s => s,
            ulong ul => (long)ul,
            _ => 0L
        };
        if (num < 0) num = 0;
        if (num < 1024) return $"{num} B";
        if (num < 1024 * 1024) return $"{num / 1024.0:F2} KB";
        if (num < 1024L * 1024 * 1024) return $"{num / (1024.0 * 1024):F2} MB";
        return $"{num / (1024.0 * 1024 * 1024):F2} GB";
    }

    /// <inheritdoc/>
    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
