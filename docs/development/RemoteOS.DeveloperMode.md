# RemoteOS Developer Mode

Developer Mode provides a localhost-only bridge for installing and refreshing development applications without publishing them to the application store. It is disabled by default.

## Enable and pair

Open **Settings → Applications → Developer Mode**, enable it, and copy the pairing token. The bridge listens only on `http://127.0.0.1:45321/api/developer/v1/` and requires the token in every `X-RemoteOS-Dev-Token` request header.

Use the included CLI:

```powershell
$env:REMOTEOS_DEV_TOKEN = "<token from Settings>"
dotnet run --project Tools/RemoteOS.DevCli -- install .\bin\Debug\my-app.roapp
dotnet run --project Tools/RemoteOS.DevCli -- watch .\bin\Debug\my-app.roapp
```

`watch` reinstalls and relaunches the package when the archive changes. Updating an app closes its windows, unloads its collectible assembly load context, registers the new version, and launches it again.

## Development package format

A `.roapp` file is a ZIP archive with this minimum structure:

```text
manifest.json
lib/net10.0/MyApp.dll
lib/net10.0/<private dependencies>.dll
```

`manifest.json`:

```json
{
  "id": "com.example.hello",
  "displayName": "Hello Developer",
  "version": "0.1.0-dev",
  "entryAssembly": "lib/net10.0/MyApp.dll",
  "entryType": "Example.HelloApp",
  "iconGlyph": "🧪",
  "description": "A development package",
  "requestedPermissions": ["desktop.wallpaper.write"],
  "supportedFileExtensions": [".hello"],
  "supportedFileNames": [".hellorc"],
  "supportsExtensionlessFiles": false,
  "clientPlatforms": ["windows", "linux"],
  "serverRequirements": {
    "platforms": ["windows", "linux"],
    "capabilities": ["server.files"]
  }
}
```

`clientPlatforms` and `serverRequirements` are optional. An omitted or empty list means that the
package places no restriction on that dimension. The shell evaluates them before loading the
package assembly, both for desktop launches and **Open with** file launches. See
[`RemoteOS.ApplicationCompatibility.md`](./RemoteOS.ApplicationCompatibility.md) for the complete
contract and the server capability catalogue.

The entry type must implement `RemoteOS.AppSDK.IExternalRemoteApplication`. It receives `IExternalAppContext`, which exposes only approved RemoteOS capabilities, including owned-window creation and the permission-gated desktop appearance service.

To appear in RemoteExplorer's **Open with** menu, the entry type must also implement `IExternalFileOpenApplication` and declare at least one accepted path rule. `supportedFileExtensions` is case-insensitive and each extension must begin with a dot. `supportedFileNames` accepts exact file names such as `.gitignore`; these take precedence over extension matches. `supportsExtensionlessFiles` enables a low-priority fallback only for files without an extension. Packages that omit all three fields remain launchable, but are never offered a file path.

## Server monitoring capability

`IExternalAppContext.ServerMonitor` provides a stable, read-only aggregate server metrics API. It requires the `server.metrics.read` manifest permission and user approval under **Application permissions**. `GetSnapshotAsync` returns one capability result; `WatchAsync` returns a host-polled sequence (minimum one-second interval) for live dashboards. It deliberately does not expose process enumeration, process termination, raw server credentials, or the task-manager client.

The complete sample is in [`examples/ServerMonitor`](../../examples/ServerMonitor). Build and package it with:

```powershell
.\examples\ServerMonitor\build-package.ps1
$env:REMOTEOS_DEV_TOKEN = "<token from Settings>"
dotnet run --project Tools/RemoteOS.DevCli -- install .\examples\ServerMonitor\bin\Debug\net10.0\RemoteOS.Example.ServerMonitor.roapp
```

After installation, grant **读取服务器性能指标** to **Server Monitor** in Settings, then launch the desktop icon.

## Network Inspector

