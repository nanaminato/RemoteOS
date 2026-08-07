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
  "requestedPermissions": ["desktop.wallpaper.write"]
}
```

The entry type must implement `RemoteOS.AppSDK.IExternalRemoteApplication`. It receives `IExternalAppContext`, which exposes only approved RemoteOS capabilities, including owned-window creation and the permission-gated desktop appearance service.

## Server monitoring capability

`IExternalAppContext.ServerMonitor` provides a stable, read-only aggregate server metrics API. It requires the `server.metrics.read` manifest permission and user approval under **Application permissions**. `GetSnapshotAsync` returns one capability result; `WatchAsync` returns a host-polled sequence (minimum one-second interval) for live dashboards. It deliberately does not expose process enumeration, process termination, raw server credentials, or the task-manager client.

The complete sample is in [`examples/ServerMonitor`](../examples/ServerMonitor). Build and package it with:

```powershell
.\examples\ServerMonitor\build-package.ps1
$env:REMOTEOS_DEV_TOKEN = "<token from Settings>"
dotnet run --project Tools/RemoteOS.DevCli -- install .\examples\ServerMonitor\bin\Debug\net10.0\RemoteOS.Example.ServerMonitor.roapp
```

After installation, grant **读取服务器性能指标** to **Server Monitor** in Settings, then launch the desktop icon.

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
