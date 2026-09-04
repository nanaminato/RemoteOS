# 代理管理器实现调研

## 阶段 0 结论

本文将完整阅读的 `docs/applications/RemoteOS.ProxyManager.Design.md`（4,155 行）映射到 2026-08-31 检查的仓库。阶段 0 不改变产品代码：尚无 Proxy Manager API、服务、Mihomo 控制器客户端、运行时安装器、配置文件存储或 Avalonia UI。

实现必须是主机全局内置应用，可复用现有 Protocol、类型化 HTTP 客户端、Avalonia 工作区/模态框、密钥、操作台账、审计和运行时安全模式。不得将 FRP 子进程监管用于 Mihomo：TUN 需要原生 OS 服务、管理路由保护、恢复标记和网络回滚。

## 相关项目

| 区域 | 仓库实际角色 | 代理映射 |
| --- | --- | --- |
| `Shared/RemoteOS.Protocol` | Client/Server DTO、枚举、路由/Hub 常量、JSON 约定 | 在此新增 Proxy 协议族 |
| `RemoteOS.Server` | ASP.NET Core .NET 10 宿主、Minimal API、主机集成、SQLite 存储 | 新增代理领域、平台适配器、服务、提供程序和端点 |
| `Client/RemoteOS.Client` | Avalonia Shell、内置应用、类型化远程客户端、DI、本地化、主题 | 新增 `remoteos.proxy`、类型化仓库、工作区/页面/视图模型 |
| `Framework/RemoteOS.Core` | 应用清单及请求能力声明 | 仅新增清单所需的稳定代理能力 |
| `Framework/RemoteOS.App.SDK` 与 `RemoteOS.WindowManager` | 托管窗口和所有者范围模态框 | 用于代理窗口及配置文件/配置/恢复对话框 |
| `RemoteOS.Server.Tests` | 控制台验证套件，不是 xUnit | 新增聚焦的协议/安全测试，再新增平台集成测试 |

Server 是单个 Windows/Linux 程序集；`Program.cs` 组合平台实现并映射端点族。当前没有 `RemoteOS.Network`、`PlatformPaths` 抽象、通用服务管理器、通用操作框架或代理功能。

## 现有抽象与约束

- `RemoteOsEndpoints` 将 REST 根固定为 `/api/v1`；代理必须使用 `/api/v1/proxy`，不能使用规范中的无版本 `/api/proxy` 示例。协议放在 `Shared/RemoteOS.Protocol/Proxy`，端点和客户端不得重复路由字符串。端点使用 `RequireAuthorization` 与 `WithTags`；WebServer 是需要 `Idempotency-Key`、返回 `202 Accepted` 持久操作、并把提权问题码映射为 403 的参考。
- JWT 是 Server 基础保护。`TunnelsRead` 允许 controller/observer，`TunnelsManage` 要求 controller。`AppPermissions` 是桌面清单/能力目录，只用于启用 UI，不是 Server 授权。代理必须实施真正的读/管理/TUN/运行时/恢复策略，并保留认证、主机 OS 检查、确认与审计。
- `ISecretStore`/`DataProtectionSecretStore` 是仅 Server 加密密钥的模式；隧道安全 DTO 只返回 `TokenConfigured`，FRP 日志有边界且脱敏。代理须使用独立用途的代理密钥存储，永不在路由、DTO、审计、日志、异常响应或 UI 状态中返回控制器密钥、订阅 Token、认证头、代理凭据、UUID 或私有/WireGuard 密钥。
- `TunnelAudit` 是简易审计参考；`HostOperationJournal`、`WebServerOperationStore` 与 `CertificateOperationStore` 是主机全局持久操作参考。代理长操作应复用或有意抽取 WebServer 的幂等性、取消、阶段/进度、持久关联 ID、锁定和中断恢复语义；不能无说明地复制第三套存储。TUN 还必须有持久恢复标记和回滚状态。
- `FrpRuntimeManager` 可提供固定清单、HTTPS 下载、归档验证、SHA-256、暂存、可执行健康检查、版本、当前/上一版本和回滚的安全经验；`FrpTunnelProvider` 可提供临时写入、验证、备份、原子提交、重启、尝试回滚模式。两者均为 FRP 专属，不得扩展；Mihomo 必须走原生 Windows 服务管理器或 systemd 生命周期及受保护路径。
- `INativeServiceAdapter` 只对白名单服务执行查询/启停/重启，不能安装或删除服务。`IHostPrivilegeService` 只报告 Server 是否已 root/管理员；Linux Firewall 有仅接受结构化防火墙操作的 root 辅助程序。它们不能变成通用代理命令执行器。Guardian 安装器和 Docker 安装计划也不是提权工作流。

需要新增：专注于 Windows ProgramData 与 Linux `/etc`、`/var/lib`、`/var/log`、`/opt` 的代理路径抽象；Windows/Linux `IProxyPlatformService`（能力、接口/路由/DNS 检查、路由保护、快照、恢复、TUN 诊断）；只允许已命名代理操作的强类型特权边界；以及可能从 `INativeServiceAdapter` 抽取的白名单生命周期控制。不得创建通用执行器，不得将 `systemctl`、`sc.exe`、`netsh`、PowerShell 或 `ip` 命令散落到业务服务。