`Network Inspector` is a RemoteOS system diagnostics window, not a development package. Open it through **Settings → Developer → Network Inspector** or `Ctrl+Shift+I`. Recording is off by default; turning Developer Mode off or signing out immediately stops recording and clears its in-memory log. It displays only redacted RemoteOS REST and SignalR summaries, never tokens, cookies, media bodies, terminal data, or arbitrary device traffic.

## Security model

Developer packages use a reserved external app ID and cannot use the `remoteos.*` built-in namespace. They are installed below the current user's local application data and do not overwrite store packages. Developer Mode does not grant manifest permissions automatically; grant or revoke each requested capability under **Application permissions**.

The bridge has no LAN listener. Do not expose its pairing token. Regenerating the token invalidates existing developer tooling sessions.

## Permission catalogue

Package manifests request stable, host-defined permissions rather than defining their own permission types. Settings groups requested permissions by category and opens a separate editor for each application.

| Category | Permission ids |
| --- | --- |
| Server files | `server.files.read`, `server.files.write` |
| Server monitoring | `server.metrics.read` |
| Server management | `server.processes.manage`, `server.services.manage`, `server.power.manage` |
| Server network | `server.network.read`, `server.network.configure` |
| Desktop and workspace | `desktop.wallpaper.write` |

Management permissions include only the minimal inspection needed to safely perform that action (for example, process management includes listing processes); they do not grant other categories. Declaring a future catalogue permission alone never grants access to host services until a permission-gated SDK capability exposes it.

Grants are local to the desktop client. On Windows they are stored with current-user DPAPI protection; other platforms use the compatible local JSON fallback.

## Clearing application data

**Settings → Applications → [application] → Clear data** always removes the application's
standard local-data directory on the current device. It deliberately does not uninstall the
package. The user may additionally choose either or both of the following:

- **Permission decisions**: removes local `Granted`/`Denied` decisions for that app id. Its
  declared permissions are therefore `Undecided` and will be requested again at a later launch.
- **Server application data**: deletes every `IExternalAppContext.SettingsStore` document for
  that app and current user, across its user, workspace, and device scopes.

The second option is account-wide private configuration deletion, not merely a reset of the
current device. A package must therefore tolerate missing settings documents after any launch.

## Runtime permission approval

When an application opens, the Shell opens one app-owned prompt for every declared permission that
is still undecided. The application is already running while these prompts are shown: choosing
**Deny** does not close it, and choosing **Later** leaves that permission undecided while the
remaining prompts continue. Granted and denied decisions are remembered; only undecided
permissions are prompted automatically on a later launch.

Both built-in `AppContext` and package `IExternalAppContext` expose the same scoped
`Permissions` surface. It can only inspect or request the current application's declared IDs:

```csharp
var status = context.Permissions.GetStatus("server.metrics.read");
if (status != AppPermissionStatus.Granted)
    status = await context.Permissions.RequestAsync("server.metrics.read");

if (status == AppPermissionStatus.Granted)
    await LoadMetricsAsync();

// Opens remoteos://settings/apps/{this-app}/permissions.
await context.Permissions.OpenSettingsAsync();
```

`RequestAsync` deliberately shows the one-permission prompt again even after a previous denial;
**Later** preserves the current decision. Applications must handle `Undecided` and `Denied` as
not granted and keep their non-privileged functionality available.

## Navigate to application settings

An external application can send the user to the host-owned Applications page without receiving any additional permission:

```csharp
await context.Settings.OpenApplicationsAsync();
```

The host reuses and focuses an existing Settings window when possible; otherwise it opens Settings and selects **Applications**. This API only performs navigation and cannot read or change settings.

## System language

Package applications can read the workspace display language and refresh their UI when it changes. This is read-only and does not require a permission:

```csharp
context.SystemLanguage.LanguageChanged += (_, change) =>
    RefreshUi(change.CurrentLanguage);

RefreshUi(context.SystemLanguage.CurrentLanguage); // BCP-47 name, such as "en-US"
```

The built-in language selector discovers files from the client `Localization` directory. A language file declares its BCP-47 `Culture`, display name, sort order, and source-string translations; adding a correctly formed `.json` file makes it selectable without changing client code.
