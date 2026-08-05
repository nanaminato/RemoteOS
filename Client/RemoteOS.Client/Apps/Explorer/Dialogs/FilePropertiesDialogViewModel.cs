using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.Files;

namespace Client.Apps.Explorer.Dialogs;

/// <summary>文件属性弹窗的展示模型。</summary>
public sealed partial class FilePropertiesDialogViewModel
{
    private readonly Action _close;

    public FilePropertiesDialogViewModel(FilePropertiesDto properties, Action close)
    {
        Properties = properties;
        _close = close;
    }

    public FilePropertiesDto Properties { get; }
    public string SizeText => Properties.Size is { } size ? $"{size:N0} bytes" : "—";

    [RelayCommand]
    private void Close() => _close();
}
