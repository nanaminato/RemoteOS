using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using RemoteOS.Core.Applications;
using RemoteOS.Runtime;
using RemoteOS.AppSDK;
using CoreAppPermissions = RemoteOS.Core.Applications.AppPermissions;

namespace Client.Services.AppPermissions;

/// <summary>
/// Per-user, local permission grants. On Windows the grant file is protected with DPAPI for the
/// current OS user; other platforms retain the JSON format as a compatibility fallback.
/// Grants remain separate from application packages, so an update can never silently add a grant.
/// </summary>
public sealed class JsonAppPermissionManager : IAppPermissionManager
{
    private readonly ApplicationManager _applications;
    private readonly string _path;
    private readonly string? _legacyPath;
    private readonly bool _useEncryption;
    private readonly object _gate = new();
    private readonly Dictionary<string, Dictionary<string, AppPermissionStatus>> _decisions;


    public JsonAppPermissionManager(ApplicationManager applications)
    {
        _applications = applications;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteOS");
        _useEncryption = OperatingSystem.IsWindows();
        _path = Path.Combine(root, _useEncryption ? "app-permissions.dat" : "app-permissions.json");
        _legacyPath = _useEncryption ? Path.Combine(root, "app-permissions.json") : null;
        _decisions = Load(_path, _legacyPath, _useEncryption);

        if (_useEncryption && !File.Exists(_path) && _legacyPath is not null && File.Exists(_legacyPath))
            MigrateLegacyFile(_legacyPath);
    }

    public bool IsGranted(AppId appId, string permissionId)
        => GetStatus(appId, permissionId) == AppPermissionStatus.Granted;

    public AppPermissionStatus GetStatus(AppId appId, string permissionId)
    {
        if (!CoreAppPermissions.IsKnown(permissionId))
            return AppPermissionStatus.Undecided;

        lock (_gate)
            return _decisions.TryGetValue(appId.Value, out var appDecisions)
                   && appDecisions.TryGetValue(permissionId, out var status)
                ? status
                : AppPermissionStatus.Undecided;
    }

    public void SetGranted(AppId appId, string permissionId, bool granted)
        => SetStatus(appId, permissionId, granted ? AppPermissionStatus.Granted : AppPermissionStatus.Denied);

    public void SetStatus(AppId appId, string permissionId, AppPermissionStatus status)
    {
        if (!CoreAppPermissions.IsKnown(permissionId))
            throw new ArgumentOutOfRangeException(nameof(permissionId), "Unknown RemoteOS application permission.");

        var manifest = _applications.GetManifest(appId);
        if (manifest is null || !manifest.Permissions.Contains(permissionId, StringComparer.Ordinal))
            throw new InvalidOperationException($"Application '{appId}' did not request '{permissionId}'.");

        lock (_gate)
        {
            if (!_decisions.TryGetValue(appId.Value, out var appDecisions))
            {
                appDecisions = new Dictionary<string, AppPermissionStatus>(StringComparer.Ordinal);
                _decisions[appId.Value] = appDecisions;
            }

            if (status == AppPermissionStatus.Undecided)
                appDecisions.Remove(permissionId);
            else
                appDecisions[permissionId] = status;

            if (appDecisions.Count == 0)
                _decisions.Remove(appId.Value);

            Save(_path, _decisions, _useEncryption);
        }
    }

    public void Clear(AppId appId)
    {
        lock (_gate)
        {
            if (!_decisions.Remove(appId.Value))
                return;
            Save(_path, _decisions, _useEncryption);
        }
    }

    private static Dictionary<string, Dictionary<string, AppPermissionStatus>> Load(string path, string? legacyPath, bool encrypted)
    {
        var json = Read(path, encrypted) ?? (legacyPath is null ? null : Read(legacyPath, encrypted: false));
        if (json is null)
            return new Dictionary<string, Dictionary<string, AppPermissionStatus>>(StringComparer.Ordinal);

        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, AppPermissionStatus>>>(json);
            if (values is not null)
                return values.ToDictionary(
                    app => app.Key,
                    app => app.Value
                        .Where(permission => CoreAppPermissions.IsKnown(permission.Key)
                                             && permission.Value != AppPermissionStatus.Undecided)
                        .ToDictionary(permission => permission.Key, permission => permission.Value, StringComparer.Ordinal),
                    StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            // Versions before permission denials were persisted used an array of granted ids.
        }

        try
        {
            var legacy = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json);
            return legacy?.ToDictionary(
                app => app.Key,
                app => app.Value.Where(CoreAppPermissions.IsKnown)
                    .ToDictionary(permission => permission, _ => AppPermissionStatus.Granted, StringComparer.Ordinal),
                StringComparer.Ordinal)
                ?? new Dictionary<string, Dictionary<string, AppPermissionStatus>>(StringComparer.Ordinal);
        }
        catch (JsonException) { return new Dictionary<string, Dictionary<string, AppPermissionStatus>>(StringComparer.Ordinal); }
    }

    private static string? Read(string path, bool encrypted)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            var bytes = File.ReadAllBytes(path);
            if (encrypted && OperatingSystem.IsWindows())
                bytes = UnprotectForCurrentUser(bytes);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (CryptographicException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static void Save(string path, Dictionary<string, Dictionary<string, AppPermissionStatus>> decisions, bool encrypted)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var snapshot = decisions.ToDictionary(
            app => app.Key,
            app => app.Value.OrderBy(permission => permission.Key, StringComparer.Ordinal)
                .ToDictionary(permission => permission.Key, permission => permission.Value, StringComparer.Ordinal),
            StringComparer.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        if (encrypted && OperatingSystem.IsWindows())
            bytes = ProtectForCurrentUser(bytes);

        var temporaryPath = path + ".tmp";
        File.WriteAllBytes(temporaryPath, bytes);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private void MigrateLegacyFile(string legacyPath)
    {
        try
        {
            Save(_path, _grants, encrypted: true);
            File.Delete(legacyPath);
        }
        catch (CryptographicException) { }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] ProtectForCurrentUser(byte[] bytes) =>
        ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static byte[] UnprotectForCurrentUser(byte[] bytes) =>
        ProtectedData.Unprotect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser);
}
