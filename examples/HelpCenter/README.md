# Help Center example

This development package registers the `help` URI scheme and opens offline multilingual Markdown guides in one reusable window.

Examples:

```text
help://guide/docker/install?lang=en
help://guide/docker/uninstall?lang=zh-CN
```

Build and install it from the repository root:

```bash
export REMOTEOS_DEV_TOKEN="<token from Settings > Applications > Developer Mode>"
dotnet run --project Tools/RemoteOS.DevCli -- pack ./examples/HelpCenter --configuration Debug --install
```

Once installed, choose **Help Center** as the default program for `help` in Settings → Default apps. With no competing `help` handler installed, the Shell selects it automatically.
