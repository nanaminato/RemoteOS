# RemoteOS 特权操作审计（2026-09-04）

本清单是 `RemoteOS.PrivilegedOperations.Goal.md` 的 Goal 0 实现记录。审计基于
`ProcessStartInfo`、宿主文件写入、服务控制、包安装、证书与网络代码路径的静态检索；它
只把实际需要 root/LocalSystem/Administrator 的动作列为特权路径。

| 范围 | 位置 | 分类 | 当前处置 |
| --- | --- | --- | --- |
| Explorer 受保护文件 | `Files`、`PrivilegedFileService` | 必须 Helper | 已迁移：capability、目标范围、Linux sudo Helper / Windows pipe Helper |
| Native service start/stop/restart | `ProcessGuardian/NativeServiceAdapter` | 必须 Helper | 已迁移：allowlisted service ID + 枚举 action；读取状态保持非特权 |
| UFW | `Firewall/LinuxUfwFirewallService` | 必须 Helper | 等价受限 Firewall Helper；不接受任意 firewall 命令 |
| Nginx 系统服务生命周期、APT 安装/卸载 | `WebServer/NginxWebServerManager` | 必须 Helper | 已迁移：固定 nginx.service 枚举动作与固定 APT nginx 包操作 |
| Nginx 集成、站点与 ACME 配置写入 | `WebServer/NginxWebServerManager` | 必须 Helper | 待迁移；受保护配置写入仍不允许以普通 Server 身份执行 |
| Mihomo systemd unit 与服务生命周期 | `Proxy/Platform/NativeMihomoPrivilegedOperations` | 必须 Helper | 已迁移：固定 unit、daemon-reload 与枚举 service action 均经 Helper |
| Mihomo 受保护配置与网络恢复 | `Proxy` | 必须 Helper | 待迁移；暂保持 fail-closed，不允许 Server 以特权直写 |
| ACME / 证书部署与 80/443 绑定 | `Certificate` | 必须 Helper | 待迁移；仅应用私有存储写入保持非特权 |
| Docker runtime 安装 | `Docker` | 必须 Helper 或手工操作 | 待逐项建模；不能安全结构化时返回 `manual-host-action-required` |
| Git engine 安装 | `Git/LocalGitRepositoryService` | 必须 Helper 或手工操作 | 待逐项建模；普通 Git 仓库操作是非特权 |
| Guardian workload、FRP、普通 Docker CLI | `Guardian`、`Tunnels`、`Docker` | 无特权 | 仅对 Server 私有目录或用户权限范围运行，不进入 Helper |
| 指标、Nginx/服务发现、配置测试 | `SystemMonitor`、`WebServer` | 无特权 | 固定只读子进程；不得借用 Helper |

## 冻结的安全默认值

- Windows Helper 运行账户：`LocalSystem`；Server：`NT AUTHORITY\\LocalService` 加服务 SID。
- Windows 管理员认证允许输入不同的宿主管理员用户名；通过 `LogonUser` 获取临时 token 后检查 Administrators 成员资格。
- Linux 仅验证当前 RemoteOS 宿主用户的 PAM 密码，不接受隐式的另一 sudo 管理员身份。
- Linux Firewall Helper 保持独立等价受限实现，后续增加统一审计关联。
- 不能建模为封闭结构化能力的安装器操作必须安全拒绝，不能退回到通用命令执行。

## 尚未完成的迁移门槛

`NginxWebServerManager`、Mihomo、证书、Docker 和 Git 安装相关的写入/进程调用尚未
迁入对应 Helper capability。它们不能被视为已完成，后续 Goal 4/5 必须以 operation-specific
接口替换这些路径，并在隔离 Ubuntu 与 Windows Server VM 中执行实机验证。
