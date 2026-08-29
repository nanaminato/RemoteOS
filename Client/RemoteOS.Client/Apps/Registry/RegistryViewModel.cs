using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Client.Localization;
using RemoteOS.Protocol.Registry;

namespace Client.Apps.Registry;

public sealed partial class RegistryViewModel(IRegistryClient client) : ObservableObject
{
    private List<RegistryEntryDto> _allEntries = [];
    private HashSet<string> _expandedKeys = new(StringComparer.Ordinal);
    public ObservableCollection<RegistryKeyNode> Keys { get; } = [];
    public ObservableCollection<RegistryEntryRow> Entries { get; } = [];
    [ObservableProperty] private string _statusText = LocalizedText.Get("registry.status.loading", "Loading registry…");
    [ObservableProperty] private string _navigationPathInput = "HKEY_USERS";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private RegistryKeyNode? _selectedKey;
    [ObservableProperty] private RegistryEntryRow? _selectedEntry;
    [ObservableProperty] private string _path = "Workspace\\Desktop\\Preferences";
    [ObservableProperty] private string _name = "Settings";
    [ObservableProperty] private RegistryScope _scope = RegistryScope.Workspace;
    [ObservableProperty] private RegistryValueType _valueType = RegistryValueType.Json;
    [ObservableProperty] private string _valueText = "{}";
    public Func<RegistryEntryRow, Task>? ShowEditDialogAsync { get; set; }
    public Func<RegistryScope, string, Task>? ShowNewValueDialogAsync { get; set; }
    public IReadOnlyList<RegistryScope> Scopes { get; } = Enum.GetValues<RegistryScope>();
    public IReadOnlyList<RegistryValueType> ValueTypes { get; } = Enum.GetValues<RegistryValueType>();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        try
        {
            var selectedKey = SelectedKey?.Key;
            _expandedKeys = Keys.SelectMany(Flatten).Where(node => node.IsExpanded).Select(node => node.Key).ToHashSet(StringComparer.Ordinal);
            var entriesTask = client.ListAsync();
            var summaryTask = client.GetSummaryAsync();
            await Task.WhenAll(entriesTask, summaryTask);
            _allEntries = (await entriesTask).ToList();
            Keys.Clear();
            Keys.Add(new RegistryKeyNode("HKEY_USERS", "__root"));
            var root = Keys[0];
            root.Children.Add(new RegistryKeyNode(LocalizedText.Get("registry.tree.current_user", "Current User"), "__current"));
            root.Children.Add(new RegistryKeyNode(LocalizedText.Get("registry.tree.other_users", "Other Users (access denied)"), "__other"));
            foreach (var entry in _allEntries) AddKey(entry);
            SelectedKey = selectedKey is null ? root.Children[0].Children.FirstOrDefault() : FindKey(selectedKey) ?? root.Children[0].Children.FirstOrDefault();
            RebuildEntries();
            StatusText = _allEntries.Count == 0 ? LocalizedText.Get("registry.status.empty", "No values in this key.") : string.Format(LocalizedText.Get("registry.status.count", "{0} value(s)."), _allEntries.Count);
        }
        catch (Exception ex) { StatusText = string.Format(LocalizedText.Get("registry.status.unavailable", "Registry unavailable: {0}"), ex.Message); }
        finally { IsLoading = false; }
    }

    partial void OnSelectedKeyChanged(RegistryKeyNode? value) { RebuildEntries(); NavigationPathInput = DisplayPath(value); }
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
            StatusText = LocalizedText.Get("registry.status.saved", "Value saved.");
            await RefreshAsync();
            SelectedKey = FindKey(Path);
        }
        catch (Exception ex) { StatusText = string.Format(LocalizedText.Get("registry.status.save_failed", "Could not save value: {0}"), ex.Message); }
    }

    [RelayCommand]
    private async Task DeleteAsync()
    {
        if (SelectedEntry is null) return;
        try { await client.DeleteAsync(SelectedEntry.Source.Scope, SelectedEntry.Source.Path, SelectedEntry.Source.Name); StatusText = LocalizedText.Get("registry.status.deleted", "Value deleted."); await RefreshAsync(); }
        catch (Exception ex) { StatusText = string.Format(LocalizedText.Get("registry.status.delete_failed", "Could not delete value: {0}"), ex.Message); }
    }

    [RelayCommand]
    private Task NewValue()
    {
        var source = _allEntries.FirstOrDefault(x => x.Path == SelectedKey?.Key);
        return ShowNewValueDialogAsync is null ? Task.CompletedTask : ShowNewValueDialogAsync(source?.Scope ?? RegistryScope.Workspace, source?.Path ?? "Workspace\\Desktop\\Preferences");
    }

    [RelayCommand]
    private Task EditSelectedAsync() => SelectedEntry is not null && ShowEditDialogAsync is not null
        ? ShowEditDialogAsync(SelectedEntry) : Task.CompletedTask;

    [RelayCommand]
    private void Navigate()
    {
        var input = NavigationPathInput.Trim().Trim('\\');
        const string root = "HKEY_USERS";
        if (input.StartsWith(root, StringComparison.OrdinalIgnoreCase)) input = input[root.Length..].Trim('\\');
        if (input.StartsWith("Current User\\", StringComparison.OrdinalIgnoreCase)) input = input["Current User\\".Length..];
        if (input.StartsWith("Other Users", StringComparison.OrdinalIgnoreCase)) { StatusText = LocalizedText.Get("registry.status.access_denied", "Other users' registry hives cannot be opened."); return; }
        if (string.IsNullOrWhiteSpace(input)) { SelectedKey = Keys.SingleOrDefault(x => x.Key == "__root"); return; }
        var found = FindKey(input);
        if (found is null) { StatusText = string.Format(LocalizedText.Get("registry.status.path_not_found", "Registry key not found: {0}"), NavigationPathInput); return; }
        SelectedKey = found;
    }

    private void RebuildEntries()
    {
        Entries.Clear();
        var key = SelectedKey?.Key;
        if (key is "__root" or "__current" or "__other") key = null;
        foreach (var entry in _allEntries.Where(x => key is null || x.Path == key)) Entries.Add(RegistryEntryRow.From(entry));
    }

    private void AddKey(RegistryEntryDto entry)
    {
        var root = Keys.Single(x => x.Key == "__root");
        var current = root.Children.Single(x => x.Key == "__current");
        foreach (var segment in entry.Path.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            var collection = current?.Children ?? Keys;
            var parentKey = current?.Key;
            var key = parentKey is null || parentKey.StartsWith("__", StringComparison.Ordinal) ? segment : parentKey + "\\" + segment;
            current = collection.FirstOrDefault(x => x.Key == key) ?? new RegistryKeyNode(segment, key) { IsExpanded = _expandedKeys.Contains(key) };
            if (!collection.Contains(current)) collection.Add(current);
        }
    }

    private RegistryKeyNode? FindKey(string key)
    {
        RegistryKeyNode? current = Keys.SingleOrDefault(x => x.Key == "__root")?.Children.SingleOrDefault(x => x.Key == "__current");
        if (current is null) return null;
        foreach (var segment in key.Split('\\'))
        {
            var collection = current?.Children ?? Keys;
            current = collection.FirstOrDefault(x => x.Key == (current is null || current.Key.StartsWith("__", StringComparison.Ordinal) ? segment : current.Key + "\\" + segment));
            if (current is null) return null;
        }
        return current;
    }

    private static string DisplayPath(RegistryKeyNode? node) => node?.Key switch
    {
        null or "__root" => "HKEY_USERS",
        "__current" => "HKEY_USERS\\Current User",
        "__other" => "HKEY_USERS\\Other Users",
        var key => "HKEY_USERS\\Current User\\" + key,
    };

    private static IEnumerable<RegistryKeyNode> Flatten(RegistryKeyNode node)
    {
        yield return node;
        foreach (var child in node.Children.SelectMany(Flatten)) yield return child;
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
    public bool IsExpanded { get; set; }
}
