# 开发调试指南

常规开发调试时**不要**运行部署脚本，也**不需要**注册 Windows 服务。直接在 Rider 中同时启动 Agent 和 Server 即可。仅当需要测试真实 Linux UFW 变更时，按下节完成一次特权 helper 配置。

---

## 常规桌面系统（Windows 10/11）

### 特权 Helper 日常调试

不要为日常断点调试安装 `RemoteOSPrivilegedHelper` 服务。创建开发专用配置（不可放在
`ProgramData\RemoteOS\privileged-helper`，且仅允许测试目录），然后直接从 IDE 启动：

```powershell
dotnet run --project RemoteOS.PrivilegedHelper -- --console --config C:\RemoteOS-dev\privileged-helper.debug.json
```

配置必须显式包含 `allowConsoleDebug: true`，并配置与 Server 完全相同的
`pipeName`、随机 Base64 `sharedSecret`、`fileAllowedRoots` 与 `allowedServiceIds`。Server 启动
配置中分别设置 `PrivilegedHelper__PipeName` 和 `PrivilegedHelper__SharedSecret`。这样 Server
仍通过正式的命名管道、HMAC、重放保护和固定请求协议调用 Helper，断点则直接命中同一进程中的
执行器。只有需要验证真实管理员行为时才以管理员身份启动 IDE。

`--console` 不能读取并启用生产 `helper.json`：它使用独立配置结构，并要求显式开发开关。发布前
仍必须在隔离 Windows VM 以 LocalSystem 服务模式至少验证一次，以覆盖 Session 0、HKCU、用户
profile、DPAPI、网络凭据、映射盘和环境变量差异。

### 1. 创建调试用户

以**管理员身份**打开 PowerShell，创建用于调试的本地用户：

```powershell
# 以管理员身份运行
New-LocalUser -Name "testuser" -Password (ConvertTo-SecureString "Test@123" -AsPlainText -Force)

# 验证用户是否创建成功
Get-LocalUser | Select-Object Name, Enabled
```
预期输出示例：
```bash
Name               Enabled
----               -------
Administrator      False
betha               True
DefaultAccount     False
Guest              False
testuser            True
WDAGUtilityAccount False
WsiAccount         False
```
### 2. 验证用户密码
创建完成后，可以用以下命令验证密码是否正确：
```powershell
# 使用 PrincipalContext 验证
$ctx = New-Object System.DirectoryServices.AccountManagement.PrincipalContext([System.DirectoryServices.AccountManagement.ContextType]::Machine)
$ctx.ValidateCredentials("testuser", "Test@123")
```
返回 True 表示验证成功。

注意：不要用当前已登录的账户测试 ValidateCredentials，Windows 安全机制会阻止已登录用户验证自己的密码。

### 3. 清理测试用户（可选）
调试完成后可删除测试用户：
```powershell
Remove-LocalUser -Name "testuser"
```

## Linux 防火墙调试(linux 专用)

Firewall 不要求以 root 启动 `dotnet run`。若要在 Linux 调试真实 UFW 操作，先由管理员为**实际运行 Rider（或其他 IDE）的 Linux 账户**执行一次开发配置脚本：

```bash
sudo deployment/linux/install-remoteos-firewall-development.sh "$USER"
```
### 脚本说明：
- 安装 root-owned 的 helper 程序
- 为开发账户写入只允许调用此 helper 的受限 sudoers 规则
- 不会创建或启动 systemd 服务
- 不会启动 Server、Agent 或 Desktop  

因此可直接在 Rider 中启动这三个项目进行调试。Server 使用以下命令完成经过双重校验的 UFW 操作：  
```bash
sudo -n /usr/local/lib/remoteos/remoteos-firewall-helper
```
### 注意事项：
- ❌ 不要使用 sudo dotnet run
- 若只调试 UI/API 而未安装 helper，Firewall 会稳定显示 firewall.privileged_proxy_required

## 进程守护
### 1. 配置 Agent 环境变量
新建 RemoteOS.Guardian.Agent 的 .NET Project 启动配置，在“环境变量”中逐项加入：
```bash
REMOTEOS_GUARDIAN_PIPE=remoteos-guardian-dev
REMOTEOS_GUARDIAN_SHARED_SECRET=dev-guardian-secret-local-only
REMOTEOS_GUARDIAN_DATA_DIR=E:\riderprojects\RemoteOS\.codex-scratch\guardian-dev 
```
注意：REMOTEOS_GUARDIAN_DATA_DIR 需要替换为计算机上实际存在的目录。
### 2. 配置 Server 环境变量
在 RemoteOS.Server 的启动配置中加入同一对 Pipe/密钥：
```bash
GuardianAgent__PipeName=remoteos-guardian-dev
GuardianAgent__SharedSecret=dev-guardian-secret-local-only
```
注意在 Rider 中每个环境变量单独添加。
### 3.启动顺序：
按以下顺序启动：
1. RemoteOS.Guardian.Agent
2. RemoteOS.Server
3. RemoteOS.Client（连接到调试 Server）

### 4. 验证守护程序状态
   重启 Server 后，守护程序状态显示说明：

| 状态 | 含义 |
|------|------|
| 可用 | ✅ Agent 运行正常 |
| guardian.agent_unavailable | Agent 未运行，但密钥配置正确 |
| guardian.agent_not_configured | Server 的两个环境变量未生效（检查配置） |

### 5. 测试工作负载
   使用以下不会长期占用业务端口的 workload 做首次验证：

| 字段     | 值 |
|--------|-----|
| 工作负载名称 | Development ping |
| 可执行文件  | C:\Windows\System32\PING.EXE |
| 工作目录   | C:\Windows\System32 |
| 参数     | 127.0.0.1<br>-t |

保存后应出现在左侧列表。点击启动，再点击"查看日志"可看到输出；停止或删除可验证完整生命周期。

**注意**：调试时不要勾选"宿主机重启后自动启动"。

### 6. 测试 .NET/Java 应用
   .NET 应用配置：

| 字段 | 值 |
|------|-----|
| 可执行文件 | C:\Program Files\dotnet\dotnet.exe |
| 工作目录 | 应用发布目录（绝对路径） |
| 参数 | MyApp.dll |

Java 应用配置：

| 字段 | 值 |
|------|-----|
| 可执行文件 | ...\bin\java.exe |
| 工作目录 | 应用目录（绝对路径） |
| 参数 | -jar<br>app.jar |

通用要求：

- 工作目录必须是存在的绝对路径
- 可执行文件可填写存在的绝对路径，或填写 Guardian Agent PATH 中的程序名（例如 dotnet）
- 保存时会解析并持久化为绝对路径
- 实际可访问性由目标 RunAs 账户的 OS 权限决定
