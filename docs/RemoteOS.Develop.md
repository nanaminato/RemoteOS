常规开发调试时不要运行部署脚本，也不需要注册 Windows 服务。直接在 Rider 中同时启动 Agent 和 Server 即可；仅当需要测试真实 Linux UFW 变更时，按下节完成一次特权 helper 配置。

## Linux 防火墙调试

Firewall 不要求以 root 启动 `dotnet run`。若要在 Linux 调试其真实 UFW 操作，管理员可用现有部署脚本的第五个参数指定当前开发账户；脚本会安装一次 root-owned helper 和仅针对该账户的受限 `sudoers` 规则：

```bash
sudo deployment/linux/install-remoteos-services.sh \
  /absolute/publish-root \
  /absolute/publish-root/RemoteOS.Server \
  /absolute/publish-root/RemoteOS.Guardian.Agent \
  5000 \
  "$USER"
```

随后以该普通账户运行 Server；它使用 `sudo -n /usr/local/lib/remoteos/remoteos-firewall-helper` 完成经过双重校验的 UFW 操作。不要使用 `sudo dotnet run`。若只调试 UI/API 而未安装 helper，Firewall 会稳定显示 `firewall.privileged_proxy_required`。
新建 RemoteOS.Guardian.Agent 的 .NET Project 启动配置，在“环境变量”中逐项加入：
```bash
REMOTEOS_GUARDIAN_PIPE=remoteos-guardian-dev
REMOTEOS_GUARDIAN_SHARED_SECRET=dev-guardian-secret-local-only
REMOTEOS_GUARDIAN_DATA_DIR=E:\riderprojects\RemoteOS\.codex-scratch\guardian-dev
```
在 RemoteOS.Server 的启动配置中加入同一对 Pipe/密钥：
```bash
GuardianAgent__PipeName=remoteos-guardian-dev
GuardianAgent__SharedSecret=dev-guardian-secret-local-only
```
注意在 Rider 中每个环境变量单独添加。
启动顺序：
RemoteOS.Guardian.Agent
RemoteOS.Server
RemoteOS.Client，连接该调试 Server

重启 Server 后，守护程序状态应显示为可用。若 Agent 未运行但密钥正确，会显示 guardian.agent_unavailable；若仍是 guardian.agent_not_configured，说明 Server 的两个环境变量没有生效。
可用下面这个不会长期占用业务端口的 workload 做首次验证：
```bash
ID：dev-ping
名称：Development ping
可执行文件：C:\Windows\System32\PING.EXE
工作目录：C:\Windows\System32
参数（每行一个）：
127.0.0.1
-t
```
保存后应出现在左侧列表；点击启动，再点击“查看日志”可看到输出；停止或删除可验证完整生命周期。调试时不要勾选“宿主机重启后自动启动”。
测试 .NET/Java 时：
.NET：可执行文件填 C:\Program Files\dotnet\dotnet.exe，工作目录填应用发布目录，参数填 MyApp.dll。
Java：可执行文件填 ...\bin\java.exe，参数每行填 -jar、app.jar。
可执行文件和工作目录必须是存在的绝对路径；实际可访问性由目标 `RunAs` 账户的 OS 权限决定。
