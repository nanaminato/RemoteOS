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

    [RelayCommand]
    private async Task InsertTextAsync()
    {
        if (RequestTextAsync is null)
            return;

        var result = await RequestTextAsync();
        if (!string.IsNullOrEmpty(result))
            Text += result;
    }
}
