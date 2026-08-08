using System.IO.Compression;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Threading;
using Client.Services.AppPermissions;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Runtime;
using RemoteOS.WindowManager;
using AppContext = RemoteOS.AppSDK.AppContext;

namespace Client.Services.Developer;

/// <summary>
/// Installs development <c>.roapp</c> archives and loads them into their own assembly load context.
/// A development package may never use the reserved <c>remoteos.*</c> identifier range.
/// </summary>
public sealed class DeveloperPackageManager
{
    private readonly ApplicationManager _applications;
    private readonly ExternalAppContextFactory _contextFactory;
    private readonly IWindowManager _windowManager;
    private readonly string _root;
    private readonly string _catalogPath;
    private readonly Dictionary<string, DeveloperAppRecord> _catalog;
    private readonly Dictionary<string, LoadedDeveloperApp> _loaded = new(StringComparer.Ordinal);

    public DeveloperPackageManager(
        ApplicationManager applications,
        ExternalAppContextFactory contextFactory,
        IWindowManager windowManager)
    {
        _applications = applications;
        _contextFactory = contextFactory;
        _windowManager = windowManager;
        _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteOS", "developer-apps");
        _catalogPath = Path.Combine(_root, "catalog.json");
        _catalog = LoadCatalog(_catalogPath);
    }

    public IReadOnlyList<DeveloperAppInfo> Installed => _catalog.Values
        .OrderBy(record => record.DisplayName)
        .Select(record => new DeveloperAppInfo(record.Id, record.DisplayName, record.Version, record.Path))
        .ToArray();

    /// <summary>Loads installed packages at client startup. Invalid old packages are ignored instead of breaking the shell.</summary>
    public void LoadInstalled()
    {
        foreach (var record in _catalog.Values.ToArray())
        {
            try { ReplaceLoaded(CreateLoaded(record), launch: false); }
            catch { /* A developer can replace the broken package through the Dev Bridge. */ }
        }
    }

