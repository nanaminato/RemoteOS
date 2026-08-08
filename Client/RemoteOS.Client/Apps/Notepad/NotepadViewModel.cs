using System.Text;
using Client.Apps.Explorer;
using Client.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Client.Apps.Notepad;

/// <summary>RemoteOS 的基础文本编辑器。文件内容始终通过远程文件 API 打开和保存。</summary>
public sealed partial class NotepadViewModel : ObservableObject
{
    private readonly IExplorerClient? _files;
    private bool _isLoading;

    public NotepadViewModel(IExplorerClient? files) => _files = files;

    [ObservableProperty] private string _text = string.Empty;
    [ObservableProperty] private string? _currentPath;
    [ObservableProperty] private string _encodingName = "UTF-8";
    [ObservableProperty] private double _fontSize = 14;
    [ObservableProperty] private bool _isDirty;
    [ObservableProperty] private string _statusText = LocalizedText.Get("notepad.status.ready");

    public int CharCount => Text.Length;
    public int LineCount => string.IsNullOrEmpty(Text) ? 1 : Enumerable.Count<char>(Text, c => c == '\n') + 1;
    public string DocumentName => string.IsNullOrWhiteSpace(CurrentPath) ? LocalizedText.Get("notepad.document.untitled") : Path.GetFileName(CurrentPath) ?? LocalizedText.Get("notepad.document.untitled");
    public IReadOnlyList<string> AvailableEncodings { get; } = ["UTF-8", "UTF-8 BOM", "UTF-16 LE", "UTF-16 BE"];
    public IReadOnlyList<double> FontSizes { get; } = [12, 13, 14, 16, 18, 20];

    partial void OnTextChanged(string value)
    {
        OnPropertyChanged(nameof(CharCount));
        OnPropertyChanged(nameof(LineCount));
        if (!_isLoading) IsDirty = true;
    }

    partial void OnCurrentPathChanged(string? value) => OnPropertyChanged(nameof(DocumentName));

    [RelayCommand]
    private void NewDocument()
    {
        _isLoading = true;
        Text = string.Empty;
        CurrentPath = null;
        EncodingName = "UTF-8";
        IsDirty = false;
        StatusText = LocalizedText.Get("notepad.status.new_document");
        _isLoading = false;
    }

    public Func<Task<string?>>? RequestFileAsync { get; set; }
    public Func<string, Task<string?>>? RequestSavePathAsync { get; set; }
    public Func<Task>? RequestSettingsAsync { get; set; }
    public Action? CloseSettingsAction { get; set; }

    [RelayCommand]
    private async Task OpenDocumentAsync()
    {
        var path = await (RequestFileAsync?.Invoke() ?? Task.FromResult<string?>(null));
        if (!string.IsNullOrWhiteSpace(path)) await OpenPathAsync(path);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        var path = CurrentPath;
        if (string.IsNullOrWhiteSpace(path))
            path = await (RequestSavePathAsync?.Invoke("untitled.txt") ?? Task.FromResult<string?>(null));
        if (!string.IsNullOrWhiteSpace(path)) await SaveToPathAsync(path);
    }

    [RelayCommand]
    private async Task SaveAsAsync()
    {
        var suggestedName = string.IsNullOrWhiteSpace(CurrentPath) ? "untitled.txt" : Path.GetFileName(CurrentPath) ?? "untitled.txt";
        var path = await (RequestSavePathAsync?.Invoke(suggestedName) ?? Task.FromResult<string?>(null));
        if (!string.IsNullOrWhiteSpace(path)) await SaveToPathAsync(path);
    }

    [RelayCommand]
    private async Task ReopenWithEncodingAsync()
    {
        if (string.IsNullOrWhiteSpace(CurrentPath)) return;
        if (IsDirty)
        {
            StatusText = LocalizedText.Get("notepad.status.save_or_discard");
            return;
        }
        await OpenPathAsync(CurrentPath);
    }

    [RelayCommand]
    private async Task OpenSettingsAsync()
        => await (RequestSettingsAsync?.Invoke() ?? Task.CompletedTask);

    [RelayCommand]
    private void CloseSettings() => CloseSettingsAction?.Invoke();

    public async Task OpenPathAsync(string path)
    {
        if (_files is null) { StatusText = LocalizedText.Get("notepad.status.connect_before_open"); return; }
        try
        {
            var bytes = await _files.ReadFileAsync(path);
            if (bytes is null) { StatusText = LocalizedText.Get("notepad.status.file_missing"); return; }
            _isLoading = true;
            Text = Decode(bytes, EncodingName);
            CurrentPath = path;
            IsDirty = false;
            StatusText = LocalizedText.Format("notepad.status.opened", Path.GetFileName(path), EncodingName);
        }
        catch (Exception ex) { StatusText = LocalizedText.Format("notepad.status.open_failed", ex.Message); }
        finally { _isLoading = false; }
    }

    private async Task SaveToPathAsync(string path)
    {
        if (_files is null) { StatusText = LocalizedText.Get("notepad.status.connect_before_save"); return; }
        try
        {
            await _files.WriteFileAsync(path, GetEncoding(EncodingName).GetBytes((string)Text));
            CurrentPath = path;
            IsDirty = false;
            StatusText = LocalizedText.Format("notepad.status.saved", Path.GetFileName(path), EncodingName);
        }
        catch (Exception ex) { StatusText = LocalizedText.Format("notepad.status.save_failed", ex.Message); }
    }

    private static string Decode(byte[] bytes, string encodingName)
    {
        var text = GetEncoding(encodingName).GetString(bytes);
        return text.Length > 0 && text[0] == '\uFEFF' ? text[1..] : text;
    }

    private static Encoding GetEncoding(string encodingName) => encodingName switch
    {
        "UTF-8 BOM" => new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
        "UTF-16 LE" => Encoding.Unicode,
        "UTF-16 BE" => Encoding.BigEndianUnicode,
        _ => new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: false),
    };
}
