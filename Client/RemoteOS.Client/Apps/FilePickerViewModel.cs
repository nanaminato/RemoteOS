using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Client.Apps;

/// <summary>Small, reusable file-explorer view model used when an app opens the picker in select mode.</summary>
public partial class FilePickerViewModel : ObservableObject
{
    private readonly Action<string> _select;
    private readonly Action _cancel;

    public FilePickerViewModel(Action<string> select, Action cancel)
    {
        _select = select;
        _cancel = cancel;
        CurrentPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Refresh();
    }

    public ObservableCollection<FilePickerEntry> Entries { get; } = new();

    [ObservableProperty] private string _currentPath = string.Empty;
    [ObservableProperty] private FilePickerEntry? _selectedEntry;
    [ObservableProperty] private string _errorMessage = string.Empty;

    [RelayCommand]
    private void Up()
    {
        var parent = Directory.GetParent(CurrentPath);
        if (parent is null) return;
        CurrentPath = parent.FullName;
        Refresh();
    }

    [RelayCommand]
    private void OpenSelected()
    {
        if (SelectedEntry is null) return;
        if (SelectedEntry.IsDirectory)
        {
            CurrentPath = SelectedEntry.FullPath;
            Refresh();
            return;
        }
        _select(SelectedEntry.FullPath);
    }

    [RelayCommand]
    private void Select() => OpenSelected();

    [RelayCommand]
    private void Cancel() => _cancel();

    private void Refresh()
    {
        Entries.Clear();
        SelectedEntry = null;
        ErrorMessage = string.Empty;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(CurrentPath).OrderBy(Path.GetFileName))
                Entries.Add(new FilePickerEntry(Path.GetFileName(directory), directory, true));
            foreach (var file in Directory.EnumerateFiles(CurrentPath).OrderBy(Path.GetFileName))
                Entries.Add(new FilePickerEntry(Path.GetFileName(file), file, false));
        }
        catch (Exception ex)
        {
            ErrorMessage = $"无法读取此位置：{ex.Message}";
        }
    }
}
