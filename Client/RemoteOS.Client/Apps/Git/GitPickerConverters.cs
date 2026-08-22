using Avalonia.Data.Converters;
using Client.Localization;

namespace Client.Apps.Git;

/// <summary>选择器视图与远程视图共用的小型值转换器集合。</summary>
public static class GitPickerConverters
{
    /// <summary>集合 Count 为 0 时返回 true（用于空列表占位提示的可见性绑定）。</summary>
    public static readonly IValueConverter IsZeroToVisible = new CountIsZeroToVisibleConverter();

    /// <summary>把绑定值代入 LocalizationService 的格式化字符串。
    /// 用法：Text="{Binding Count, Converter={x:Static git:GitPickerConverters.LocalizedFormat}, ConverterParameter=git.workspace.file_count_format}"。
    /// 注意：仅在绑定值变化时重新计算；语言切换后需触发源属性变更才会刷新文案。</summary>
    public static readonly IValueConverter LocalizedFormat = new LocalizedFormatConverter();
}

/// <summary>当输入为整数 0 时返回 true，否则返回 false（适用于 Avalonia 的 IsVisible 绑定）。</summary>
public sealed class CountIsZeroToVisibleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is int i ? (i == 0) : (object?)false;

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>把绑定值代入 LocalizationService 的格式化字符串（ConverterParameter 为 key）。</summary>
public sealed class LocalizedFormatConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
    {
        var key = parameter as string ?? string.Empty;
        if (string.IsNullOrEmpty(key)) return value?.ToString() ?? string.Empty;
        return LocalizedText.Format(key, value);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
