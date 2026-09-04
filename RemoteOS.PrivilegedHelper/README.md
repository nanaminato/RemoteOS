# RemoteOS.PrivilegedHelper

This is a short-lived local root helper, not a network service and not the Guardian Agent.
It receives one JSON request on standard input and returns one JSON response on standard output.
`read-file` and `write-file` are used by RemoteExplorer; `run` is the future generic host-command entry point.

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
