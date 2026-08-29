using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.Registry;

namespace Client.Apps.Registry;

public sealed partial class RegistryValueDialogViewModel : ObservableObject
{
    private readonly IRegistryClient _client;
    private readonly Action<bool> _close;
    [ObservableProperty] private string _valueText;
    [ObservableProperty] private string? _error;
    public RegistryEntryDto Entry { get; }
    public bool IsJson => Entry.ValueType == RegistryValueType.Json;

    public RegistryValueDialogViewModel(RegistryEntryRow row, IRegistryClient client, Action<bool> close)
    {
        Entry = row.Source; _client = client; _close = close; ValueText = Entry.DesiredValue.GetRawText();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            using var value = JsonDocument.Parse(ValueText);
            await _client.SaveAsync(new PutRegistryEntryRequest(Entry.Scope, Entry.Path, Entry.Name, Entry.ValueType, value.RootElement.Clone()));
            _close(true);
        }
        catch (Exception ex) { Error = ex.Message; }
    }
    [RelayCommand] private void Cancel() => _close(false);
}
