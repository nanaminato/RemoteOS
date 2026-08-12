using System.Collections.ObjectModel;
using Client.Localization;
using Client.Services.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.Firewall;

namespace Client.Apps.Firewall;

/// <summary>Window-local Linux UFW editor state. Passwords are requested one-shot and never retained by the view model.</summary>
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
    [ObservableProperty] private bool _isLoading;

    public bool IsRoot => string.Equals(session.CurrentUser?.Username, "root", StringComparison.Ordinal);
    /// <summary>Provided by the window so a credential is collected only for the pending operation.</summary>
    public Func<Task<string?>>? RequestPasswordAsync { get; set; }

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
    private Task EnableAsync() => ApplyAsync(confirmation => client.SetEnabledAsync(new UpdateFirewallEnabledRequest(true, confirmation)));
    [RelayCommand]
    private Task DisableAsync() => ApplyAsync(confirmation => client.SetEnabledAsync(new UpdateFirewallEnabledRequest(false, confirmation)));
    [RelayCommand]
    private Task SaveDefaultsAsync() => ApplyAsync(confirmation => client.SetDefaultsAsync(new UpdateFirewallDefaultsRequest(IncomingPolicy, OutgoingPolicy, confirmation)));
    [RelayCommand]
    private async Task AddRuleAsync()
    {
        if (string.IsNullOrWhiteSpace(Port)) { StatusText = LocalizedText.Get("firewall.validation.port_required"); return; }
        await ApplyAsync(confirmation => client.CreateRuleAsync(new CreateFirewallRuleRequest(Action, Direction, Protocol, Source, Destination, Port, confirmation)));
    }
    [RelayCommand]
    private Task DeleteRuleAsync(FirewallRuleDto? rule) => rule is null ? Task.CompletedTask : ApplyAsync(confirmation => client.DeleteRuleAsync(rule.Number, new DeleteFirewallRuleRequest(confirmation)));

    private async Task ApplyAsync(Func<FirewallCredentialConfirmation?, Task<FirewallOperationResult>> operation)
    {
        FirewallCredentialConfirmation? confirmation = null;
        if (!IsRoot)
        {
            var password = await (RequestPasswordAsync?.Invoke() ?? Task.FromResult<string?>(null));
            if (password is null) return;
            confirmation = new FirewallCredentialConfirmation(password);
        }

        IsLoading = true;
        try
        {
            var result = await operation(confirmation);
            StatusText = result.Success ? LocalizedText.Get("firewall.operation.succeeded") : LocalizedText.Format("firewall.operation.failed", result.ProblemCode);
        }
        catch (Exception exception) { StatusText = LocalizedText.Format("firewall.operation.failed", exception.Message); }
        finally
        {
            IsLoading = false;
        }
        await RefreshAsync();
    }
}
