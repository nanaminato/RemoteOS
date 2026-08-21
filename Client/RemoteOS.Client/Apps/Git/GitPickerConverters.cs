using Avalonia.Data.Converters;

namespace Client.Apps.Git;

/// <summary>选择器视图与远程视图共用的小型值转换器集合。</summary>
public static class GitPickerConverters
{
    /// <summary>集合 Count 为 0 时返回 true（用于空列表占位提示的可见性绑定）。</summary>
    public static readonly IValueConverter IsZeroToVisible = new CountIsZeroToVisibleConverter();
}

/// <summary>当输入为整数 0 时返回 true，否则返回 false（适用于 Avalonia 的 IsVisible 绑定）。</summary>
public sealed class CountIsZeroToVisibleConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => value is int i ? (i == 0) : (object?)false;

    public object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
