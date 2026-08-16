using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.WindowManager;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace RemoteOS.Runtime;

/// <summary>
/// The RemoteOS application runtime: maintains the registry of installed applications and
/// launches them on demand, wiring each launch to the shared <see cref="IWindowManager"/>.
/// </summary>
public sealed class ApplicationManager : IAppActivationService
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

        return ActivateApplication(app, null);
    }

    /// <summary>Open a file in a registered file-opening application.</summary>
    public bool OpenFile(AppId id, string path)
    {
        if (!_apps.TryGetValue(id, out var app) || app is not IFileOpenApplication fileOpener
            || !app.Manifest.SupportsFile(path))
            return false;

        if (!EnsureCompatible(app.Manifest))
            return false;
        var existing = FindExistingPrimaryWindow(id);
        if (existing is not null && app.Manifest.InstancePolicy == ApplicationInstancePolicy.SingleWindow)
        {
            // A future single-window file application may additionally implement an activation
            // handler and route its file reference into a tab. Until then, preserving the
            // single-window guarantee is safer than silently creating another instance.
            _windowManager.Restore(existing);
            _windowManager.Focus(existing);
            RequestUndecidedPermissions(app.Manifest.Id);
            return true;
        }
        fileOpener.OpenFile(new AppContext(id, _windowManager, _services), path);
        RequestUndecidedPermissions(app.Manifest.Id);
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
        RequestUndecidedPermissions(terminal.Manifest.Id);
        return true;
    }

    /// <summary>
    /// Resolves a Shell-owned <c>remoteos://</c> URI or a manifest-declared external URI scheme.
    /// </summary>
    public AppActivationResult Activate(AppActivationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Uri);
        var uri = request.Uri;
        if (!uri.IsAbsoluteUri || string.IsNullOrWhiteSpace(uri.Scheme) || string.IsNullOrEmpty(uri.UserInfo) || uri.Port != -1)
            return new AppActivationResult(AppActivationStatus.InvalidUri);

        if (!uri.Scheme.Equals("remoteos", StringComparison.OrdinalIgnoreCase))
            return ActivateExternalUri(request);

        if (string.IsNullOrWhiteSpace(uri.Host))
            return new AppActivationResult(AppActivationStatus.InvalidUri);

        if (uri.Host.Equals("file", StringComparison.OrdinalIgnoreCase)
            && uri.AbsolutePath.Equals("/open", StringComparison.OrdinalIgnoreCase))
            return ActivateFileOpen(request);

        var matches = _apps.Values
            .Where(app => app is IAppActivationHandler handler && handler.CanHandleActivation(uri))
            .ToArray();
        if (matches.Length != 1)
            return new AppActivationResult(AppActivationStatus.RouteNotFound);

        var application = matches[0];
        return ActivateApplication(application, request)
            ? new AppActivationResult(AppActivationStatus.Activated, application.Manifest.Id)
            : new AppActivationResult(AppActivationStatus.Unavailable, application.Manifest.Id);
    }

    private AppActivationResult ActivateExternalUri(AppActivationRequest request)
    {
        var uri = request.Uri;
        if (uri.Scheme.Length > 32 || string.IsNullOrWhiteSpace(uri.Host) || uri.Query.Length > 4097)
            return new AppActivationResult(AppActivationStatus.InvalidUri);

        var matches = _apps.Values
            .Where(app => app.Manifest.UriSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
            .Where(app => app is IAppActivationHandler handler && handler.CanHandleActivation(uri))
            .ToArray();

        var preferredId = (_services.GetService(typeof(IUriSchemeDefaultResolver)) as IUriSchemeDefaultResolver)
            ?.ResolveDefaultApplication(uri.Scheme);
        var application = preferredId is { } preferred
            ? matches.SingleOrDefault(app => app.Manifest.Id == preferred)
            : null;
        if (application is not null)
            return ActivateApplication(application, request)
                ? new AppActivationResult(AppActivationStatus.Activated, application.Manifest.Id)
                : new AppActivationResult(AppActivationStatus.Unavailable, application.Manifest.Id);

        if (matches.Length == 1)
        {
            application = matches[0];
            return ActivateApplication(application, request)
                ? new AppActivationResult(AppActivationStatus.Activated, application.Manifest.Id)
                : new AppActivationResult(AppActivationStatus.Unavailable, application.Manifest.Id);
        }

        var routingUi = _services.GetService(typeof(IUriSchemeRoutingUi)) as IUriSchemeRoutingUi;
        if (matches.Length == 0)
        {
            if (request.UserInitiated && routingUi is not null)
                _ = NotifyNoHandlerAsync(routingUi, uri);
            return new AppActivationResult(AppActivationStatus.NoHandler);
        }

        if (request.UserInitiated && routingUi is not null)
        {
            _ = ChooseExternalHandlerAsync(routingUi, request, matches);
            return new AppActivationResult(AppActivationStatus.HandlerSelectionRequired);
        }

        return new AppActivationResult(AppActivationStatus.RouteNotFound);
    }

    private async Task ChooseExternalHandlerAsync(IUriSchemeRoutingUi routingUi, AppActivationRequest request,
        IReadOnlyList<IRemoteApplication> candidates)
    {
        try
        {
            var choice = await routingUi.ChooseHandlerAsync(request.Uri,
                candidates.Select(candidate => candidate.Manifest.ToInfo()).ToArray());
            if (choice is null)
                return;

            var application = candidates.SingleOrDefault(candidate => candidate.Manifest.Id == choice.ApplicationId);
            if (application is null)
                return; // The package registry changed while the dialog was open.

            if (choice.SetAsDefault)
            {
                try { await routingUi.SaveDefaultHandlerAsync(request.Uri.Scheme, application.Manifest.Id); }
                catch { /* Persisting a preference must not prevent the selected app from opening. */ }
            }

            ActivateApplication(application, request);
        }
        catch
        {
            // A routing prompt must never crash the Shell or the source application.
        }
    }

    private static async Task NotifyNoHandlerAsync(IUriSchemeRoutingUi routingUi, Uri uri)
    {
        try { await routingUi.NotifyNoHandlerAsync(uri); }
        catch { /* A missing routing UI must not alter the stable activation result. */ }
    }

    private AppActivationResult ActivateFileOpen(AppActivationRequest request)
    {
        // Host paths are intentionally not an inter-package protocol. The Explorer is the only
        // current first-party caller; package applications must use their file capability APIs.
        if (request.SourceAppId is not { Value: "remoteos.explorer" })
            return new AppActivationResult(AppActivationStatus.Unavailable);

        var values = ParseQuery(request.Uri);
        if (!values.TryGetValue("appId", out var appId) || !values.TryGetValue("path", out var path)
            || string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(path))
            return new AppActivationResult(AppActivationStatus.InvalidUri);

        var target = new AppId(appId);
        return OpenFile(target, path)
            ? new AppActivationResult(AppActivationStatus.Activated, target)
            : new AppActivationResult(AppActivationStatus.Unavailable, target);
    }

    private static IReadOnlyDictionary<string, string> ParseQuery(Uri uri)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = pair.IndexOf('=');
            if (split < 1) continue;
            var key = Uri.UnescapeDataString(pair[..split]);
            var value = Uri.UnescapeDataString(pair[(split + 1)..]);
            if (key.Length <= 32 && value.Length <= 4096)
                values[key] = value;
        }
        return values;
    }

    private bool ActivateApplication(IRemoteApplication app, AppActivationRequest? request)
    {
        if (!EnsureCompatible(app.Manifest))
            return false;

        var context = new AppContext(app.Manifest.Id, _windowManager, _services);
        var existing = FindExistingPrimaryWindow(app.Manifest.Id);
        if (existing is not null && app.Manifest.InstancePolicy == ApplicationInstancePolicy.SingleWindow)
        {
            if (request is not null && app is IAppActivationHandler handler)
                handler.HandleActivation(context, request, existing);
            _windowManager.Restore(existing);
            _windowManager.Focus(existing);
            RequestUndecidedPermissions(app.Manifest.Id);
            return true;
        }

        app.Activate(context);
        RequestUndecidedPermissions(app.Manifest.Id);
        if (request is not null && app is IAppActivationHandler activationHandler)
            activationHandler.HandleActivation(context, request, FindExistingPrimaryWindow(app.Manifest.Id));
        return true;
    }

    private void RequestUndecidedPermissions(AppId appId)
    {
        var service = _services.GetService(typeof(IAppPermissionRequestService)) as IAppPermissionRequestService;
        if (service is null)
            return;

        // Permission decisions are intentionally not part of application activation: the app
        // is already open and remains usable if every request is rejected or deferred.
        _ = RequestUndecidedPermissionsAsync(service, appId);
    }

    private static async Task RequestUndecidedPermissionsAsync(IAppPermissionRequestService service, AppId appId)
    {
        try { await service.RequestUndecidedAsync(appId); }
        catch { /* A prompt failure must never turn a successful launch into a failed launch. */ }
    }

    private ManagedWindow? FindExistingPrimaryWindow(AppId appId) => _windowManager.Windows
        .LastOrDefault(window => window.Info.OwnerAppId == appId && !window.IsModalDialog);

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
