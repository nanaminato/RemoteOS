# RelaxKonOS 一键服务端安装

发布包包含已 `dotnet publish` 的 Server、Guardian Agent、权限助手，以及本目录中的平台部署引擎。引导安装器负责语言、预检、安装来源、监听方式、文件权限范围、服务启动和健康检查；平台部署引擎只负责受控的系统级变更。

引导安装器会先把三个组件的完整 publish 输出复制到持久安装目录（Windows 默认 `C:\Program Files\RelaxKonOS`，Linux 默认 `/opt/relaxkonos`）；服务绝不会指向临时下载目录或离线介质。

## 发布包布局

```text
manifest.json
payload/windows/{server,guardian,privileged-helper}/...
payload/linux/{server,guardian,privileged-helper}/...
deployment/windows/Install-RelaxKonOSServices.ps1
deployment/linux/install-relaxkonos-services.sh
```

`manifest.json` 的首个版本使用 `schemaVersion: 1`；示例见 [release-manifest.example.json](./release-manifest.example.json)。线上安装必须由发布页同时提供 ZIP 的 SHA-256，安装器会在解压前验证它。正式发行应在此基础上对 ZIP 使用代码签名或签名的发布清单。

维护者用下列命令制作一个自包含的单平台发布包（会同时生成 ZIP 与同名 `.sha256` 文件）：

```powershell
./deployment/packaging/New-RelaxKonOSRelease.ps1 -Version 0.1.0 -Runtime win-x64
```

## Windows

管理员 PowerShell 中运行：

```powershell
& .\deployment\bootstrap\Install-RelaxKonOS.ps1 -BundlePath 'D:\RelaxKonOS-release'
```

也可以传入 `-ReleaseUri` 与必须的 `-ReleaseSha256` 安装在线 ZIP。`-NonInteractive` 适用于自动化；此时必须显式提供发布来源，默认仅监听 `127.0.0.1:5000`、只允许权限助手访问 RelaxKonOS 数据目录。

## Linux

```bash
sudo ./deployment/bootstrap/install-relaxkonos.sh --bundle /mnt/RelaxKonOS-release
```

在线安装传入 `--release-uri URL --release-sha256 SHA256`。Linux 权限助手不是常驻服务：Server 账户只能通过固定的 sudo 规则执行 root-owned Helper；Server 和 Guardian 则是 systemd 服务。

局域网模式仅将 Server 绑定到 `0.0.0.0`，不会自动打开防火墙。公网部署请选择反向代理模式（默认本机监听），并由反向代理终结 HTTPS。
