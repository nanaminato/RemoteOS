using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using Client.Apps.Explorer;
using Client.Apps.Explorer.Dialogs;
using Client.Apps.Settings;
using Client.Localization;
using Client.Services;
using Client.Services.Auth;
using Client.Services.DesktopRestore;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Windows;
using RemoteOS.Runtime;
using RemoteOS.WindowManager;
using RemoteOS.Protocol.Files;
using RemoteOS.Protocol.Workspace;

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
    private readonly DesktopRestoreOrchestrator _desktopRestore;
    private readonly IExplorerClient _files;
    private readonly IRemoteFileClipboard _fileClipboard;
    private readonly DefaultAppRegistry _defaultApps;
    private readonly ISettingsClient _settingsClient;
    private readonly IAppActivationDiagnostics _activationDiagnostics;
    private int _desktopFileLoadGeneration;

    public DesktopShellViewModel(
        WindowManager windowManager,
        ApplicationManager applications,
        ShellSettings settings,
        LocalizationService localization,
        IAuthSession session,
        Action shutdown,
        DesktopRestoreOrchestrator desktopRestore,
        IExplorerClient files,
        IRemoteFileClipboard fileClipboard,
        DefaultAppRegistry defaultApps,
        ISettingsClient settingsClient,
        IAppActivationDiagnostics activationDiagnostics)
    {
        _windowManager = windowManager;
        _applications = applications;
        _settings = settings;
        _localization = localization;
        _session = session;
        _shutdown = shutdown;
        _desktopRestore = desktopRestore;
        _files = files;
        _fileClipboard = fileClipboard;
        _defaultApps = defaultApps;
        _settingsClient = settingsClient;
        _activationDiagnostics = activationDiagnostics;

        _windowManager.WindowOpened += (_, _) => RefreshTaskbarGroups();
        _windowManager.WindowClosed += (_, _) => RefreshTaskbarGroups();
        _windowManager.ActiveWindowChanged += (_, _) => RefreshTaskbarGroups();
        _applications.RegistryChanged += (_, _) => Dispatcher.UIThread.Post(PopulateDesktop);
        _session.StateChanged += (_, state) => Dispatcher.UIThread.Post(() =>
        {
            if (state.State != AuthSessionState.Authenticated)
                _fileClipboard.Clear();
            PopulateDesktop();
        });
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
    public string ConnectionServer => _session.ServerUrl ?? T("shell.connection.not_connected", "Not connected");
    public string ConnectionUser => _session.CurrentUser?.Username ?? T("shell.connection.unknown_user", "Unknown user");
    public string ConnectionWorkspace => _session.CurrentWorkspace?.Name ?? T("shell.connection.default_workspace", "Default workspace");

    /// <summary>Called by the view after WindowManager has attached the desktop window host.</summary>
    public Task RestoreDesktopStateAsync(CancellationToken cancellationToken = default) =>
        _desktopRestore.RestoreAsync(cancellationToken);

    /// <summary>Live, application-grouped taskbar items.</summary>
    public ObservableCollection<TaskbarGroupViewModel> TaskbarGroups { get; } = new();

    public ObservableCollection<AppEntryViewModel> DesktopIcons { get; } = new();
    /// <summary>Entries from the authenticated user's remote Desktop special folder.</summary>
    public ObservableCollection<DesktopFileEntryViewModel> DesktopFiles { get; } = new();
    /// <summary>Application launchers and remote desktop files in the shared icon grid.</summary>
    public ObservableCollection<object> DesktopItems { get; } = new();
    public ObservableCollection<AppEntryViewModel> StartApps { get; } = new();

    // The shell supplies these UI callbacks. Keeping prompts and picker controls out of this
    // view-model lets the actual filesystem operations be shared by desktop context-menu items.
    public Func<string, string, string, Task<bool>>? RequestDesktopConfirmAsync { get; set; }
    public Func<IReadOnlyList<ApplicationInfo>, string, Task<OpenWithChoice?>>? RequestDesktopOpenWithAsync { get; set; }
    public Func<FilePropertiesDto, Task>? ShowDesktopPropertiesAsync { get; set; }

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
            // An app that needs a connected Linux Server must not be advertised on a Windows
            // Server desktop or Start menu. Launch still performs the same check for defense in depth.
            .Where(application => _applications.GetManifest(application.Id) is { } manifest
                && _applications.EvaluateCompatibility(manifest).IsCompatible)
            .Select(i => new AppEntryViewModel(Localize(i), _applications))
            .ToList();

        DesktopIcons.Clear();
        StartApps.Clear();
        foreach (var entry in entries)
        {
            DesktopIcons.Add(entry);
            StartApps.Add(entry);
        }

        RefreshDesktopItems();
        RefreshTaskbarGroups();
        _ = LoadDesktopFilesAsync();
    }

    private ApplicationInfo Localize(ApplicationInfo app)
    {
        var metadata = app.GetLocalizedMetadata(_localization.CurrentLanguage);
        return app with
        {
            DisplayName = T($"application.{app.Id.Value}.display_name", metadata.DisplayName),
            Description = metadata.Description is null
                ? null
                : T($"application.{app.Id.Value}.description", metadata.Description),
        };
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

    /// <summary>Refreshes application launchers and the authenticated user's desktop files.</summary>
    [RelayCommand]
    private void RefreshDesktop() => PopulateDesktop();

    [RelayCommand]
    private void OpenDesktopFolder()
    {
        if (!string.IsNullOrWhiteSpace(_desktopPath))
            _applications.Activate(new AppActivationRequest(RemoteOsActivationUris.ExplorerPath(_desktopPath)));
    }

    [RelayCommand]
    private void SelectDesktopItem(object? item)
    {
        foreach (var app in DesktopIcons)
            app.IsDesktopSelected = ReferenceEquals(app, item);
        foreach (var file in DesktopFiles)
            file.IsDesktopSelected = ReferenceEquals(file, item);
    }

    [RelayCommand]
    private void ClearDesktopSelection() => SelectDesktopItem(null);

    [RelayCommand]
    private void OpenDesktopApp(AppEntryViewModel? app)
    {
        if (app is not null) app.LaunchCommand.Execute(null);
    }

    /// <summary>Shows an application's details through its existing Settings permission/details route.</summary>
    [RelayCommand]
    private void ShowDesktopAppDetails(AppEntryViewModel? app)
    {
        if (app is not null)
            _applications.Activate(new AppActivationRequest(RemoteOsActivationUris.SettingsAppPermissions(app.Id)));
    }

    [RelayCommand]
    private void OpenDesktopEntry(DesktopFileEntryViewModel? item)
    {
        if (item is null)
        {
            RecordDesktopFileMenuDiagnostic("open command ignored: entry was null.");
            return;
        }

        if (item.IsDirectory)
        {
            var folderResult = _applications.Activate(new AppActivationRequest(RemoteOsActivationUris.ExplorerPath(item.Entry.Path)));
            RecordDesktopFileMenuDiagnostic($"folder open result: entry={item.DisplayName}, result={folderResult.Status}, target={folderResult.TargetAppId?.Value ?? "<none>"}.");
            return;
        }

        var extension = Path.GetExtension(item.Entry.Name);
        var defaultAppId = string.IsNullOrEmpty(extension) ? null : _defaultApps.Resolve(extension);
        var opener = defaultAppId is not null && _applications.SupportsFile(new AppId(defaultAppId), item.Entry.Path)
            ? new AppId(defaultAppId)
            : _applications.FileOpenersForPath(item.Entry.Path).FirstOrDefault()?.Id;
        RecordDesktopFileMenuDiagnostic(
            $"file open requested: entry={item.DisplayName}, extension={extension}, default={defaultAppId ?? "<none>"}, selected={opener?.Value ?? "<none>"}.");
        if (opener is null)
        {
            RecordDesktopFileMenuDiagnostic("file open stopped: no compatible file opener was registered.");
            return;
        }

        var result = _applications.Activate(new AppActivationRequest(RemoteOsActivationUris.OpenFile(opener.Value, item.Entry.Path)));
        RecordDesktopFileMenuDiagnostic($"file open result: entry={item.DisplayName}, result={result.Status}, target={result.TargetAppId?.Value ?? "<none>"}.");
    }

    [RelayCommand]
    private async Task OpenDesktopEntryWithAsync(DesktopFileEntryViewModel? item)
    {
        if (item is null)
        {
            RecordDesktopFileMenuDiagnostic("open-with command ignored: entry was null.");
            return;
        }
        if (item.IsDirectory)
        {
            OpenDesktopEntry(item);
            return;
        }

        var openers = _applications.FileOpenersForPath(item.Entry.Path);
        RecordDesktopFileMenuDiagnostic($"open-with requested: entry={item.DisplayName}, candidates={openers.Count}, dialogAvailable={RequestDesktopOpenWithAsync is not null}.");
        if (openers.Count == 0 || RequestDesktopOpenWithAsync is null)
        {
            RecordDesktopFileMenuDiagnostic("open-with stopped: no compatible opener or no dialog callback.");
            return;
        }

        var choice = await RequestDesktopOpenWithAsync(openers, Path.GetExtension(item.Entry.Name));
        if (choice is null)
        {
            RecordDesktopFileMenuDiagnostic("open-with cancelled.");
            return;
        }

        var selectedApplication = choice.ApplicationId;
        var extension = Path.GetExtension(item.Entry.Name);
        if (choice.SetAsDefault && !string.IsNullOrWhiteSpace(extension))
            await SaveDesktopDefaultAppAsync(extension, selectedApplication);

        var result = _applications.Activate(new AppActivationRequest(
            RemoteOsActivationUris.OpenFile(new AppId(selectedApplication), item.Entry.Path)));
        RecordDesktopFileMenuDiagnostic($"open-with result: entry={item.DisplayName}, app={selectedApplication}, result={result.Status}.");
    }

    [RelayCommand]
    private void CopyDesktopEntry(DesktopFileEntryViewModel? item)
    {
        if (item is null) return;
        _fileClipboard.Set([item.Entry], RemoteFileClipboardOperation.Copy);
        RecordDesktopFileMenuDiagnostic($"copy stored in desktop clipboard: entry={item.DisplayName}.");
    }

    [RelayCommand]
    private void CutDesktopEntry(DesktopFileEntryViewModel? item)
    {
        if (item is null) return;
        _fileClipboard.Set([item.Entry], RemoteFileClipboardOperation.Cut);
        RecordDesktopFileMenuDiagnostic($"cut stored in desktop clipboard: entry={item.DisplayName}.");
    }

    [RelayCommand]
    private Task PasteDesktopAsync() => PasteDesktopEntryAsync(null);

    private async Task PasteDesktopEntryAsync(DesktopFileEntryViewModel? item)
    {
        var targetDirectory = item is { IsDirectory: true } ? item.Entry.Path : _desktopPath;
        if (string.IsNullOrWhiteSpace(targetDirectory) || !_fileClipboard.HasEntries)
        {
            RecordDesktopFileMenuDiagnostic($"desktop paste stopped: targetAvailable={!string.IsNullOrWhiteSpace(targetDirectory)}, clipboardItems={_fileClipboard.Entries.Count}.");
            return;
        }

        RecordDesktopFileMenuDiagnostic($"desktop paste started: clipboardItems={_fileClipboard.Entries.Count}, operation={_fileClipboard.Operation}.");

        try
        {
            foreach (var entry in _fileClipboard.Entries)
            {
                var destination = CombineRemotePath(targetDirectory, entry.Name);
                if (PathEquals(entry.Path, destination))
                {
                    // Copying to the same Desktop should behave like a desktop file manager,
                    // producing a sibling copy instead of silently doing nothing.
                    if (_fileClipboard.Operation == RemoteFileClipboardOperation.Cut)
                        continue;
                    destination = await GetAvailableDesktopCopyPathAsync(targetDirectory, entry.Name);
                }
                if (_fileClipboard.Operation == RemoteFileClipboardOperation.Cut)
                    await _files.MoveAsync(entry.Path, destination, overwrite: false);
                else
                    await _files.CopyAsync(entry.Path, destination, overwrite: false);
            }

            if (_fileClipboard.Operation == RemoteFileClipboardOperation.Cut)
                _fileClipboard.Clear();
            RefreshDesktop();
            RecordDesktopFileMenuDiagnostic("desktop paste completed and refresh requested.");
        }
        catch (Exception ex)
        {
            RecordDesktopFileMenuDiagnostic($"desktop paste failed: {ex.GetType().Name}: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteDesktopEntryAsync(DesktopFileEntryViewModel? item)
    {
        if (item is null || RequestDesktopConfirmAsync is null)
        {
            RecordDesktopFileMenuDiagnostic($"delete stopped: entryAvailable={item is not null}, dialogAvailable={RequestDesktopConfirmAsync is not null}.");
            return;
        }
        var confirmed = await RequestDesktopConfirmAsync(
            T("common.delete", "Delete"),
            LocalizedText.Format("explorer.delete_confirmation", item.Entry.Name),
            T("common.delete", "Delete"));
        if (!confirmed)
        {
            RecordDesktopFileMenuDiagnostic($"delete cancelled: entry={item.DisplayName}.");
            return;
        }

        try
        {
            await _files.DeleteAsync(item.Entry.Path);
            RefreshDesktop();
            RecordDesktopFileMenuDiagnostic($"delete completed: entry={item.DisplayName}; refresh requested.");
        }
        catch (Exception ex)
        {
            RecordDesktopFileMenuDiagnostic($"delete failed: entry={item.DisplayName}, error={ex.GetType().Name}: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ShowDesktopEntryPropertiesAsync(DesktopFileEntryViewModel? item)
    {
        if (item is null || ShowDesktopPropertiesAsync is null)
        {
            RecordDesktopFileMenuDiagnostic($"properties stopped: entryAvailable={item is not null}, dialogAvailable={ShowDesktopPropertiesAsync is not null}.");
            return;
        }
        RecordDesktopFileMenuDiagnostic($"properties requested: entry={item.DisplayName}.");
        try
        {
            var properties = await _files.GetPropertiesAsync(item.Entry.Path);
            if (properties is not null)
            {
                RecordDesktopFileMenuDiagnostic("properties loaded; showing dialog.");
                await ShowDesktopPropertiesAsync(properties);
            }
            else
                RecordDesktopFileMenuDiagnostic("properties stopped: entry no longer exists.");
        }
        catch (Exception ex)
        {
            RecordDesktopFileMenuDiagnostic($"properties failed: entry={item.DisplayName}, error={ex.GetType().Name}: {ex.Message}");
        }
    }

    public void RecordDesktopFileMenuDiagnostic(string message) =>
        _activationDiagnostics.Record($"Desktop file context: {message}");

    /// <summary>Used by the desktop-owned properties window to persist POSIX permission edits.</summary>
    public Task<FilePropertiesDto> SetDesktopUnixPermissionsAsync(string path, int unixMode) =>
        _files.SetUnixPermissionsAsync(path, unixMode);

    [RelayCommand]
    private void ShowDesktopEntryInExplorer(DesktopFileEntryViewModel? item)
    {
        if (item is null) return;
        var path = item.IsDirectory ? item.Entry.Path : Path.GetDirectoryName(item.Entry.Path);
        if (!string.IsNullOrWhiteSpace(path))
            _applications.Activate(new AppActivationRequest(RemoteOsActivationUris.ExplorerPath(path)));
    }

    [RelayCommand]
    private void OpenFileExplorer() => LaunchApplication("remoteos.explorer");

    [RelayCommand]
    private void OpenTerminal() => LaunchApplication("remoteos.terminal");

    [RelayCommand]
    private void OpenSettings() => LaunchApplication("remoteos.settings");

    /// <summary>Opens Settings directly on its Personalization page.</summary>
    [RelayCommand]
    private void OpenPersonalization() =>
        _applications.Activate(new AppActivationRequest(RemoteOsActivationUris.SettingsPersonalization));

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

    private string? _desktopPath;

    private async Task LoadDesktopFilesAsync()
    {
        var generation = ++_desktopFileLoadGeneration;
        if (_session.State != AuthSessionState.Authenticated)
        {
            _desktopPath = null;
            DesktopFiles.Clear();
            RefreshDesktopItems();
            return;
        }

        try
        {
            var locations = await _files.GetSpecialLocationsAsync();
            var desktop = locations.FirstOrDefault(location => location.Kind == SpecialFolderKind.Desktop);
            if (generation != _desktopFileLoadGeneration) return;

            _desktopPath = desktop?.Path;
            if (desktop is null)
            {
                DesktopFiles.Clear();
                RefreshDesktopItems();
                return;
            }

            var directory = await _files.GetDirectoryAsync(desktop.Path);
            if (generation != _desktopFileLoadGeneration) return;

            var entries = directory.Directories
                .Concat(directory.Files.Select(file => new FileSystemEntryDto(
                    file.Path, file.Name, file.Size, FileSystemEntryType.File, file.Created, file.Modified,
                    file.Accessed, file.IsHidden, file.IsSystem)))
                .Where(entry => !entry.IsHidden)
                .OrderByDescending(entry => entry.Type is FileSystemEntryType.Directory or FileSystemEntryType.Drive)
                .ThenBy(entry => entry.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(entry => new DesktopFileEntryViewModel(entry))
                .ToList();

            DesktopFiles.Clear();
            foreach (var entry in entries) DesktopFiles.Add(entry);
            RefreshDesktopItems();
        }
        catch
        {
            // The app launcher remains usable if a server has no Desktop directory or does not
            // permit it. A later refresh retries the request without surfacing a shell-level error.
            if (generation == _desktopFileLoadGeneration)
            {
                _desktopPath = null;
                DesktopFiles.Clear();
                RefreshDesktopItems();
            }
        }
    }

    private void RefreshDesktopItems()
    {
        DesktopItems.Clear();
        foreach (var app in DesktopIcons) DesktopItems.Add(app);
        foreach (var file in DesktopFiles) DesktopItems.Add(file);
    }

    private static string CombineRemotePath(string directory, string name)
    {
        var separator = directory.Contains('\\') ? '\\' : '/';
        var trimmed = directory.TrimEnd('\\', '/');
        return trimmed.Length == 0 ? separator + name : trimmed + separator + name;
    }

    private static bool PathEquals(string first, string second) => string.Equals(
        first.TrimEnd('\\', '/'), second.TrimEnd('\\', '/'),
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private async Task<string> GetAvailableDesktopCopyPathAsync(string directory, string name)
    {
        var baseName = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        for (var index = 1; ; index++)
        {
            var candidateName = $"{baseName} ({index}){extension}";
            var candidatePath = CombineRemotePath(directory, candidateName);
            if (await _files.GetInfoAsync(candidatePath) is null)
                return candidatePath;
        }
    }

    private async Task SaveDesktopDefaultAppAsync(string extension, string applicationId)
    {
        var mappings = _defaultApps.Snapshot
            .Where(mapping => !mapping.Scheme.Equals(extension, StringComparison.OrdinalIgnoreCase))
            .Append(new DefaultAppMappingDto(extension, applicationId))
            .ToArray();
        _defaultApps.SetMappings(mappings);

        if (_session is not { State: AuthSessionState.Authenticated, ServerUrl: { } url, Tokens: { } tokens, CurrentWorkspace: { } workspace })
            return;
        await _settingsClient.SaveAsync(url, tokens.AccessToken, workspace.Id, _settings.ToPreferences(mappings));
    }

    private void RefreshTaskbarGroups()
    {
        var groupedWindows = _windowManager.Windows
            // Modal dialogs belong to their owner. Showing them here lets taskbar activation
            // select the blocked owner, which breaks the modal focus contract.
            .Where(window => !window.IsModalDialog)
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
            var displayName = app is null
                ? windows[0].Title
                : T($"application.{app.Manifest.Id.Value}.display_name", app.Manifest.DisplayName);
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

    private string T(string key, string englishFallback) => _localization.Get(key, englishFallback);
}
