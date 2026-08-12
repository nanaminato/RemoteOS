using System.Collections.ObjectModel;
using Client.Localization;
using Client.Services.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.Firewall;

namespace Client.Apps.Firewall;

/// <summary>Window-local Linux UFW editor state. Passwords are one-shot and never retained after a mutation.</summary>
public sealed partial class FirewallViewModel(IRemoteFirewallClient client, IAuthSession session) : ObservableObject
{
    public ObservableCollection<FirewallRuleDto> Rules { get; } = [];
    [ObservableProperty] private FirewallRuleDto? _selectedRule;
    [ObservableProperty] private string _statusText = LocalizedText.Get("firewall.status.loading");
    [ObservableProperty] private bool _isEnabled;
    [ObservableProperty] private string _incomingPolicy = "deny";
    [ObservableProperty] private string _outgoingPolicy = "allow";
    [ObservableProperty] private string _action = "allow";
    [ObservableProperty] private string _direction = "in";
    [ObservableProperty] private string _protocol = "tcp";
    [ObservableProperty] private string _source = "any";
    [ObservableProperty] private string _destination = "any";
    [ObservableProperty] private string _port = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private bool _isLoading;

    public bool IsRoot => string.Equals(session.CurrentUser?.Username, "root", StringComparison.Ordinal);

    public async Task StartAsync() => await RefreshAsync();

    [RelayCommand]
    private async Task RefreshAsync()
    {
        IsLoading = true;
        try
        {
            var status = await client.GetStatusAsync();
            Rules.Clear();
            if (!status.IsAvailable)
            {
                StatusText = LocalizedText.Format("firewall.status.unavailable", status.ProblemCode);
                return;
            }
            IsEnabled = status.IsEnabled;
            IncomingPolicy = status.DefaultIncomingPolicy ?? "deny";
            OutgoingPolicy = status.DefaultOutgoingPolicy ?? "allow";
            foreach (var rule in await client.ListRulesAsync()) Rules.Add(rule);
            StatusText = LocalizedText.Format("firewall.status.ready", status.Backend, status.Version ?? "");
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("firewall.status.failed", exception.Message); }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    private Task EnableAsync() => ApplyAsync(() => client.SetEnabledAsync(new UpdateFirewallEnabledRequest(true, Confirmation())));
    [RelayCommand]
    private Task DisableAsync() => ApplyAsync(() => client.SetEnabledAsync(new UpdateFirewallEnabledRequest(false, Confirmation())));
    [RelayCommand]
    private Task SaveDefaultsAsync() => ApplyAsync(() => client.SetDefaultsAsync(new UpdateFirewallDefaultsRequest(IncomingPolicy, OutgoingPolicy, Confirmation())));
    [RelayCommand]
    private async Task AddRuleAsync()
    {
        if (string.IsNullOrWhiteSpace(Port)) { StatusText = LocalizedText.Get("firewall.validation.port_required"); return; }
        await ApplyAsync(() => client.CreateRuleAsync(new CreateFirewallRuleRequest(Action, Direction, Protocol, Source, Destination, Port, Confirmation())));
    }
    [RelayCommand]
    private Task DeleteRuleAsync(FirewallRuleDto? rule) => rule is null ? Task.CompletedTask : ApplyAsync(() => client.DeleteRuleAsync(rule.Number, new DeleteFirewallRuleRequest(Confirmation())));

    private FirewallCredentialConfirmation? Confirmation() => IsRoot ? null : new FirewallCredentialConfirmation(Password);
    private async Task ApplyAsync(Func<Task<FirewallOperationResult>> operation)
    {
        IsLoading = true;
        try
        {
            var result = await operation();
            StatusText = result.Success ? LocalizedText.Get("firewall.operation.succeeded") : LocalizedText.Format("firewall.operation.failed", result.ProblemCode);
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("firewall.operation.failed", exception.Message); }
        finally
        {
            // Password belongs to this single request only, even when it fails.
            Password = string.Empty;
            IsLoading = false;
        }
        await RefreshAsync();
    }
}
