using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Client.Apps;

public partial class NotepadInsertDialogViewModel : ObservableObject
{
    private readonly Action<string> _confirm;
    private readonly Action _cancel;

    public NotepadInsertDialogViewModel(Action<string> confirm, Action cancel)
    {
        _confirm = confirm;
        _cancel = cancel;
    }

    [ObservableProperty] private string _input = string.Empty;

    /// <summary>Optional nested dialog entry point, supplied by the parent dialog.</summary>
    public Func<Task<string?>>? RequestNestedTextAsync { get; set; }

    [RelayCommand]
    private void Confirm() => _confirm(Input);

    [RelayCommand]
    private void Cancel() => _cancel();

    [RelayCommand]
    private async Task AddFromNestedDialogAsync()
    {
        if (RequestNestedTextAsync is null)
            return;

        var result = await RequestNestedTextAsync();
        if (!string.IsNullOrEmpty(result))
            Input += result;
    }
}
