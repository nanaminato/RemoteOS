// SizeSuffix 逻辑移植自 Jaya FileSystemObjectModel.SizeSuffix（BSD-3）。
// Copyright (c) 2020, Rubal Walia. 原始许可见 LICENSE-jaya.txt 与 THIRD_PARTY_NOTICES.md。
using System.Globalization;
using Avalonia.Data.Converters;
using RemoteOS.Protocol.Files;

namespace Client.Apps.Explorer.Converters;

/// <summary>条目类型 → 图标可见性转换器。ConverterParameter 指定期望的类别：
/// "drive" 仅 Drive 为 true；"dir" 仅 Directory 为 true；"file" 仅 File 为 true。</summary>
public sealed class EntryTypeToGlyphConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not FileSystemEntryType t || parameter is not string p) return false;
        return p switch
        {
            "drive" => t == FileSystemEntryType.Drive,
            "dir" => t == FileSystemEntryType.Directory,
            "file" => t == FileSystemEntryType.File,
            _ => false
        };
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>条目类型 → 中文类型名（用于"类型"列）。</summary>
public sealed class EntryTypeToStringConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is FileSystemEntryType t ? t switch
        {
            FileSystemEntryType.Drive => "驱动器",
            FileSystemEntryType.Directory => "文件夹",
            FileSystemEntryType.File => "文件",
            _ => string.Empty
        } : string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>大小 → 友好字符串（字节/KB/MB/...）。目录/驱动器无大小返回空。移植自 Jaya SizeSuffix。</summary>
public sealed class EntrySizeToStringConverter : IValueConverter
{
    private static readonly string[] Suffixes = { "bytes", "KB", "MB", "GB", "TB", "PB" };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long size || size <= 0) return string.Empty;
        var mag = (int)Math.Log(size, 1024);
        if (mag >= Suffixes.Length) mag = Suffixes.Length - 1;
        var adjusted = (decimal)size / (1L << (mag * 10));
        if (Math.Round(adjusted, 2) >= 1000 && mag < Suffixes.Length - 1)
        {
            mag++;
            adjusted /= 1024;
        }
        return $"{adjusted:n2} {Suffixes[mag]}";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
