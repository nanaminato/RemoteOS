using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.Registry;

namespace Client.Apps.Registry;

public sealed partial class RegistryViewModel(IRegistryClient client) : ObservableObject
{
    private List<RegistryEntryDto> _allEntries = [];
    public ObservableCollection<RegistryKeyNode> Keys { get; } = [];
    public ObservableCollection<RegistryEntryRow> Entries { get; } = [];
    [ObservableProperty] private string _statusText = "Loading registry…";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private RegistryKeyNode? _selectedKey;
    [ObservableProperty] private RegistryEntryRow? _selectedEntry;
    [ObservableProperty] private string _path = "Workspace\\Desktop\\Preferences";
    [ObservableProperty] private string _name = "Settings";
    [ObservableProperty] private RegistryScope _scope = RegistryScope.Workspace;
    [ObservableProperty] private RegistryValueType _valueType = RegistryValueType.Json;
    [ObservableProperty] private string _valueText = "{}";
    public IReadOnlyList<RegistryScope> Scopes { get; } = Enum.GetValues<RegistryScope>();
    public IReadOnlyList<RegistryValueType> ValueTypes { get; } = Enum.GetValues<RegistryValueType>();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var entriesTask = client.ListAsync();
            var summaryTask = client.GetSummaryAsync();
            await Task.WhenAll(entriesTask, summaryTask);
            _allEntries = (await entriesTask).ToList();
            Keys.Clear();
            foreach (var entry in _allEntries) AddKey(entry);
            SelectedKey ??= Keys.FirstOrDefault();
            RebuildEntries();
            StatusText = _allEntries.Count == 0 ? "No values. Enter a path, name, type, and data to create one." : $"{_allEntries.Count} value(s).";
        }
        catch (Exception ex) { StatusText = $"Registry unavailable: {ex.Message}"; }
        finally { IsLoading = false; }
    }

    partial void OnSelectedKeyChanged(RegistryKeyNode? value) => RebuildEntries();
    partial void OnSelectedEntryChanged(RegistryEntryRow? value)
    {
        if (value is null) return;
        Scope = value.Source.Scope; Path = value.Source.Path; Name = value.Source.Name; ValueType = value.Source.ValueType; ValueText = value.Source.DesiredValue.GetRawText();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            using var document = JsonDocument.Parse(ValueText);
            await client.SaveAsync(new PutRegistryEntryRequest(Scope, Path, Name, ValueType, document.RootElement.Clone()));
            StatusText = "Value saved.";
            await RefreshAsync();
            SelectedKey = FindKey($"{Scope}\\{Path}");
        }
        catch (Exception ex) { StatusText = $"Could not save value: {ex.Message}"; }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedEntry is null) return;
        try { await client.DeleteAsync(SelectedEntry.Source.Scope, SelectedEntry.Source.Path, SelectedEntry.Source.Name); StatusText = "Value deleted."; await RefreshAsync(); }
        catch (Exception ex) { StatusText = $"Could not delete value: {ex.Message}"; }
    }

    [RelayCommand]
    private void NewValue()
    {
        SelectedEntry = null; Name = "NewValue"; ValueType = RegistryValueType.String; ValueText = "\"\"";
    }

    private void RebuildEntries()
    {
        Entries.Clear();
        var key = SelectedKey?.Key;
        foreach (var entry in _allEntries.Where(x => key is null || $"{x.Scope}\\{x.Path}" == key)) Entries.Add(RegistryEntryRow.From(entry));
    }

    private void AddKey(RegistryEntryDto entry)
    {
        RegistryKeyNode? current = null;
        foreach (var segment in new[] { entry.Scope.ToString() }.Concat(entry.Path.Split('\\', StringSplitOptions.RemoveEmptyEntries)))
        {
            var collection = current?.Children ?? Keys;
            var parentKey = current?.Key;
            var key = parentKey is null ? segment : parentKey + "\\" + segment;
            current = collection.FirstOrDefault(x => x.Key == key) ?? new RegistryKeyNode(segment, key);
            if (!collection.Contains(current)) collection.Add(current);
        }
    }

    private RegistryKeyNode? FindKey(string key)
    {
        RegistryKeyNode? current = null;
        foreach (var segment in key.Split('\\'))
        {
            var collection = current?.Children ?? Keys;
            current = collection.FirstOrDefault(x => x.Key == (current is null ? segment : current.Key + "\\" + segment));
            if (current is null) return null;
        }
        return current;
    }
}

public sealed record RegistryEntryRow(RegistryEntryDto Source, string Name, string DesiredValue, string Type)
{
    public static RegistryEntryRow From(RegistryEntryDto entry) => new(entry, entry.Name, Format(entry.DesiredValue), entry.ValueType.ToString());
    private static string Format(JsonElement value) => value.GetRawText() is { Length: > 240 } text ? text[..237] + "…" : value.GetRawText();
}

public sealed class RegistryKeyNode(string name, string key)
{
    public string Name { get; } = name;
    public string Key { get; } = key;
    public ObservableCollection<RegistryKeyNode> Children { get; } = [];
}
