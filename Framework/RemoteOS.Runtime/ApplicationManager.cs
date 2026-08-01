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

    /// <summary>Metadata for every registered application (desktop / start menu).</summary>
    public IReadOnlyList<ApplicationInfo> Registered =>
        _apps.Values.Select(a => a.Manifest.ToInfo()).OrderBy(i => i.DisplayName).ToList();

    /// <summary>Register an application so it can be launched.</summary>
    public void Register(IRemoteApplication application)
    {
        _apps[application.Manifest.Id] = application;
    }

    public bool IsRegistered(AppId id) => _apps.ContainsKey(id);
    public IRemoteApplication? Get(AppId id) => _apps.GetValueOrDefault(id);

    /// <summary>Launch the application with the given id (no-op if not registered).</summary>
    public bool Launch(AppId id)
    {
        if (!_apps.TryGetValue(id, out var app))
            return false;

        var context = new AppContext(id, _windowManager, _services);
        app.Activate(context);
        return true;
    }
}
