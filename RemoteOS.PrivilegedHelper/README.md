# RemoteOS.PrivilegedHelper

This is the narrow local privileged boundary, not a network service and not the Guardian Agent.
Linux uses a short-lived root worker over standard input/output. On Windows, both the LocalSystem
service and the developer console host use the same authenticated named-pipe protocol and the
same closed-set operation dispatcher. It never accepts an arbitrary command or executable.

## Development

Build the helper normally:

```bash
dotnet build RemoteOS.PrivilegedHelper/RemoteOS.PrivilegedHelper.csproj
```

To exercise the real Server → sudo → helper path, allow the account running the debug Server to
invoke the generated apphost without a sudo password, for example with a development-only
`/etc/sudoers.d/remoteos-privileged-helper-dev` entry:

```sudoers
your-dev-user ALL=(root) NOPASSWD: /absolute/path/to/RemoteOS.PrivilegedHelper/bin/Debug/net10.0/RemoteOS.PrivilegedHelper
```

Then set the corresponding development configuration:

```json
{
  "PrivilegedHelper": {
    "HelperPath": "/absolute/path/to/RemoteOS.PrivilegedHelper/bin/Debug/net10.0/RemoteOS.PrivilegedHelper",
    "SudoPath": "/usr/bin/sudo"
  }
}
```

The debug output is writable by the development user, so this sudoers rule intentionally grants
that user root-equivalent capability. Never use it outside a disposable development machine.

## Windows development

Run the Helper directly from the IDE with `--console`; do not install a Windows service for daily
development. Create a development-only configuration outside the deployment directory, using a
new random Base64 secret (at least 32 bytes) and only disposable file roots:

```json
{
  "pipeName": "remoteos-privileged-helper-dev",
  "sharedSecret": "replace-with-a-random-base64-secret-of-at-least-32-bytes",
  "fileAllowedRoots": ["C:\\RemoteOS-dev"],
  "allowedServiceIds": ["RemoteOSServer-dev"],
  "allowConsoleDebug": true
}
```

Start it from the IDE or a terminal:

```powershell
dotnet run --project RemoteOS.PrivilegedHelper -- --console --config C:\RemoteOS-dev\privileged-helper.debug.json
```

Configure the debug Server with the same pipe name and secret:

```text
PrivilegedHelper__PipeName=remoteos-privileged-helper-dev
PrivilegedHelper__SharedSecret=<same Base64 secret>
```

The console host grants pipe access only to the interactive developer account (plus SYSTEM and
Administrators), which lets a Server launched by that account use the production IPC path. Run
the IDE elevated only when testing operations that genuinely require Administrator rights. The
configuration requires `allowConsoleDebug: true`; the production `helper.json` does not use this
schema and cannot enable console mode accidentally. Before release, test once through the
LocalSystem service to cover Session 0, profile, DPAPI, network-credential and mapped-drive
differences.

## Linux release installation

Publish the project first (the helper needs its `.runtimeconfig.json`, `.deps.json`, and any
managed assemblies next to the executable):

```bash
dotnet publish RemoteOS.PrivilegedHelper/RemoteOS.PrivilegedHelper.csproj -c Release -r linux-x64 --self-contained false
```

Pass the published apphost as the fourth argument of
[`install-remoteos-services.sh`](../deployment/linux/install-remoteos-services.sh). The installer
copies the whole publish directory into a root-owned location and creates the narrow sudoers rule
for the Server service account.
