# Help Center 示例

此开发包注册 `help` URI 方案，并在一个可复用窗口中打开离线多语言 Markdown 指南。

示例：

```text
help://guide/docker/install?lang=en
help://guide/docker/uninstall?lang=zh-CN
```

在仓库根目录构建并安装：

```bash
export REMOTEOS_DEV_TOKEN="33CqN1nDrp0xP2bBLd7sZfw9APHrnbiIAg_gzYQwo-w"
dotnet run --project Tools/RemoteOS.DevCli -- pack ./examples/HelpCenter --configuration Debug --install
```

安装后，在“设置 → 默认应用”中将 **Help Center** 设为 `help` 的默认程序。未安装其他 `help` 处理程序时，Shell 会自动选择它。
