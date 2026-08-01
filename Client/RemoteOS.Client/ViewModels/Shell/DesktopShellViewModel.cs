using System.Collections.ObjectModel;
using Avalonia.Threading;
using Client.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Windows;
using RemoteOS.Runtime;
using RemoteOS.WindowManager;

namespace Client.ViewModels.Shell;

/// <summary>
/// Root view-model for the RemoteOS desktop shell. Owns the window manager facade exposed to
/// the view, the desktop / start menu application entries, the taskbar window list and the clock.
/// </summary>
public partial class DesktopShellViewModel : ObservableObject
{
    private readonly WindowManager _windowManager;
    private readonly ApplicationManager _applications;
    private readonly ShellSettings _settings;
    private readonly Action _shutdown;

    public DesktopShellViewModel(
        WindowManager windowManager,
        ApplicationManager applications,
        ShellSettings settings,
        Action shutdown)
    {
        _windowManager = windowManager;
        _applications = applications;
        _settings = settings;
        _shutdown = shutdown;

        StartClock();
    }

    public WindowManager WindowManager => _windowManager;
    public ShellSettings Settings => _settings;

    /// <summary>Live list of open windows (bound to the taskbar). Source is the window manager.</summary>
    public IReadOnlyList<ManagedWindow> Windows => _windowManager.Windows;

    public ObservableCollection<AppEntryViewModel> DesktopIcons { get; } = new();
    public ObservableCollection<AppEntryViewModel> StartApps { get; } = new();

    [ObservableProperty] private bool _isStartOpen;
    [ObservableProperty] private string _clock = string.Empty;
    [ObservableProperty] private string _dateText = string.Empty;

    /// <summary>Populate desktop + start menu from registered applications. Call after DI registration.</summary>
    public void PopulateDesktop()
    {
        var entries = _applications.Registered
            .Select(i => new AppEntryViewModel(i, _applications))
            .ToList();

        DesktopIcons.Clear();
        StartApps.Clear();
        foreach (var entry in entries)
        {
            DesktopIcons.Add(entry);
            StartApps.Add(entry);
        }
    }

    [RelayCommand]
    private void ToggleStart() => IsStartOpen = !IsStartOpen;

    [RelayCommand]
    private void CloseStart() => IsStartOpen = false;

    [RelayCommand]
    private void Launch(AppId id)
    {
        _applications.Launch(id);
        IsStartOpen = false;
    }

    [RelayCommand]
    private void Shutdown() => _shutdown.Invoke();

    /// <summary>Taskbar button click: restore minimized windows, or minimize the active one.</summary>
    [RelayCommand]
    private void ToggleTaskbarItem(ManagedWindow window)
    {
        if (window.State == WindowState.Minimized)
            _windowManager.Restore(window);
        else if (window.IsActive)
            _windowManager.Minimize(window);
        else
            _windowManager.Focus(window);
    }

    private void StartClock()
    {
        void Tick()
        {
            var now = DateTime.Now;
            Clock = now.ToString("HH:mm");
            DateText = now.ToString("M/d ddd");
        }

        Tick();
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => Tick());
        timer.Start();
    }
}
