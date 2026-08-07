using Avalonia.Threading;
using Client.Apps.Settings.ViewModels;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Runtime;
using RemoteOS.WindowManager;

namespace Client.Services;

/// <summary>Host-owned route into Settings for built-in and package applications.</summary>
public sealed class SettingsNavigationService : ISettingsNavigation
{
    private static readonly AppId SettingsAppId = new("remoteos.settings");

    private readonly ApplicationManager _applications;
    private readonly IWindowManager _windowManager;
    private ManagedWindow? _window;
    private SettingsViewModel? _viewModel;
    private bool _openApplicationsWhenRegistered;

    public SettingsNavigationService(ApplicationManager applications, IWindowManager windowManager)
    {
        _applications = applications;
        _windowManager = windowManager;
    }

    public Task OpenApplicationsAsync() => Dispatcher.UIThread.InvokeAsync(OpenApplications).GetTask();

    public void Register(ManagedWindow window, SettingsViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
        if (_openApplicationsWhenRegistered)
            NavigateToApplications();
    }

    public void Unregister(ManagedWindow window)
    {
        if (!ReferenceEquals(_window, window))
            return;
        _window = null;
        _viewModel = null;
    }

    private void OpenApplications()
    {
        if (_window is not null && _windowManager.Windows.Contains(_window))
        {
            NavigateToApplications();
            _windowManager.Focus(_window);
            return;
        }

        _openApplicationsWhenRegistered = true;
        _applications.Launch(SettingsAppId);
    }

    private void NavigateToApplications()
    {
        _openApplicationsWhenRegistered = false;
        _viewModel?.SelectApplicationsPage();
    }
}
