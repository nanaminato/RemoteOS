using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.Registry;

namespace Client.Apps.Registry;

public sealed partial class RegistryViewModel(IRegistryClient client) : ObservableObject
{
    public ObservableCollection<RegistryEntryRow> Entries { get; } = [];
    [ObservableProperty] private string _statusText = "Loading registry…";
    [ObservableProperty] private string _summaryText = string.Empty;
    [ObservableProperty] private bool _isLoading;

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
            Entries.Clear();
            foreach (var entry in await entriesTask) Entries.Add(RegistryEntryRow.From(entry));
            var summary = await summaryTask;
            SummaryText = $"Pending: {summary.PendingSyncCount} · Failed: {summary.FailedCount} · Restart required: {summary.RestartRequiredCount}";
            StatusText = Entries.Count == 0 ? "No registered configuration values are available." : $"{Entries.Count} registered value(s).";
        }
        catch (Exception ex) { StatusText = $"Registry unavailable: {ex.Message}"; }
        finally { IsLoading = false; }
    }
}

public sealed record RegistryEntryRow(string Scope, string Path, string Name, string DesiredValue, string Type, string State, string ApplyMode, string Revision)
{
    public static RegistryEntryRow From(RegistryEntryDto entry) => new(entry.Scope.ToString(), entry.Path, entry.Name,
        Format(entry.DesiredValue), entry.ValueType.ToString(), entry.State.ToString(), entry.ApplyMode.ToString(), entry.Revision.ToString());
    private static string Format(JsonElement value) => value.GetRawText() is { Length: > 240 } text ? text[..237] + "…" : value.GetRawText();
}
