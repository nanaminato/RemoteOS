using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using RemoteOS.Core.Applications;
using RemoteOS.Runtime;
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
    private readonly Dictionary<string, HashSet<string>> _grants;

    public JsonAppPermissionManager(ApplicationManager applications)
    {
        _applications = applications;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteOS");
        _useEncryption = OperatingSystem.IsWindows();
        _path = Path.Combine(root, _useEncryption ? "app-permissions.dat" : "app-permissions.json");
        _legacyPath = _useEncryption ? Path.Combine(root, "app-permissions.json") : null;
        _grants = Load(_path, _legacyPath, _useEncryption);

        if (_useEncryption && !File.Exists(_path) && _legacyPath is not null && File.Exists(_legacyPath))
            MigrateLegacyFile(_legacyPath);
    }

    public bool IsGranted(AppId appId, string permissionId)
    {
        if (!CoreAppPermissions.IsKnown(permissionId))
            return false;

        lock (_gate)
            return _grants.TryGetValue(appId.Value, out var appGrants) && appGrants.Contains(permissionId);
    }

    public void SetGranted(AppId appId, string permissionId, bool granted)
    {
        if (!CoreAppPermissions.IsKnown(permissionId))
            throw new ArgumentOutOfRangeException(nameof(permissionId), "Unknown RemoteOS application permission.");

        var manifest = _applications.GetManifest(appId);
        if (manifest is null || !manifest.Permissions.Contains(permissionId, StringComparer.Ordinal))
            throw new InvalidOperationException($"Application '{appId}' did not request '{permissionId}'.");

        lock (_gate)
        {
            if (!_grants.TryGetValue(appId.Value, out var appGrants))
            {
                appGrants = new HashSet<string>(StringComparer.Ordinal);
                _grants[appId.Value] = appGrants;
            }

            if (granted)
                appGrants.Add(permissionId);
            else
                appGrants.Remove(permissionId);

            if (appGrants.Count == 0)
                _grants.Remove(appId.Value);

            Save(_path, _grants, _useEncryption);
        }
    }

    private static Dictionary<string, HashSet<string>> Load(string path, string? legacyPath, bool encrypted)
    {
        var json = Read(path, encrypted) ?? (legacyPath is null ? null : Read(legacyPath, encrypted: false));
        if (json is null)
            return new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string, string[]>>(json);
            return values?.ToDictionary(
                pair => pair.Key,
                pair => new HashSet<string>(pair.Value.Where(CoreAppPermissions.IsKnown), StringComparer.Ordinal),
                StringComparer.Ordinal)
                ?? new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        }
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

    private static void Save(string path, Dictionary<string, HashSet<string>> grants, bool encrypted)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var snapshot = grants.ToDictionary(pair => pair.Key, pair => pair.Value.Order().ToArray(), StringComparer.Ordinal);
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
