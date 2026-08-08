using System.Collections.ObjectModel;
using System.Diagnostics;
using Client.Apps.TaskManager;
using Client.Services;
using Client.Services.Auth;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Protocol.SystemMonitor;

namespace Client.Apps.Settings.ViewModels;

/// <summary>Connection details, reachable server addresses, and a lightweight latency test.</summary>
public sealed partial class NetworkPageViewModel : SettingsPageViewModel
{
    private readonly IAuthSession _session;
    private readonly IRemoteOsClient _remote;
    private readonly ITaskManagerClient _system;

    public NetworkPageViewModel(
        ShellSettings settings,
        IAuthSession session,
        IRemoteOsClient remote,
        ITaskManagerClient system,
        Action? save)
        : base(settings, save)
    {
        _session = session;
        _remote = remote;
        _system = system;
        ServerAddresses = new ObservableCollection<NetworkAddressDto>();
    }

    public override string Glyph => "🌐";
    public override string DisplayNameKey => "settings.page.network";
    public override string DisplayName => "Network";

    public string ConnectionState => _session.State switch
    {
        AuthSessionState.Authenticated => "已连接",
        AuthSessionState.Connecting => "连接中…",
        _ => "未连接",
    };

    public string ServerUrl => _session.ServerUrl ?? "—";
    public string UserName => _session.CurrentUser?.Username ?? "—";
    public string WorkspaceName => _session.CurrentWorkspace?.Name ?? "—";
    public bool IsConnected => _session.State == AuthSessionState.Authenticated;
    public ObservableCollection<NetworkAddressDto> ServerAddresses { get; }

    [ObservableProperty] private string _latencyText = "未测试";
    [ObservableProperty] private bool _isTesting;
    [ObservableProperty] private string _serverAddressesStatus = "尚未获取服务器地址。";
    [ObservableProperty] private bool _isLoadingServerAddresses;

    public async Task LoadServerAddressesAsync()
    {
        if (!IsConnected)
        {
            ServerAddresses.Clear();
            ServerAddressesStatus = "未连接到服务器。";
            return;
        }

        IsLoadingServerAddresses = true;
        ServerAddressesStatus = "正在获取服务器地址…";
        try
        {
            var addresses = await _system.GetNetworkAddressesAsync();
            ServerAddresses.Clear();
            foreach (var address in addresses)
                ServerAddresses.Add(address);
            ServerAddressesStatus = addresses.Count == 0
                ? "未发现可用的非回环 IPv4 或 IPv6 地址。"
                : $"发现 {addresses.Count} 个服务器地址。";
        }
        catch (Exception ex)
        {
            ServerAddresses.Clear();
            ServerAddressesStatus = $"无法获取服务器地址：{ex.Message}";
        }
        finally
        {
            IsLoadingServerAddresses = false;
        }
    }

    [RelayCommand]
    private Task RefreshServerAddressesAsync() => LoadServerAddressesAsync();

    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task TestConnectionAsync()
    {
        if (_session is not { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens })
        {
            LatencyText = "未连接，无法测试";
            return;
        }

        IsTesting = true;
        LatencyText = "测试中…";
        try
        {
            var sw = Stopwatch.StartNew();
            await _remote.GetMeAsync(url, tokens.AccessToken);
            sw.Stop();
            LatencyText = $"{sw.ElapsedMilliseconds} ms";
        }
        catch (Exception ex)
        {
            LatencyText = $"失败：{ex.Message}";
        }
        finally
        {
            IsTesting = false;
        }
    }

    private bool CanTest => !IsTesting;
    partial void OnIsTestingChanged(bool value) => TestConnectionCommand.NotifyCanExecuteChanged();
}