## UI、主题、本地化与所有权

先提供受限且脱敏的 REST 日志；若后续需要实时日志/连接，应沿用现有 SignalR Hub 协议，不新增原始套接字框架。内置应用派生自 `RemoteApplicationBase`，声明 `remoteos.*` AppId/清单，从 `AppContext.Services` 解析类型化服务，打开桌面托管窗口。Server 客户端使用在 `Bootstrapper` 注册且含诊断、`AcceptLanguageHandler` 和认证的类型化 `HttpClient`；Proxy 必须采用 `IProxyRepository`/`RemoteProxyRepository`，视图/视图模型不能直接创建 `HttpClient` 或连接 Mihomo。

使用 CommunityToolkit.Mvvm 的 `ObservableObject`、`ObservableProperty`、`RelayCommand`。工作区参照 Docker 的左侧导航、`ContentControl`、独立 AXAML 页；禁止巨大单页或隐藏标签页。模态框使用 `AppContext.ShowDialogAsync<TResult>`，不得使用原生 OS 对话框。主题使用 `ThemeService` 的动态语义资源，禁止 Mihomo/Clash 专用调色板或硬编码颜色。所有 Proxy 键加入 en-US、zh-CN、ja-JP；动态文本响应 `LanguageChanged`。

代理状态（运行时库存、活动配置文件、恢复标记、网络快照、控制器配置、操作/审计历史、安全状态）是主机全局的，不能放到工作区偏好或用户行中；用户/会话身份只用于审计归因。选择主机全局迁移方案后，原始 YAML、备份和运行时资产写入受保护平台路径；保留完整引擎 YAML 加 RemoteOS 覆盖层，不尝试完整 DTO 重写。

## 必需组件图与实施阶段

1. **阶段 1：领域与协议。** 在 `Shared/RemoteOS.Protocol/Proxy` 新增引擎/平台能力、运行/运行时/TUN/健康/操作状态、稳定问题码、配置文件、运行时、组、连接、日志、DNS、恢复和路由常量；新增引擎无关 Server 接口及序列化/问题码测试。没有 UI、下载、服务或 TUN 激活。
2. **阶段 2：Mihomo 适配器。** 实现 Server 专用 `MihomoEngine` 和仅本机控制器客户端，映射为中立协议、保护生成的控制器密钥、验证配置、脱敏有界日志；客户端无控制器访问。
3. **阶段 3：运行时与原生服务。** 以 FRP 的验证/暂存/版本/回滚经验实现托管/外部 Mihomo 运行时，但使用代理路径；新增强类型运行时/服务/配置操作及 Windows/Linux 服务集成。首次安装和健康检查关闭 TUN。
4. **阶段 4：配置文件与配置事务。** 实现主机全局元数据、活动配置、原始 YAML 读写、验证、临时写入、备份、提交、重载、健康检查与回滚；避免完整 YAML 可视模型。
5. **阶段 5：TUN 安全。** 实现能力检测、活动会话路由捕获、系统绕过、出站接口、路由/DNS 快照、恢复标记、事务启停、启动恢复评估、回滚与紧急禁用。先完成 Server 测试，证明管理流量在激活时可达，才暴露 UI/API。
6. **阶段 6：API 与授权。** 在 `Program.cs` 注册服务并添加 `MapProxyEndpoints`；应用认证和危险操作策略；运行时/TUN/恢复变更需要幂等键与持久操作 ID；审计操作人/会话/主机/引擎/配置文件/结果/关联 ID，且无敏感内容。
7. **阶段 7：Avalonia。** 注册类型化仓库、清单和内置应用；提供概览、配置文件、代理、连接、DNS、日志、设置等按能力而非引擎名称划分的页面；使用现有主题、本地化、托管模态框。
8. **阶段 8–10。** 完成审计/密钥/控制器安全和授权测试，在真实 Windows 与 Ubuntu 执行 TUN 集成测试证明管理路径存活，最后完善代理架构、Mihomo、TUN、恢复、安全、安装和故障排查文档。

## 风险与阶段 0 完成条件

当前没有跨平台提权工作流；阶段 3 必须先设计受约束部署/类型化辅助边界，不能收集 OS 密码或成为通用执行器。平台路径和网络路由/DNS 抽象是新增基础设施，`INativeServiceAdapter` 过窄，现有操作存储需要明确的复用/抽取决定，且必须新增主机范围 TUN 锁和恢复标记。FRP 仅是安全模式参考；其常驻子进程设计违反 Mihomo 服务要求。阶段 1 仅可诊断防火墙，不得直接写入 UFW、nftables、iptables 或 Windows 防火墙策略。规范中大写错误示例与仓库小写点分规范冲突，阶段 1 必须选择一种稳定公共格式并始终使用。

阶段 0 已完成：阅读完整规范；检查解决方案、Server 组合和持久化；检查权限、提权、服务、平台边界、API、操作、审计、密钥存储和流式传输；检查 Avalonia MVVM、类型化客户端、工作区、模态框、主题和本地化模式；识别可复用基础设施、缺失组件和冲突；除本文档外未修改产品代码。只有接受本文档后才能开始阶段 1。
