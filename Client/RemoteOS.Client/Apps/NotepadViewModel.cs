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
}
