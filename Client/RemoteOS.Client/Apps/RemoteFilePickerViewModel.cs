using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Client.Apps.Explorer;
using RemoteOS.Protocol.Files;

namespace Client.Apps;

/// <summary>用于选择宿主机远程文件的轻量文件浏览器，而不是读取客户端本地文件系统。</summary>
public sealed partial class RemoteFilePickerViewModel : ObservableObject
{
    private readonly IExplorerClient _client;
    private readonly Action<string> _select;
    private readonly Action _cancel;

    public RemoteFilePickerViewModel(IExplorerClient client, Action<string> select, Action cancel)
    {
        _client = client;
        _select = select;
        _cancel = cancel;
        _ = NavigateAsync(null);
    }

    public ObservableCollection<FileSystemEntryDto> Entries { get; } = new();

    [ObservableProperty] private string _location = "Computer";
    [ObservableProperty] private string? _currentPath;
    [ObservableProperty] private FileSystemEntryDto? _selectedEntry;
    [ObservableProperty] private string _errorMessage = string.Empty;
    [ObservableProperty] private bool _isLoading;

    [RelayCommand]
    private async Task OpenSelectedAsync()
    {
        if (SelectedEntry is null) return;
        if (SelectedEntry.Type is FileSystemEntryType.Directory or FileSystemEntryType.Drive)
        {
            await NavigateAsync(SelectedEntry.Path);
            return;
        }
        _select(SelectedEntry.Path);
    }

    [RelayCommand]
    private Task UpAsync() => NavigateAsync(GetParentPath(CurrentPath));

    [RelayCommand]
    private void Cancel() => _cancel();

    private async Task NavigateAsync(string? path)
    {
        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var directory = await _client.GetDirectoryAsync(path);
            Entries.Clear();
            foreach (var item in directory.Directories) Entries.Add(item);
            foreach (var file in directory.Files)
                Entries.Add(new FileSystemEntryDto(file.Path, file.Name, file.Size, FileSystemEntryType.File,
                    file.Created, file.Modified, file.Accessed, file.IsHidden, file.IsSystem));
            CurrentPath = path;
            Location = string.IsNullOrEmpty(path) ? "Computer" : directory.Path;
            SelectedEntry = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Cannot read this location: {ex.Message}";
        }
        finally { IsLoading = false; }
    }

    private static string? GetParentPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var trimmed = path.TrimEnd('\\', '/');
        var separator = Math.Max(trimmed.LastIndexOf('\\'), trimmed.LastIndexOf('/'));
        if (separator < 0) return null;
        if (separator == 2 && trimmed[1] == ':') return trimmed[..3];
        if (separator == 0) return "/";
        return trimmed[..separator];
    }
}
