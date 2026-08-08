using Client.Services;
using Client.Services.Developer;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Client.Apps.Settings.ViewModels;

/// <summary>Developer features are a top-level Settings category, separate from Applications.</summary>
public sealed class DeveloperPageViewModel : SettingsPageViewModel
{
    public DeveloperPageViewModel(ShellSettings settings, DeveloperModeService developerMode, Action? save)
        : base(settings, save) => DeveloperMode = new DeveloperModeViewModel(developerMode);

    public override string Glyph => "🛠️";
    public override string DisplayNameKey => "settings.page.developer";
    public override string DisplayName => "Developer";
    public DeveloperModeViewModel DeveloperMode { get; }
}

public sealed partial class DeveloperModeViewModel : ObservableObject
{
    private readonly DeveloperModeService _developerMode;

    public DeveloperModeViewModel(DeveloperModeService developerMode)
    {
        _developerMode = developerMode;
        _isEnabled = developerMode.IsEnabled;
    }

    [ObservableProperty] private bool _isEnabled;
    public string Endpoint => _developerMode.Endpoint;
    public string PairingToken => _developerMode.PairingToken;

    partial void OnIsEnabledChanged(bool value) => _developerMode.SetEnabled(value);

    [RelayCommand]
    private void RegeneratePairingToken()
    {
        _developerMode.RegeneratePairingToken();
        OnPropertyChanged(nameof(PairingToken));
    }
}
