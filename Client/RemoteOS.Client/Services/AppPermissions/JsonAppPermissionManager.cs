using System.Text.Json;
using RemoteOS.Core.Applications;
using RemoteOS.Runtime;
using CoreAppPermissions = RemoteOS.Core.Applications.AppPermissions;

namespace Client.Services.AppPermissions;

/// <summary>
/// Per-user, local permission grants. Grants are intentionally separate from application packages:
/// updating or reinstalling an application never silently adds a new permission.
/// </summary>
public sealed class JsonAppPermissionManager : IAppPermissionManager
{
    private readonly ApplicationManager _applications;
    private readonly string _path;
    private readonly object _gate = new();
    private readonly Dictionary<string, HashSet<string>> _grants;

    public JsonAppPermissionManager(ApplicationManager applications)
    {
        _applications = applications;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteOS");
        _path = Path.Combine(root, "app-permissions.json");
        _grants = Load(_path);
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

            Save(_path, _grants);
        }
    }

    private static Dictionary<string, HashSet<string>> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            var values = JsonSerializer.Deserialize<Dictionary<string, string[]>>(File.ReadAllText(path));
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
        catch (IOException)
        {
            return new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        }
    }

    private static void Save(string path, Dictionary<string, HashSet<string>> grants)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var snapshot = grants.ToDictionary(pair => pair.Key, pair => pair.Value.Order().ToArray(), StringComparer.Ordinal);
        var temporaryPath = path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporaryPath, path, overwrite: true);
    }
}
