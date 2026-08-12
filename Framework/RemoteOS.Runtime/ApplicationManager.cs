using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.WindowManager;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace RemoteOS.Runtime;

/// <summary>
/// The RemoteOS application runtime: maintains the registry of installed applications and
/// launches them on demand, wiring each launch to the shared <see cref="IWindowManager"/>.
/// </summary>
public sealed class ApplicationManager
{
    private readonly Dictionary<AppId, IRemoteApplication> _apps = new();
    private readonly IWindowManager _windowManager;
    private readonly IServiceProvider _services;

    public ApplicationManager(IWindowManager windowManager, IServiceProvider services)
    {
        _windowManager = windowManager;
        _services = services;
    }

    /// <summary>Raised whenever the launchable application registry changes.</summary>
    public event EventHandler? RegistryChanged;

    /// <summary>Metadata for every registered application (desktop / start menu).</summary>
    public IReadOnlyList<ApplicationInfo> Registered =>
        _apps.Values.Select(a => a.Manifest.ToInfo()).OrderBy(i => i.DisplayName).ToList();

    /// <summary>Applications that explicitly declare one or more supported file rules.</summary>
    public IReadOnlyList<ApplicationInfo> FileOpeners =>
        _apps.Values.Where(a => a is IFileOpenApplication && a.Manifest.SupportsFiles).Select(a => a.Manifest.ToInfo())
            .OrderBy(i => i.DisplayName).ToList();

    /// <summary>Applications eligible to open the specified file extension.</summary>
    public IReadOnlyList<ApplicationInfo> FileOpenersForExtension(string extension) =>
        FileOpeners.Where(app => app.FileExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)).ToList();

    /// <summary>Applications eligible to open the supplied path, ordered by rule specificity.</summary>
    public IReadOnlyList<ApplicationInfo> FileOpenersForPath(string path) =>
        _apps.Values
            .Where(app => app is IFileOpenApplication && app.Manifest.SupportsFile(path))
            .OrderByDescending(app => app.Manifest.GetFileMatchPriority(path))
            .ThenBy(app => app.Manifest.DisplayName)
            .Select(app => app.Manifest.ToInfo())
            .ToList();

    /// <summary>Whether the registered application explicitly accepts the supplied file path.</summary>
    public bool SupportsFile(AppId id, string path) =>
        _apps.TryGetValue(id, out var app)
        && app is IFileOpenApplication
        && app.Manifest.SupportsFile(path);

    /// <summary>Register an application so it can be launched.</summary>
    public void Register(IRemoteApplication application)
    {
        _apps[application.Manifest.Id] = application;
        RegistryChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Removes a dynamically loaded application from the launch registry.</summary>
    public bool Unregister(AppId id)
    {
        var removed = _apps.Remove(id);
        if (removed)
            RegistryChanged?.Invoke(this, EventArgs.Empty);
        return removed;
    }

    public bool IsRegistered(AppId id) => _apps.ContainsKey(id);
    public IRemoteApplication? Get(AppId id) => _apps.GetValueOrDefault(id);
    public ApplicationManifest? GetManifest(AppId id) => _apps.GetValueOrDefault(id)?.Manifest;

    /// <summary>Returns the current host compatibility without showing an unavailable-app window.</summary>
    public ApplicationCompatibilityResult EvaluateCompatibility(ApplicationManifest manifest) =>
        (_services.GetService(typeof(IApplicationCompatibilityEvaluator)) as IApplicationCompatibilityEvaluator)
            ?.Evaluate(manifest) ?? ApplicationCompatibilityResult.Compatible;

    /// <summary>Launch the application with the given id (no-op if not registered).</summary>
    public bool Launch(AppId id)
    {
        if (!_apps.TryGetValue(id, out var app))
            return false;

        var context = new AppContext(id, _windowManager, _services);
        if (!EnsureCompatible(app.Manifest))
            return false;
        app.Activate(context);
        return true;
    }

    /// <summary>Open a file in a registered file-opening application.</summary>
    public bool OpenFile(AppId id, string path)
    {
        if (!_apps.TryGetValue(id, out var app) || app is not IFileOpenApplication fileOpener
            || !app.Manifest.SupportsFile(path))
            return false;

        if (!EnsureCompatible(app.Manifest))
            return false;
        fileOpener.OpenFile(new AppContext(id, _windowManager, _services), path);
        return true;
    }

    /// <summary>
    /// Opens a new terminal in <paramref name="workingDirectory"/> using a registered terminal
    /// application. This lets applications request a terminal without taking a dependency on a
    /// concrete terminal implementation.
    /// </summary>
    public bool OpenTerminal(string workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return false;

        var terminal = _apps.Values
            .Where(app => app is IOpenTerminalApplication)
            .OrderBy(app => app.Manifest.DisplayName)
            .FirstOrDefault();
        if (terminal is not IOpenTerminalApplication terminalOpener || !EnsureCompatible(terminal.Manifest))
            return false;

        terminalOpener.OpenTerminal(new AppContext(terminal.Manifest.Id, _windowManager, _services), workingDirectory);
        return true;
    }

    private bool EnsureCompatible(ApplicationManifest manifest)
    {
        var result = EvaluateCompatibility(manifest);
        if (result.IsCompatible)
            return true;

        (_services.GetService(typeof(IApplicationCompatibilityNotifier)) as IApplicationCompatibilityNotifier)
            ?.Notify(manifest, result);
        return false;
    }
}
