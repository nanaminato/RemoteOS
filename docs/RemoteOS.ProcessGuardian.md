# RemoteOS ProcessGuardian 设计

> 规划中的内置进程守护管理器。它统一管理由 RemoteOS 声明和守护的后台程序，并以只读/受控方式管理现有 `systemd` 单元和 Windows SCM 服务；不是任务管理器的替代品。
>
> - 即时进程查看与结束任务：[`RemoteOS.TaskManager.md`](./RemoteOS.TaskManager.md)
> - 安全与权限提升：[`RemoteOS.Security.md`](./RemoteOS.Security.md)
> - 内置应用通用约束：[`RemoteOS.BuiltInApplication.Conventions.md`](./RemoteOS.BuiltInApplication.Conventions.md)

---

## 1. 定位和边界

`RemoteProcessGuardian`（应用 ID：`remoteos.processguardian`）提供“定义 → 验证 → 部署 → 启动 → 健康检查 → 自动恢复 → 日志/审计 → 停用”的服务化闭环。它借鉴 PM2 的进程清单、启动恢复、生态配置与实时日志，以及 systemd/Windows 服务的启动类型和依赖模型。PM2 将守护进程清单持久化并在主机重启后恢复，且支持实时及落盘日志。[PM2 Process Management](https://pm2.io/docs/runtime/guide/process-management/) [PM2 Log Management](https://pm2.io/docs/runtime/guide/log-management/)

任务管理器面对“主机当前所有可见进程”，可直接结束；守护管理器面对“已登记的工作负载”，关心所需状态、退出原因、重启预算、依赖和启动恢复。两者必须互相链接，但不得共享可变状态或绕开各自权限。

### 1.1 v1 范围

| 项目 | 说明 |
|---|---|
| Guardian workload | 可执行文件、脚本解释器或受控命令；工作目录、参数、环境引用、运行账户、启动依赖、重启策略、健康检查、日志策略 |
| 生命周期 | 验证、启用/禁用、启动、优雅停止、强制停止、重启、重载（仅声明支持时）、开机恢复 |
| 可观测性 | 当前/期望状态、PID、退出码、重启次数、CPU/内存、健康结果、事件时间线、stdout/stderr 实时与历史日志 |
| 原生服务 | 列表、详情、启动/停止/重启、启动类型读取；通过适配器管理，而不将服务配置伪装成 Guardian 定义 |
| 安装 | 安装或修复 **RemoteOS Guardian Agent**（不是任意第三方应用）；用户的工作负载由 Agent 部署和守护 |

不支持在 v1 中把已有任意进程“接管”为守护对象、编辑系统关键服务、储存运行账户密码、在客户端启动宿主进程，或把 Windows `.bat`/shell 文本直接注册为服务。

---

## 2. 运行模型

为确保 Server 重启或用户登出后仍能守护工作负载，守护功能不能依附在 `RemoteOS.Server` 进程内。目标架构是一个独立的本机 `RemoteOS.Guardian.Agent`：Ubuntu 上为受限 systemd service，Windows 上为 Windows Service。Server 以仅限本机、相互认证的 IPC 向 Agent 下达结构化命令；Agent 才是子进程父级和日志捕获者。

```text
RemoteProcessGuardian (Client UI)
       │ HTTPS + JWT
RemoteOS.Server: Guardian endpoints
       │ 授权 / 验证 / 审计 / 定义仓储
       │ local authenticated IPC
RemoteOS.Guardian.Agent
       ├─ Supervisor（子进程、重启、日志、健康检查）
       ├─ systemd adapter (Ubuntu)
       └─ SCM adapter (Windows)
              └─ workload / native service
```

Agent 的身份和 IPC 端点不得被普通用户读取或连接。Agent 只接受来自已验证 Server 的请求，拒绝原始 shell 命令；Server 也只能将通过验证的 `ProcessDefinition` 编译为 `LaunchSpec`。

### 2.1 状态机

```text
Draft → Validated → Disabled ⇄ Starting → Running ⇄ Degraded
                           │       │          │
                           │       └→ Stopping → Stopped
                           └──────────────────→ Failed

Failed -- restart budget available --> Backoff → Starting
Failed -- budget exhausted ---------> CrashLoop
```

- **desired state** 由用户动作或“开机启用”决定；**actual state** 由 Agent 事件决定，不能由 UI 乐观伪造。
- 优雅停止先发送平台适当的终止请求，等待 `StopTimeout`，超时后才允许经确认的强制终止整个子进程树。
- 每次启动记录 generation；同 PID 复用或旧事件不得覆盖新 generation。

### 2.2 守护定义

`ProcessDefinition` 是 Protocol 中版本化、可审计的声明，最小字段如下：

| 组 | 字段 |
|---|---|
| 标识 | `Id`、稳定 `Name`、显示名/描述本地化键、Owner、标签、DefinitionVersion |
| 启动 | `ExecutablePath`、结构化 `Arguments[]`、`WorkingDirectory`、`Interpreter`、`RunAs`、环境变量（值或 SecretReference） |
| 可用性 | `EnabledOnBoot`、依赖项、`RestartPolicy`、最大重启次数、窗口期、退避上限、启动/停止超时 |
| 健康 | 无/进程存活、HTTP(S)、TCP；间隔、超时、连续失败阈值、初始宽限期 |
| 日志 | 捕获 stdout/stderr、结构化格式、单文件大小、保留数、保留天数、脱敏规则 |
| 安全 | 允许的路径根、能力标记、配置修改所需确认级别 |

参数永远是数组而不是命令字符串，禁止 shell expansion、`cmd.exe /c`、`sh -c` 等隐式解释器。脚本必须明确选择已批准解释器和绝对路径。敏感环境变量只存 `SecretReference`，由 Agent 在启动瞬间从 OS 安全存储解析，API、数据库、日志和 UI 一律只显示占位符。

---

## 3. 平台适配与安装

| 能力 | Ubuntu | Windows |
|---|---|---|
| Guardian Agent | `remoteos-guardian.service`，由 systemd 在开机启动 | `RemoteOSGuardian` Windows Service，SCM 管理 |
| workload 启动 | Agent 直接 fork/exec；不要求用户 shell | Agent 以结构化 `ProcessStartInfo` 启动，使用 Job Object 管理进程树 |
| 原生服务读取/控制 | `systemctl`/D-Bus 适配器，白名单单元 | `ServiceController`/SCM 适配器，白名单服务 |
| 重启语义 | Agent 统一执行；不依赖单元 `Restart=` | Agent 统一执行；不伪造每个 workload 为 SCM 服务 |
| 安装/修复 | 包管理器安装 Agent unit，`systemctl enable --now` | 签名安装包注册 Agent service；SCM 设置自动启动 |

Windows 的 SCM 支持服务的 `auto`、`demand`、`disabled`、`delayed-auto` 等启动类型和依赖；设计只在原生服务适配器中映射这些含义，不把它们错误映射为普通进程。[sc.exe create 参考](https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/sc-create)

### 3.1 安装流程

1. 探测 Agent 是否存在、版本/签名、IPC 可达性、服务状态、磁盘、可执行路径和所需权限。
2. 显示安装计划、受影响服务、包来源/签名、数据目录与回滚说明。
3. 用户拥有 `server.guardian.install` 后明确确认；宿主 OS 提权机制运行签名安装包或官方系统包，RemoteOS 不请求或保存管理员密码。
4. 启动 Agent，建立本地 IPC，执行自检工作负载，记录安装审计。
5. 若失败，保留诊断和已完成步骤；仅在安装器明确提供安全卸载计划时执行回滚。

运行用户和服务账户是安全边界。v1 默认仅允许由 Agent 的受限服务账户运行工作负载；以自定义用户运行属于后续能力，必须接入 OS 凭据库、路径 ACL 检查和单独授权，不能通过表单保存密码。

---

## 4. UI、接口与数据

### 4.1 信息架构

```text
概览（运行数 / 异常 / 最近事件）
├─ 受守护工作负载（列表、筛选、批量安全操作）
├─ 新建 / 编辑向导（命令、生命周期、健康、日志、审查）
├─ 原生服务（只读默认；按许可控制）
├─ 日志与事件
└─ Agent（状态、安装/修复、版本、诊断）
```

编辑向导的最后一步是不可绕过的“审查”：显示规范化后的可执行路径、每个参数、启动用户、可访问路径、端口健康检查、重启预算和秘密引用数量。启动/停止/重启的单项操作可立即执行；批量操作和强制停止要求展示影响列表。

### 4.2 服务端契约（拟定）

Protocol 放于 `Shared/RemoteOS.Protocol/ProcessGuardian/`，仅以 DTO、路由和问题码暴露。拟定端点：

| 方法 | 路由 | 权限 |
|---|---|---|
| GET | `/api/v1/guardian/status` | `server.guardian.read` |
| GET/POST | `/api/v1/guardian/workloads` | read/manage |
| GET/PATCH/DELETE | `/api/v1/guardian/workloads/{id}` | read/manage |
| POST | `/api/v1/guardian/workloads/{id}/{start|stop|restart|reload}` | `server.guardian.manage` |
| GET | `/api/v1/guardian/workloads/{id}/logs` | read |
| GET | `/api/v1/guardian/services` | `server.services.read` |
| POST | `/api/v1/guardian/services/{id}/{action}` | `server.services.manage` |
| POST | `/api/v1/guardian/agent/installation/{plan|execute}` | `server.guardian.install` |

`IProcessGuardianService`（Server）和 `IGuardianAgentClient`（IPC）实现两层边界。调用超时、取消、断线和幂等键必须贯穿两层；Agent 事件通过 Server 过滤后使用 SignalR 推送。只有日志尾部/增量事件可流式传输，历史日志按游标分页并应用大小限制。

SQLite 是声明、审计和历史摘要的真源；Agent 在本机持有可重放的最小运行快照，以便 Server 不可用时仍按已批准定义恢复。Agent 恢复 Server 连通后上报 generation、实际状态和未上送事件，由 Server 幂等合并。原始日志留在 Agent 的受限目录，按策略滚动；数据库只保存索引、校验和短摘要。

---

## 5. 权限、可靠性和验收

新增权限：

| 权限 | 允许内容 |
|---|---|
| `server.guardian.read` | Agent、工作负载、日志和事件的只读访问 |
| `server.guardian.manage` | 创建/修改定义、启停/重启受守护工作负载、日志策略 |
| `server.guardian.install` | 安装、修复、升级 Guardian Agent |
| `server.services.read/manage` | 现有系统服务的读取/控制；与 Guardian 权限分离 |

- 禁止用户用 Guardian 改写 RemoteOS 自身、SSH、登录、网络、防火墙、Docker 等受保护服务，除非未来显式白名单并具有专门的高风险策略。
- 运行文件、工作目录、日志路径和健康检查 URL 要做存在性、规范化路径、允许根、符号链接与访问权限验证；拒绝网络共享/临时目录等默认不安全位置。
- 日志需速率限制、大小上限、轮转和敏感值清洗；健康检查不得携带未掩码的 URL 密钥。
- 因配置无效、权限不足、二进制缺失、退出非零、健康检查失败、重启耗尽而失败时，界面显示稳定问题码与下一步建议，审计留完整安全诊断。

实施顺序：先实现 Agent 安装/IPC/status 与只读工作负载；再实现定义校验、启动/停止/重启、日志和状态机；再加重启/健康/开机恢复；最后接入原生服务适配器和 UI 向导。验收必须在 Ubuntu 与 Windows 各验证 Agent 重启、主机重启、崩溃退避、进程树清理、日志轮转、Server 暂时不可用、权限拒绝、秘密脱敏和审计重放。

