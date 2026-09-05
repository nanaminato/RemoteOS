# RemoteOS 开发者 CLI

`remoteos-dev` 会发布 RemoteOS 应用项目并创建其 `.roapp` 包，无需为项目编写专用 Shell 脚本。

```bash
remoteos-dev pack ./MyApp --configuration Release
remoteos-dev pack ./MyApp --runtime win-x64 --configuration Release --install
remoteos-dev watch ./MyApp --runtime win-x64 --configuration Debug
```

`pack` 默认写入 `artifacts/<entry-assembly>.roapp`。它要求 `.csproj` 旁存在 `manifest.json`；可用 `--manifest` 和 `--output` 覆盖这些路径。它会打包 `manifest.json` 的 `entryAssembly` 所声明目标框架目录下的全部 `dotnet publish` 输出，包括私有依赖项和原生运行时资产。

对会连接正在运行的 RemoteOS Shell 的命令设置 `REMOTEOS_DEV_TOKEN`（或传入 `--token`）：`--install`、`watch`、`apps`、`install`、`update`、`launch` 和 `uninstall`。使用 `watch --no-install` 可在不连接 Shell 的情况下构建包。

不带参数运行 `remoteos-dev` 可查看完整命令参考。RemoteOS 仓库的“开发者模式”指南说明包格式和兼容性约定。
