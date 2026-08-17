# RemoteOS 内置应用开发约束

> 本文是所有现有与后续内置应用的强制设计基线。新应用在编码前必须建立自己的设计文档，并逐项说明如何符合本约束；与已有文档冲突时，以安全、跨平台和协议边界为优先，随后修订冲突文档。

## 1. 架构与项目边界

1. 内置应用实现 `IRemoteApplication` 或继承 `RemoteApplicationBase`，使用 `remoteos.<name>` App ID；客户端 UI 通过 `AppContext.ShowWindow` 创建，不自行管理原生窗口。
2. 客户端负责 Avalonia UI、本地交互状态和显示；访问远程宿主机、账户、文件、进程、服务、容器或持久化数据时，必须经过 `RemoteOS.Server` 的授权 API。客户端不得直接访问服务器文件、Docker socket、named pipe、操作系统服务或数据库。
3. Client/Server 通信必须在 `Shared/RemoteOS.Protocol` 定义 DTO、枚举、路由/Hub 常量和序列化约定。业务代码禁止硬编码 API 字符串或复制 DTO；Protocol 保持零业务依赖。
4. Server 的系统能力先定义接口，再实现 Ubuntu/Linux 与 Windows Provider。`Program.cs` 只做运行时选择；Endpoint、Client、ViewModel 和共享 DTO 不出现 `OperatingSystem.Is...`、P/Invoke、`/proc` 路径、PowerShell 或 shell 命令。
5. 使用构造函数注入与 `Microsoft.Extensions.DependencyInjection`。不引入 Service Locator、反射扫描、全局可变单例或由 UI 直接创建服务器客户端。
6. 先在 `Windows Server Test` 验证所需 Windows 原生 API，再进入 Server Provider；同一能力必须在 Ubuntu 上拥有等价实现、明确降级或明确“不支持”状态。

## 2. 设计文档先行

每个内置应用在实现前新增 `docs/applications/RemoteOS.<App>.md`，至少包含：定位/非目标、用户流程和 UI 信息架构、Client/Server/Protocol/存储边界、Ubuntu 与 Windows 支持矩阵、权限与威胁模型、错误/离线/断线行为、持久化与升级策略、实施拆分、验收矩阵及外部参考链接。

功能状态只允许使用“已实现”“设计中”“计划中”“不支持”，并在 [`RemoteOS.md`](../README.md) 的应用表和文档索引中同步。不得把设计当作已实现功能描述。

## 3. 国际化与本地化（强制）

- 所有用户可见文字使用稳定 key，经 `LocalizationService.Get(key, englishFallback)` 或等效绑定获取；禁止在 AXAML、ViewModel、Dialog、通知、验证和错误映射中硬编码自然语言。
- 每个新 key 同时加入 `en-US`、`zh-CN`、`ja-JP` 对应语言包，key 层级按应用名称隔离，例如 `docker.container.start`、`process_guardian.validation.path_outside_root`。英语 fallback 不是免翻译理由。
- 可见日期、时间、数字、大小、百分比、排序和文本方向使用当前 `CultureInfo`；协议传输 ISO-8601 UTC 时间、数值和稳定枚举，不传递本地化文案。
- 后端返回稳定问题码、字段名和可选安全诊断；客户端负责映射本地化提示。不得把 OS 或第三方原始错误原样展示给用户。
- 支持语言切换：所有由 ViewModel 管理的标题、按钮、状态、空态、工具提示、菜单、确认文案和动态错误应在 `LanguageChanged` 后更新。新应用须有语言切换 smoke test。

现行语言包格式与动态刷新模式见 [`RemoteOS.Localization.md`](../desktop/RemoteOS.Localization.md)。

## 4. 跨平台（Windows + Ubuntu）

| 规则 | 必须做法 |
|---|---|
| 支持声明 | 文档列出每个平台的最低环境、能力差异、安装/检测/卸载路径和已验证版本；未验证即标为“设计中”或“不支持”。 |
| OS 抽象 | Server 接口与 Windows/Linux Provider 分离。返回统一 DTO，允许以显式 `CapabilityStatus` 表达缺失。 |
| 路径与命令 | 传递结构化路径、参数数组和环境字典；不能拼接 shell 字符串。UI 不假设盘符、`/`、注册表或 `/proc`。 |
| 服务生命周期 | Ubuntu 使用 systemd 适配器，Windows 使用 SCM/ServiceController 适配器；不能将一种平台的语义悄悄伪装成另一种。 |
| 权限 | 使用当前宿主身份和 OS 原生授权；不得收集 sudo/UAC/服务账户密码或自行提权。 |
| 可选能力 | GPU、容器模式、特定驱动等以探测结果呈现；没有能力时给本地化说明和安全替代，而非崩溃。 |

## 5. 安全、权限与危险操作

1. 默认最小权限。当前 RemoteOS 的目标部署是单台、由网站管理员维护的服务器：第一方内置应用不采用 `AppPermissions` 做细粒度授权，已登录的管理员可使用全部内置管理功能。此约定不取消 API 认证、宿主 OS 权限检查、危险操作确认或审计；外置应用和未来多用户模式仍必须使用稳定的 `AppPermissions` 与服务端授权。
2. 不存储宿主 OS 密码、sudo 密码、Docker socket、访问令牌、私钥或明文 secret。敏感配置只保存 OS 安全存储引用；UI、日志、审计、遥测和异常都须脱敏。
3. 删除、覆盖、强制终止、升级、安装、网络暴露、特权执行、修改开机启动等操作必须先做预检，展示影响对象与不可逆性，并取得与风险匹配的明确确认。批量操作必须列出目标。
4. “权限不足”返回可操作的提示，不自动使用管理员身份重试。需要管理员权限的安装或系统操作采用宿主 OS 受确认的提权委托。
5. Endpoint 验证认证、管理员运行状态、输入范围、路径规范化与速率/大小限制；客户端校验只用于体验，不能替代服务器校验。当前单机管理员模式不要求按 User/Workspace 隔离宿主机级资源；进入多用户模式前必须补齐该隔离与授权模型。
6. 审计记录操作者、时间、目标、动作、确认、结果和关联操作 ID，但不记录秘密或敏感内容。

## 6. 网络、并发、状态与可观测性

- 使用 typed `HttpClient`，每请求根据 `IAuthSession` 构造安全 URI 与 Bearer token；不得修改共享 `HttpClient.BaseAddress`。流式通信使用已定义的 SignalR 契约。
- 所有可变更请求使用幂等键；长任务返回 `OperationId`，可取消、可断线重连，并向 UI 提供阶段/进度/稳定问题码。禁止长时间占用 UI 线程。
- UI 刷新必须有取消令牌、重入保护、可见性/窗口关闭清理和合理间隔。Server 返回的实际状态优先于乐观 UI 状态。
- 每项存储数据指定真源、宿主机或用户/Workspace 作用域、迁移和保留期。运行时事实数据（如进程、容器）不复制为永久真源。
- 日志、终端、上传下载和事件流实施分页/游标、长度与速率上限、断线清理和敏感值清洗。

## 7. 质量门禁

合并前至少完成：Protocol 序列化与路由测试；Server Provider 的 Ubuntu/Windows 单元或集成测试；授权、输入验证、脱敏、危险确认的安全测试；三语言资源完整性与切换 smoke test；窗口关闭/断线/取消测试；以及每个已声明平台的手工验收记录。

新增依赖需说明许可证、平台兼容性、离线/升级影响和是否替代已有能力。设计完成并不豁免代码审查、`dotnet build`、格式检查和实际平台验证。
