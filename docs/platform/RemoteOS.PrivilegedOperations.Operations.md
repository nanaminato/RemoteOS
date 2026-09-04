# RemoteOS Privileged Helper 运维指南

本指南适用于以最小权限账户运行的 `RemoteOS.Server`。Server 不应以 root、
LocalSystem 或 Administrator 身份运行；所有成功的宿主特权操作必须有 Helper transport
审计记录。

## Linux

使用签名发布包中的 `deployment/linux/install-remoteos-services.sh` 安装。安装程序会：

- 创建 `remoteos-server` 系统账户；
- 将 Helper 发布目录、sudoers 与策略文件设为 root 所有且 Server 用户不可写；
- 将受管文件根和服务 ID 写入 `/etc/remoteos/privileged-helper-roots` 与
  `/etc/remoteos/privileged-services`；
- 仅允许 Server 用户以 `sudo -n` 调用无参数 Helper apphost。

安装后检查：

```text
systemctl status remoteos-server remoteos-guardian
sudo -u remoteos-server sudo -n /usr/local/lib/remoteos/privileged-helper/<apphost>
```

第二个命令没有 JSON 请求时必须失败；它只能证明 sudoers 指向固定 apphost，不能用于
执行命令。不要向 sudoers 增加通配符、shell 或自定义参数规则。

若增加受保护文件根或可控制服务，修改前必须进行安全审查；策略文件必须保持
`root:root`、`0600`。重装服务会恢复默认的最小策略。

## Windows Server

使用提升的会话运行 `deployment/windows/Install-RemoteOSServices.ps1`。它会安装：

- `RemoteOSPrivilegedHelper`：LocalSystem Windows Service；
- `RemoteOSServer`：LocalService，启用 service SID；
- `RemoteOSGuardian`：安装器声明的 Guardian 服务。

Helper 仅监听本机命名管道。管道 ACL 仅包含 LocalSystem、Administrators 和 Server
service SID；每条消息还必须通过安装时生成的共享密钥 HMAC 验证。`helper.json` 只能由
LocalSystem 与 Administrators 读取；Server 仅能读取自身 `appsettings.host.json` 中的密钥。
同一 operation ID 在十分钟内只能处理一次；客户端必须为新的操作生成新 ID，重复 ID 会被
Helper 以冲突结果拒绝而不会再次执行。
安装脚本还会把 Helper apphost 的 SHA-256 写入该受保护配置；Helper 启动时必须匹配该
清单，完整性不匹配时不会监听管道。升级或修复 Helper 必须重新运行安装脚本，不能直接
替换可执行文件。

安装后检查服务状态和 Event Viewer 中 `RemoteOSPrivilegedHelper` 的事件。若 Helper
缺失、密钥不匹配或协议版本不匹配，Server 必须返回 Helper 不可用，不能回退为启动提升的
可执行文件。

## 共享密钥轮换与升级

在维护窗口重新运行相同版本的签名安装脚本。脚本生成新的随机密钥、更新受保护配置并按
Helper、Guardian、Server 顺序重启服务。不得手工把密钥复制到用户配置、日志、数据库或
HTTP 请求中。

升级前记录当前 Helper 和 Server 发布版本；升级失败时恢复匹配的一对发布目录与受保护
配置，然后先启动 Helper，确认健康后再启动 Server。

## 故障排查

- `privileged-helper-unavailable`：检查 Helper 服务/系统单元、发布目录所有者、pipe ACL、
  sudoers 和密钥配置。
- `elevation-required`：客户端需要为当前 JWT、相同 capability 和精确资源重新进行宿主管理员认证。
- `access-denied` / `resource-not-allowed`：不要放宽 sudoers 或管道 ACL；检查 Helper 策略根、
  allowlisted 服务 ID 和受管实例 ID。
- `manual_host_action_required`：该能力尚未有安全的封闭模型，必须在宿主操作系统中按官方
  文档执行，不得把命令放入 Server 配置。

审计日志只包含 operation ID、operation、资源哈希、结果和问题码；若发现密码、JWT、
共享密钥、文件内容或完整命令行，应视为安全缺陷并立即轮换相关密钥。