    public async Task<DeveloperAppInfo> InstallAsync(Stream package, bool launch, CancellationToken cancellationToken = default)
    {
        var staging = Path.Combine(_root, ".staging", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            var manifest = await ExtractAndReadManifestAsync(package, staging, cancellationToken);
            ValidateManifest(manifest);

            var appId = manifest.Id.Trim();
            var version = manifest.Version.Trim();
            // Keep every deployment in a distinct folder. A currently loaded DLL can be locked on
            // Windows, so overwriting a version directory would make the development update flaky.
            var destination = VersionPath(appId);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(staging, destination);
            staging = string.Empty;

            var record = new DeveloperAppRecord(appId, manifest.DisplayName.Trim(), version, destination, manifest.EntryAssembly.Trim(), manifest.EntryType.Trim(),
                manifest.IconGlyph, manifest.Description, manifest.RequestedPermissions ?? Array.Empty<string>(),
                manifest.SupportedFileExtensions ?? Array.Empty<string>());
            var loaded = CreateLoaded(record);
            await Dispatcher.UIThread.InvokeAsync(() => ReplaceLoaded(loaded, launch));

            _catalog[appId] = record;
            SaveCatalog(_catalogPath, _catalog);
            return new DeveloperAppInfo(record.Id, record.DisplayName, record.Version, record.Path);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(staging) && Directory.Exists(staging))
                Directory.Delete(staging, recursive: true);
        }
    }

    public async Task<bool> UninstallAsync(string appId)
    {
        if (!_catalog.Remove(appId))
            return false;

        await Dispatcher.UIThread.InvokeAsync(() => Unload(appId));
        SaveCatalog(_catalogPath, _catalog);
        var appDirectory = AppDirectory(appId);
        if (Directory.Exists(appDirectory))
            Directory.Delete(appDirectory, recursive: true);
        return true;
    }

    public Task<bool> LaunchAsync(string appId) => Dispatcher.UIThread.InvokeAsync(() => _applications.Launch(new AppId(appId))).GetTask();

    private LoadedDeveloperApp CreateLoaded(DeveloperAppRecord record)
    {
        var assemblyPath = ResolvePackagePath(record.Path, record.EntryAssembly);
        if (!File.Exists(assemblyPath))
            throw new FileNotFoundException("The package entry assembly was not found.", assemblyPath);

        var context = new DeveloperAssemblyLoadContext(assemblyPath);
        try
        {
            var assembly = context.LoadFromAssemblyPath(assemblyPath);
            var type = assembly.GetType(record.EntryType, throwOnError: false)
                ?? throw new InvalidOperationException($"Package entry type '{record.EntryType}' was not found.");
            if (Activator.CreateInstance(type) is not IExternalRemoteApplication application)
                throw new InvalidOperationException("The package entry type must implement IExternalRemoteApplication.");

            var manifest = new ApplicationManifest(new AppId(record.Id), record.DisplayName, record.Version, record.IconGlyph, record.Description,
                record.RequestedPermissions, record.SupportedFileExtensions);
            return new LoadedDeveloperApp(context, application, manifest);
        }
        catch
        {
            context.Unload();
            throw;
        }
    }

    private void ReplaceLoaded(LoadedDeveloperApp replacement, bool launch)
    {
        Unload(replacement.Manifest.Id.Value);
        _loaded[replacement.Manifest.Id.Value] = replacement;
        _applications.Register(replacement.Application is IExternalFileOpenApplication && replacement.Manifest.FileExtensions.Count > 0
            ? new ExternalFileApplicationAdapter(replacement, _contextFactory)
            : new ExternalApplicationAdapter(replacement, _contextFactory));
        if (launch)
            _applications.Launch(replacement.Manifest.Id);
    }

    private void Unload(string appId)
    {
        var id = new AppId(appId);
        foreach (var window in _windowManager.Windows.Where(window => window.Info.OwnerAppId == id).ToArray())
            _windowManager.Close(window);

        _applications.Unregister(id);
        if (_loaded.Remove(appId, out var loaded))
        {
            loaded.LoadContext.Unload();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private async Task<DeveloperPackageManifest> ExtractAndReadManifestAsync(Stream package, string destination, CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
        var manifestEntry = archive.GetEntry("manifest.json")
            ?? throw new InvalidOperationException("A development package must contain manifest.json at its root.");
        DeveloperPackageManifest manifest;
        await using (var manifestStream = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<DeveloperPackageManifest>(manifestStream,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, cancellationToken)
                ?? throw new InvalidOperationException("manifest.json is invalid.");
        }

        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name))
                continue;
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The package contains an invalid path.");
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await using var source = entry.Open();
            await using var output = File.Create(target);
            await source.CopyToAsync(output, cancellationToken);
        }

        return manifest;
    }

    private static void ValidateManifest(DeveloperPackageManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Id) || !System.Text.RegularExpressions.Regex.IsMatch(manifest.Id, "^[a-z0-9][a-z0-9.-]{2,127}$"))
            throw new InvalidOperationException("Application id must use lowercase letters, digits, dots, or hyphens.");
        if (manifest.Id.StartsWith("remoteos.", StringComparison.Ordinal))
            throw new InvalidOperationException("The remoteos.* application id range is reserved for built-in applications.");
        if (string.IsNullOrWhiteSpace(manifest.DisplayName) || string.IsNullOrWhiteSpace(manifest.Version)
            || string.IsNullOrWhiteSpace(manifest.EntryAssembly) || string.IsNullOrWhiteSpace(manifest.EntryType))
            throw new InvalidOperationException("manifest.json is missing a required field.");
        if (!manifest.EntryAssembly.Replace('\\', '/').StartsWith("lib/", StringComparison.Ordinal))
            throw new InvalidOperationException("entryAssembly must point to a DLL under lib/.");
    }

    private string AppDirectory(string appId)
    {
        ValidateAppId(appId);
        return Path.Combine(_root, appId);
    }

    private string VersionPath(string appId) => Path.Combine(AppDirectory(appId), "versions", Guid.NewGuid().ToString("N"));

    private static string ResolvePackagePath(string packageRoot, string relativePath)
    {
        var root = Path.GetFullPath(packageRoot) + Path.DirectorySeparatorChar;
        var resolved = Path.GetFullPath(Path.Combine(packageRoot, relativePath));
        if (!resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Package path is outside its installation directory.");
        return resolved;
    }

    private static void ValidateAppId(string appId)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(appId, "^[a-z0-9][a-z0-9.-]{2,127}$") || appId.StartsWith("remoteos.", StringComparison.Ordinal))
            throw new InvalidOperationException("Invalid developer application id.");
    }

    private static Dictionary<string, DeveloperAppRecord> LoadCatalog(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<Dictionary<string, DeveloperAppRecord>>(File.ReadAllText(path))
                    ?? new Dictionary<string, DeveloperAppRecord>(StringComparer.Ordinal);
        }
        catch (JsonException) { }
        catch (IOException) { }
        return new Dictionary<string, DeveloperAppRecord>(StringComparer.Ordinal);
    }

    private static void SaveCatalog(string path, Dictionary<string, DeveloperAppRecord> catalog)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private sealed class DeveloperAssemblyLoadContext(string mainAssemblyPath) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(mainAssemblyPath);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name?.StartsWith("RemoteOS.", StringComparison.Ordinal) == true
                || assemblyName.Name?.StartsWith("Avalonia", StringComparison.Ordinal) == true)
                return null;
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }
    }

    private sealed record LoadedDeveloperApp(
        DeveloperAssemblyLoadContext LoadContext,
        IExternalRemoteApplication Application,
        ApplicationManifest Manifest);

    private class ExternalApplicationAdapter : RemoteApplicationBase
    {
        protected readonly LoadedDeveloperApp Loaded;
        protected readonly ExternalAppContextFactory ContextFactory;

        public ExternalApplicationAdapter(LoadedDeveloperApp loaded, ExternalAppContextFactory contextFactory)
        {
            Loaded = loaded;
            ContextFactory = contextFactory;
        }

        public override ApplicationManifest Manifest => Loaded.Manifest;

        public override void Activate(AppContext context) => _ = ActivateAsync(context);

        protected async Task ActivateAsync(AppContext context)
        {
            try
            {
                await Loaded.Application.ActivateAsync(ContextFactory.Create(Manifest.Id));
            }
            catch (Exception exception)
            {
                ShowFailure(context, exception);
            }
        }

        protected void ShowFailure(AppContext context, Exception exception) =>
            context.ShowWindow($"{Manifest.DisplayName} failed to start",
                new TextBlock { Text = exception.Message, Margin = new Avalonia.Thickness(20), TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                iconGlyph: Manifest.IconGlyph, canResize: true);
    }

    private sealed class ExternalFileApplicationAdapter(LoadedDeveloperApp loaded, ExternalAppContextFactory contextFactory)
        : ExternalApplicationAdapter(loaded, contextFactory), IFileOpenApplication
    {
        public void OpenFile(AppContext context, string path) => _ = OpenFileAsync(context, path);

        private async Task OpenFileAsync(AppContext context, string path)
        {
            try
            {
                await ((IExternalFileOpenApplication)Loaded.Application)
                    .OpenFileAsync(ContextFactory.Create(Manifest.Id), path);
            }
            catch (Exception exception)
            {
                ShowFailure(context, exception);
            }
        }
    }
}

/// <summary>Development package manifest stored as <c>manifest.json</c> inside a <c>.roapp</c> archive.</summary>
public sealed record DeveloperPackageManifest(
    string Id,
    string DisplayName,
    string Version,
    string EntryAssembly,
    string EntryType,
    string? IconGlyph = null,
    string? Description = null,
    IReadOnlyList<string>? RequestedPermissions = null,
    IReadOnlyList<string>? SupportedFileExtensions = null);

internal sealed record DeveloperAppRecord(
    string Id,
    string DisplayName,
    string Version,
    string Path,
    string EntryAssembly,
    string EntryType,
    string? IconGlyph,
    string? Description,
    IReadOnlyList<string> RequestedPermissions,
    IReadOnlyList<string> SupportedFileExtensions);

public sealed record DeveloperAppInfo(string Id, string DisplayName, string Version, string InstallationPath);
