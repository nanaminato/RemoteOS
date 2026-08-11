using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Client.Apps.PortForwarding.ViewModels;

/// <summary>UI state for the device-local SSH forward manager.</summary>
public sealed partial class PortForwardingViewModel : ObservableObject, IDisposable
{
    private readonly IPortForwardingService _service;

    public PortForwardingViewModel(IPortForwardingService service)
    {
        _service = service;
        var settings = service.GetSettings();
        SshHost = settings.SshHost ?? string.Empty;
        SshUser = settings.SshUser ?? string.Empty;
        SshPortText = settings.SshPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Forwards = new ObservableCollection<PortForwardInfo>();
        RefreshForwards();
        _service.ForwardsChanged += OnForwardsChanged;
    }

    public ObservableCollection<PortForwardInfo> Forwards { get; }

    [ObservableProperty] private string _targetAddress = "http://localhost:7000";
    [ObservableProperty] private string _preferredLocalPortText = "7000";
    [ObservableProperty] private string _sshHost = string.Empty;
    [ObservableProperty] private string _sshUser = string.Empty;
    [ObservableProperty] private string _sshPortText = "22";
    [ObservableProperty] private PortForwardInfo? _selectedForward;
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private bool _isBusy;

    public bool HasSelectedForward => SelectedForward is not null;

    [RelayCommand]
    private void SaveConnectionSettings()
    {
        if (!int.TryParse(SshPortText, out var sshPort) || sshPort is < 1 or > 65535)
        {
            StatusText = "SSH port must be between 1 and 65535.";
            return;
        }
        _service.SaveSettings(new PortForwardingSettings(SshHost, SshUser, sshPort));
        StatusText = "Saved on this device only. SSH authentication uses your system SSH configuration or key agent.";
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        await RunAsync(async () =>
        {
            var forward = await _service.StartAsync(ParseRequest());
            SelectedForward = forward;
            StatusText = $"Forwarding started: {forward.LocalUri}";
        });
    }

    [RelayCommand(CanExecute = nameof(HasSelectedForward))]
    private async Task UpdateSelectedAsync()
    {
        if (SelectedForward is null) return;
        await RunAsync(async () =>
        {
            var forward = await _service.UpdateAsync(SelectedForward.Id, ParseRequest());
            SelectedForward = forward;
            StatusText = $"Forwarding updated: {forward.LocalUri}";
        });
    }

    [RelayCommand(CanExecute = nameof(HasSelectedForward))]
    private async Task RemoveSelectedAsync()
    {
        if (SelectedForward is null) return;
        var selected = SelectedForward;
        await RunAsync(async () =>
        {
            await _service.RemoveAsync(selected.Id);
            SelectedForward = null;
            StatusText = "Forwarding stopped.";
        });
    }

    [RelayCommand]
    private void Refresh() => RefreshForwards();

    partial void OnSelectedForwardChanged(PortForwardInfo? value)
    {
        OnPropertyChanged(nameof(HasSelectedForward));
        UpdateSelectedCommand.NotifyCanExecuteChanged();
        RemoveSelectedCommand.NotifyCanExecuteChanged();
        if (value is null) return;
        TargetAddress = $"{value.Scheme}://{value.RemoteHost}:{value.RemotePort}{value.PathAndQuery}";
        PreferredLocalPortText = value.LocalPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    private async Task RunAsync(Func<Task> operation)
    {
        if (IsBusy) return;
        IsBusy = true;
        try { await operation(); }
        catch (Exception ex) { StatusText = ex.Message; }
        finally
        {
            IsBusy = false;
            RefreshForwards();
        }
    }

    private PortForwardRequest ParseRequest()
    {
        var raw = TargetAddress.Trim();
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
            Uri.TryCreate("http://" + raw, UriKind.Absolute, out uri);
        if (uri is null || uri.Port is < 1 or > 65535)
            throw new ArgumentException("Enter a loopback target such as http://localhost:7000.");
        int? preferred = null;
        if (!string.IsNullOrWhiteSpace(PreferredLocalPortText))
        {
            if (!int.TryParse(PreferredLocalPortText, out var parsed) || parsed is < 1 or > 65535)
                throw new ArgumentException("Preferred local port must be between 1 and 65535.");
            preferred = parsed;
        }
        return new PortForwardRequest(uri.Host, uri.Port, uri.Scheme, preferred, uri.PathAndQuery);
    }

    private void OnForwardsChanged(object? sender, EventArgs args)
        => Dispatcher.UIThread.Post(RefreshForwards);

    private void RefreshForwards()
    {
        var current = _service.List();
        Forwards.Clear();
        foreach (var forward in current) Forwards.Add(forward);
        if (SelectedForward is { } selected)
            SelectedForward = current.FirstOrDefault(forward => forward.Id == selected.Id);
    }

    public void Dispose() => _service.ForwardsChanged -= OnForwardsChanged;
}
