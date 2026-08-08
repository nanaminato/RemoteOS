using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using Client.Services;
using Client.Services.Auth;
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
    private readonly LocalizationService _localization;
    private readonly Action _shutdown;
    private readonly IAuthSession _session;

    public DesktopShellViewModel(
        WindowManager windowManager,
        ApplicationManager applications,
        ShellSettings settings,
        LocalizationService localization,
        IAuthSession session,
        Action shutdown)
    {
        _windowManager = windowManager;
        _applications = applications;
        _settings = settings;
        _localization = localization;
        _session = session;
        _shutdown = shutdown;

        _windowManager.WindowOpened += (_, _) => RefreshTaskbarGroups();
        _windowManager.WindowClosed += (_, _) => RefreshTaskbarGroups();
        _windowManager.ActiveWindowChanged += (_, _) => RefreshTaskbarGroups();
        _applications.RegistryChanged += (_, _) => Dispatcher.UIThread.Post(PopulateDesktop);
        _localization.LanguageChanged += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            PopulateDesktop();
            OnPropertyChanged(nameof(ConnectionServer));
            OnPropertyChanged(nameof(ConnectionUser));
            OnPropertyChanged(nameof(ConnectionWorkspace));
        });

        StartClock();
    }

    public WindowManager WindowManager => _windowManager;
    public ShellSettings Settings => _settings;
    public string ConnectionServer => _session.ServerUrl ?? "未连接";
    public string ConnectionUser => _session.CurrentUser?.Username ?? "未知用户";
    public string ConnectionWorkspace => _session.CurrentWorkspace?.Name ?? "默认工作区";

    /// <summary>Live, application-grouped taskbar items.</summary>
    public ObservableCollection<TaskbarGroupViewModel> TaskbarGroups { get; } = new();

    public ObservableCollection<AppEntryViewModel> DesktopIcons { get; } = new();
    public ObservableCollection<AppEntryViewModel> StartApps { get; } = new();

    [ObservableProperty] private bool _isStartOpen;
    [ObservableProperty] private bool _areDesktopIconsVisible = true;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTaskbarPreviewOpen))]
    private TaskbarGroupViewModel? _openTaskbarGroup;
    [ObservableProperty] private string _clock = string.Empty;
    [ObservableProperty] private string _dateText = string.Empty;

    /// <summary>Populate desktop + start menu from registered applications. Call after DI registration.</summary>
    public void PopulateDesktop()
    {
        var entries = _applications.Registered
            .Select(i => new AppEntryViewModel(i with
            {
                DisplayName = _localization.Get(i.DisplayName),
                Description = i.Description is null ? null : _localization.Get(i.Description),
            }, _applications))
            .ToList();

        DesktopIcons.Clear();
        StartApps.Clear();
        foreach (var entry in entries)
        {
            DesktopIcons.Add(entry);
            StartApps.Add(entry);
        }

        RefreshTaskbarGroups();
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

    /// <summary>Restores the desktop launcher list from the current application registry.</summary>
    [RelayCommand]
    private void RefreshDesktop() => PopulateDesktop();

    [RelayCommand]
    private void OpenFileExplorer() => LaunchApplication("remoteos.explorer");

    [RelayCommand]
    private void OpenTerminal() => LaunchApplication("remoteos.terminal");

    [RelayCommand]
    private void OpenSettings() => LaunchApplication("remoteos.settings");

    [RelayCommand]
    private void OpenTaskManager() => LaunchApplication("remoteos.taskmanager");

    [RelayCommand]
    private void ShowDesktop()
    {
        foreach (var window in _windowManager.Windows.Where(window => window.State != WindowState.Minimized).ToList())
            _windowManager.Minimize(window);

        IsStartOpen = false;
        OpenTaskbarGroup = null;
    }

    /// <summary>
    /// A single-window group keeps the familiar taskbar toggle behavior. A multi-window
    /// group opens its preview strip so the user can choose the exact window to activate or close.
    /// </summary>
    [RelayCommand]
    private void ToggleTaskbarGroup(TaskbarGroupViewModel group)
    {
        IsStartOpen = false;

        if (group.HasMultipleWindows)
        {
            OpenTaskbarGroup = ReferenceEquals(OpenTaskbarGroup, group) ? null : group;
            return;
        }

        if (group.Windows.FirstOrDefault() is { } window)
            ToggleSingleTaskbarWindow(window);
    }

    private void ToggleSingleTaskbarWindow(ManagedWindow window)
    {
        if (window.State == WindowState.Minimized)
            _windowManager.Restore(window);
        else if (window.IsActive)
            _windowManager.Minimize(window);
        else
            _windowManager.Focus(window);
    }

    [RelayCommand]
    private void ActivateTaskbarWindow(ManagedWindow window)
    {
        if (window.State == WindowState.Minimized)
            _windowManager.Restore(window);
        else
            _windowManager.Focus(window);
        OpenTaskbarGroup = null;
    }

    [RelayCommand]
    private void CloseTaskbarWindow(ManagedWindow window)
        => _windowManager.Close(window);

    public bool IsTaskbarPreviewOpen => OpenTaskbarGroup is not null;

    [RelayCommand]
    private void CloseTaskbarPreview() => OpenTaskbarGroup = null;

    private void LaunchApplication(string id)
    {
        _applications.Launch(new AppId(id));
        IsStartOpen = false;
        OpenTaskbarGroup = null;
    }

    private void RefreshTaskbarGroups()
    {
        var groupedWindows = _windowManager.Windows
            .GroupBy(window => window.Info.OwnerAppId)
            .ToDictionary(group => group.Key, group => group.ToList());

        foreach (var group in TaskbarGroups.ToList())
        {
            if (groupedWindows.Remove(group.AppId, out var windows))
            {
                group.Update(windows);
                if (ReferenceEquals(OpenTaskbarGroup, group) && !group.HasMultipleWindows)
                    OpenTaskbarGroup = null;
            }
            else
            {
                if (ReferenceEquals(OpenTaskbarGroup, group))
                    OpenTaskbarGroup = null;
                TaskbarGroups.Remove(group);
            }
        }

        foreach (var (appId, windows) in groupedWindows)
        {
            var app = _applications.Get(appId);
            var displayName = _localization.Get(app?.Manifest.DisplayName ?? windows[0].Title);
            TaskbarGroups.Add(new TaskbarGroupViewModel(
                appId,
                displayName,
                windows));
        }
    }

    private void StartClock()
    {
        void Tick()
        {
            var now = DateTime.Now;
            var culture = SafeCulture(_settings.Language);
            var timeFmt = _settings.TimeFormat == "12h" ? "h:mm tt" : "HH:mm";
            Clock = now.ToString(timeFmt, culture);
            var dateFmt = string.IsNullOrWhiteSpace(_settings.DateFormat) ? "M/d ddd" : _settings.DateFormat;
            DateText = now.ToString(dateFmt, culture);
        }

        Tick();
        var timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Normal, (_, _) => Tick());
        timer.Start();

        static CultureInfo SafeCulture(string name)
        {
            try { return CultureInfo.GetCultureInfo(name); }
            catch { return CultureInfo.InvariantCulture; }
        }
    }
}
