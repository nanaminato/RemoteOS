# 安装 Docker

本指南用于在 Linux 服务器上安装 Docker Engine。存在官方软件包时，应优先使用对应发行版的 Docker 官方软件源。

## 开始前

- 确认你拥有服务器管理员权限。
- 确认服务器能够访问 Docker 软件包仓库。
- 安装新版本前，检查并处理旧版 Docker 软件包。

## 安装步骤

1. 按服务器 Linux 发行版对应的 Docker Engine 官方说明安装软件包。
2. 启动 Docker 服务，并设为开机启动。
3. 验证守护进程正在运行。

```bash
docker --version
docker info
```

## 在 RemoteOS 中验证

打开 Docker Manager，等待状态卡片显示引擎可用。如果仍不可用，请检查服务日志，并确认当前用户有权访问 Docker socket。
