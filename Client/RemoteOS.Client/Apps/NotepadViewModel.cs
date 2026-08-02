using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Client.Apps;

public partial class NotepadViewModel : ObservableObject
{
    [ObservableProperty] private string _text = string.Empty;

    public int CharCount => Text.Length;

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(CharCount));

    [RelayCommand]
    private void NewDocument() => Text = string.Empty;

    /// <summary>Provided by the application when its owning desktop window is available.</summary>
    public Func<Task<string?>>? RequestTextAsync { get; set; }
    public Func<Task<string?>>? RequestFileAsync { get; set; }

    [RelayCommand]
    private async Task InsertTextAsync()
    {
        if (RequestTextAsync is null)
            return;

        var result = await RequestTextAsync();
        if (!string.IsNullOrEmpty(result))
            Text += result;
    }

    [RelayCommand]
    private async Task OpenDocumentAsync()
    {
        if (RequestFileAsync is null)
            return;

        var path = await RequestFileAsync();
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            Text = await File.ReadAllTextAsync(path);
        }
        catch (Exception ex)
        {
            Text = $"无法打开文件：{ex.Message}";
        }
    }
}
