# RemoteOS Developer CLI

`remoteos-dev` publishes a RemoteOS application project and creates its `.roapp` package without a project-specific shell script.

```bash
remoteos-dev pack ./MyApp --configuration Release
remoteos-dev pack ./MyApp --runtime win-x64 --configuration Release --install
remoteos-dev watch ./MyApp --runtime win-x64 --configuration Debug
```

`pack` writes `artifacts/<entry-assembly>.roapp` by default. It requires a `manifest.json` beside the `.csproj`; use `--manifest` and `--output` to override those paths. It packages all `dotnet publish` output beneath the target framework directory declared by `manifest.json`'s `entryAssembly`, including private dependencies and native runtime assets.

Set `REMOTEOS_DEV_TOKEN` (or pass `--token`) for commands that contact a running RemoteOS Shell: `--install`, `watch`, `apps`, `install`, `update`, `launch`, and `uninstall`. Use `watch --no-install` to build packages without a Shell.

Run `remoteos-dev` with no arguments to see the complete command reference. The RemoteOS repository's Developer Mode guide describes the package format and compatibility contract.
