# RemoteOS 应用权限模型与项目重构（Goal 评审草案）

> 状态：**评审中，未获实施授权**
>
> 建立日期：2026-09-05
>
> 前置规范：[权限模型与项目重构规范](../RemoteOS%20权限模型与项目重构规范.md)
>
> 关联但独立：[跨平台特权操作与 Helper（Goal 执行版）](./RemoteOS.PrivilegedOperations.Goal.md)

本文将主规范转化为可执行的 Goal 方案，并先给出是否值得实施的决策依据。它不授权任何代码迁移；只有通过 Goal 0 的决策门，才能进入后续 Goal。

## 1. 评审结论

### 1.1 可行性

**可行，但不是一次普通的权限重构，而是一次应用运行时、包信任和 Server 调用链的安全边界重构。**

主规范要求外置 App 的身份由受认证的 IPC 会话决定，且 Server 进行最终授权。要满足这一要求，外置 App 必须在 Shell 进程之外运行，并且只能通过 Host/Broker 获得能力 API。仅替换 `IAppPermissionManager`、增加 `AppIdentity` 或给 Manifest 增加字段均不足以建立这个边界。

### 1.2 复杂度

若目标是“允许不可信或第三方 App，并以权限模型限制它们”，复杂度评估为 **很高**。新增复杂性集中在：

| 领域 | 必须新增或重做的能力 | 复杂度 |
| --- | --- | --- |
| App 隔离 | 外置 App 独立进程、受限启动、崩溃/升级/卸载生命周期、Host IPC | 很高 |
| 身份与信任 | 包签名、发布者信任、内置注册表、开发模式身份、撤销与升级策略 | 高 |
| 授权 | Scope、Grant、显式拒绝、默认 Policy、即时撤销、缓存失效与审计 | 高 |
| Server Broker | 不信任客户端 AppId；以 Broker 会话签发且校验 App-bound capability | 很高 |
| 业务迁移 | 文件、凭据、Git、Docker、网络、服务等领域 API 的封闭契约与测试 | 很高 |
| 运维与测试 | Windows/Linux 沙箱差异、IPC ACL、安装/更新、攻击性测试矩阵 | 高 |

若只目标为“为当前受信任的内置 App 整理声明和默认策略”，复杂度是 **中等**，但它只能提升可维护性与可见性，不能提供对恶意外置代码的隔离安全收益。

### 1.3 必要性

| 产品方向 | 建议 | 原因 |
| --- | --- | --- |
| 近期仅运行官方内置 App，开发模式只供本机开发 | **暂缓完整重构**；先完成下文的 Goal 0 和轻量治理 | 当前风险模型仍是“受信任代码”；完整隔离的成本明显高于近期收益。 |
| 要发布第三方/合作伙伴应用，但可要求签名、审核和可信发布者 | 分两步实施：先建立包身份、Policy、审计；将合作伙伴 App 明确定义为 `Trusted` | 签名降低供应链风险，但不应宣传为对恶意代码的沙箱。 |
| 要允许普通第三方、不可信或用户自行安装的 App | **完整方案是必要前置条件** | 否则 Manifest、UI 授权和 AppId 都可以被进程内代码绕过或伪造。 |

因此本项目的推荐决策是：**先做 Goal 0；在产品明确承诺“不可信第三方 App”前，不启动完整迁移。**

## 2. 当前基线与不能忽略的缺口

仓库已有可复用基础，不能另起一套平行系统：

- `Framework/RemoteOS.Core/Applications/AppPermissions.cs` 已有稳定、较粗粒度的 `server.*` 权限目录；`ApplicationManifest` 已可声明 `RequestedPermissions`。
- Client 已有 `JsonAppPermissionManager`、授权 UI、`ExternalAppContextFactory` 和外置 App 的能力型 SDK 表面。
- 内置 App 已有稳定 `remoteos.*` 身份；开发包禁止占用该命名空间。
- `RemoteOS.PrivilegedHelper`、`IHostElevationSessionStore` 处理的是**宿主 OS 提权**，应继续独立于 App Capability 授权。

但当前实现不能满足主规范的安全边界：

1. 开发包通过 `DeveloperPackageManager` 的可收集 `AssemblyLoadContext` 在 Client/Shell 同一进程内执行。程序集加载隔离不是权限或资源隔离；包代码仍可调用 .NET 的文件、网络、反射和进程 API。
2. Client 本地的 `Granted` / `Denied` 决策可改善 UI，但不是 Server 可以信任的最终授权来源。
3. `AppCapabilityEndpoints` 当前接收客户端提交的 `AppId` 与 file scope，并使用用户 JWT 签发 token；它没有由受认证 Broker 绑定的 AppIdentity。因此不得把它扩展为更多敏感能力。
4. `ApplicationManifest` 目前没有发布者、签名、安装来源或受 Host 验证的 TrustLevel；`remoteos.*` 命名保留不是完整的信任证明。
5. 内置 App 大多仍经 `AppContext.Services` 取得具体 Client 服务；强制立即改写所有内置应用会造成高回归风险，且对受信任内置代码没有等价的安全收益。

## 3. 不变边界与术语

### 3.1 三种不能混淆的授权

```text
App Capability
  App 是否可请求某个 RemoteOS 领域能力

Host Elevation
  已获准的业务操作是否还需要 root / LocalSystem / Administrator

RemoteOS User Authorization
  当前 RemoteOS 用户是否可访问其 Workspace、会话或服务端资源
```

三者按调用链分别检查。`server.files.write` 不等于管理员权限；获得五分钟的 `FileWrite` 提权也不等于 App 可以调用任意 Server API。

### 3.2 目标调用链

```text
External App process
  → authenticated App Broker session (AppIdentity fixed at launch)
  → permission evaluator (manifest + deny + grant + policy + scope)
  → capability-specific Client/Server adapter
  → RemoteOS user authorization
  → optional Host Elevation capability
  → OS / Helper
```

内置 App 可以使用同一 SDK contract 的 in-process transport；但其默认授权来自 Host-owned Policy，绝不能以 `IsBuiltIn => AllowEverything` 取代评估。

### 3.3 明确非目标

- 不把 `RemoteOS.PrivilegedHelper` 变成 App Broker，也不向外置 App 暴露它。
- 不新增 `process.execute`、任意 shell、任意文件路径、任意 HTTP 代理或原始 JWT 导出能力。
- 不在第一阶段移动所有项目目录，或将每个 Client 服务机械拆分为新程序集。
- 不把当前 `AssemblyLoadContext` 宣传为沙箱。

## 4. 目标架构（最小可行安全版本）

### 4.1 身份与信任

Host 在安装/启动时根据 package manifest、签名、发布者、安装来源和内置注册表生成不可由 App 自报的：

```csharp
public sealed record AppIdentity(
    AppId AppId,
    string PublisherId,
    AppTrustLevel TrustLevel,
    string PackageInstanceId);
```

初始信任级别只保留 `BuiltIn`、`Trusted`、`ThirdParty`、`Development`。`Development` 必须显式启用，默认没有敏感 Server capability 的自动授权。包更新造成权限新增时，现有 Grant 不得自动覆盖新 capability。

### 4.2 单一权限评估器

在 Core/AppModel 建立单一的纯领域评估器，而不是把 UI、存储和网络逻辑揉在一起：

```csharp
ValueTask<PermissionDecision> EvaluateAsync(
    AppIdentity app,
    CapabilityRequest request,
    CancellationToken cancellationToken = default);
```

固定顺序为：未知/未声明拒绝 → 管理员或用户显式拒绝 → 有效 Scope Grant → Host Policy 默认值（Allow / Prompt / Deny）。Scope 先只支持能安全规范化的 `Path`、`Service`、`Repository` 和精确资源 ID；其余类型在有领域验证器前不得加入。

### 4.3 Broker 与 Server

- 外置 App 不取得用户 JWT、不直接调用 `AppCapabilityEndpoints`，也不提交可选择的 AppId。
- Host 在建立 IPC 会话时固定 AppIdentity；每个请求从会话取得身份。
- Host/Broker 代表 App 调用 Server 时使用短期、受众为 Broker 的 app-bound token，或使用等价的 mTLS/本机认证通道；Server 必须验证 app、user、device/workspace、capability、scope 与过期时间。
- Server 端对路径、资源 ID、大小和所有敏感参数重新验证。Client 预检查只用于体验。

### 4.4 隔离策略

完整实施选择“外置 App 子进程 + 每包独立 IPC 连接”。Windows 和 Linux 的实际受限启动机制允许不同，但验收结果必须相同：外置 App 无法读取 Shell 的 token/进程内服务，无法直接获得 Client 用户的任意文件、网络或进程能力，也无法伪装另一个 App 的 Broker 会话。

若平台无法在首版可靠地给出该隔离，则该平台只允许 `BuiltIn` 和经过明确风险接受的 `Trusted` 包；不能以 `ThirdParty` 名义发布。

## 5. Goal 执行计划

每个 Goal 完成后均保持 `dotnet build RemoteOS.sln -c Debug` 通过，并新增相应的单元/集成测试。任何 Goal 未达到验收标准时，不扩展外置 App 的敏感能力目录。

### Goal 0：产品决策、威胁模型与现状清单（无业务改动）

**工作**

- 决定目标 App 类别：仅 BuiltIn、受审核 Trusted，或不可信 ThirdParty。
- 清点所有现有外置 App SDK 能力、所有直接 `HttpClient`/JWT/API 访问点、Client 本地敏感资源与 Server 端敏感 endpoint。
- 冻结 v1 权限 ID（优先复用现有 `server.*` 名称），定义每个 ID 的 BuiltIn/Trusted/ThirdParty 默认策略。
- 选择 Grant 所属范围（至少 user + device；涉及 Server 资源时明确 workspace/host 语义）、撤销语义、审计字段和升级兼容策略。
- 与 Privileged Helper Goal 对齐 capability 映射，保证没有“App 授权即 OS 提权”的捷径。

**验收 / 决策门**

1. 产品负责人书面确认目标 App 信任模型。
2. 若不是 `ThirdParty`，停止在 Goal 0，按第 6 节的轻量治理方案实施；不得以安全重构名义启动进程隔离项目。
3. 若是 `ThirdParty`，批准以下安全基线：独立进程、受认证 IPC、包签名/信任根、Server app-bound token、Windows/Linux 验证环境。

### Goal 1：模型收敛与兼容层（不改变既有默认体验）

**工作**

- 在现有 `RemoteOS.Core.Applications` 附近增加 `AppIdentity`、`AppTrustLevel`、`PermissionScope`、`PermissionGrant`、`GrantSource` 与纯 `IPermissionEvaluator`。
- 将现有 `JsonAppPermissionManager` 迁为该评估器的本地存储适配层；保留旧 `Granted`/`Denied` 文件的读取迁移。
- 加入 Host-owned BuiltIn Policy Registry；为所有已注册内置 App 补齐 manifest 声明，但默认 Policy 维持当前产品行为且不弹窗。
- 为 manifest 新增发布者/包实例元数据的向后兼容读取；外置包依然不得自行设定 TrustLevel。

**验收**

- 同一权限在内置和开发包路径都经同一评估顺序；显式 deny 覆盖 grant 和默认策略。
- 不增加新的 Server endpoint，不扩大已有外置 App 权限。
- manifest 新权限在升级后为未授权状态，且有测试覆盖。

### Goal 2：Broker 垂直切片与隔离原型（只做文件只读）

**工作**

- 实现一个外置 App 子进程 Host、每包 IPC 会话认证和不可伪造的 AppIdentity 绑定。
- 将一个最小开发包迁到新 Host；SDK 只暴露 scoped `server.files.read` 的目录列举/读取能力。
- 替换或封闭当前可由客户端提交 `AppId` 的 file-capability 签发路径；Server 改为接受 Broker 颁发的 app-bound 凭据并验证 scope。
- 验证子进程无权读取 Shell session/token，并不能直接调用受保护 Server endpoint。

**验收**

- AppId 替换、篡改 scope、重放过期请求、伪造 IPC 客户端和未经授权的 HTTP 调用均 fail-closed。
- 撤销 `server.files.read` 后，已存在的 Broker capability 在确定的短 TTL 内失效，且不能继续续期。
- 在 Windows 与 Linux 的隔离 VM 中通过同一攻击性测试集；任何一端失败则该端不宣称支持 ThirdParty。

### Goal 3：包信任、安装与开发模式迁移

**工作**

- 为 BuiltIn、Trusted、ThirdParty、Development 定义 package verifier 和安装策略；生产包验证签名、发布者与来源，开发包要求开发者模式和本机配对。
- 改造包加载、更新、卸载和窗口/子进程生命周期；实现失败恢复、版本回滚和信任撤销。
- 让 Settings 显示信任来源、声明、实际 grant 和拒绝原因；敏感权限使用 scope-aware 提示。

**验收**

- 未验证包不能获得 BuiltIn/Trusted 身份；`remoteos.*` 命名、manifest 字段或缓存文件都不能提升信任级别。
- 更新包新增 capability 不自动授权；禁用开发者模式会停止/禁用 Development 包。
- 包崩溃、IPC 断开、升级中断和卸载不会遗留有效 Broker session 或 capability。

### Goal 4：能力 API 的领域化迁移

**工作**

- 按风险和可封闭程度迁移：文件（read/write）→ app settings/媒体 → metrics → Git repository → Docker/Web Server/服务。
- 每个领域定义窄接口、可规范化 Scope、输入限制、审计和 Server 端授权；不用万能 `IFileSystemApi` 或 `IProcessApi` 兜底任意资源。
- 内置 App 逐个迁移到同一 contract 的 in-process transport；保留适配层，避免全量 Big Bang。

**验收**

- 每新增一种外置能力都有：manifest 声明、Policy、grant/revoke、Broker/Server 双端校验、审计和测试。
- 不存在外置 App 可取得通用用户 JWT、`IServiceProvider`、任意 HTTP client 或特权 Helper transport 的路径。

### Goal 5：高风险能力与运维收尾

**工作**

- 最后评估 Credential、网络访问/监听、Git 网络、Docker/服务管理等高风险能力；优先提供领域工作流而非通用网络/进程 API。
- 建立包密钥轮换、撤销、审计检索、故障排查、平台支持矩阵和回归测试。
- 对无法形成封闭 contract 的需求只提供 `manual-host-action-required`，而不是绕过 Broker。

**验收**

- 威胁模型覆盖恶意包、恶意/被盗用户 token、恶意 IPC client、路径穿越/链接、权限更新、撤销、降级、包篡改和 Broker 崩溃。
- 所有高风险成功调用可关联 app identity、用户、资源 hash、capability、grant 来源、操作 ID 与结果；不记录 token、密码、文件内容或密钥。

## 6. 若暂不支持不可信第三方 App：轻量治理方案

这是近期的推荐路线，预计不会引入运行时/IPC 大重构：

1. 明确文档：当前开发包是受信任开发代码，权限 UI 是产品功能门控而非恶意代码沙箱。
2. 复用现有 `AppPermissions` 和 manifest，为全部内置 App 补全声明，并引入 Host-owned BuiltIn 默认策略清单。
3. 将 `AppCapabilityEndpoints` 标注为内部过渡接口；不为其新增高风险 scope，也不允许开发包直接获得新敏感 Server API。
4. 继续优先完成 `RemoteOS.PrivilegedOperations.Goal.md`：它直接减少 Server 以高权限运行的风险，且不依赖完整 App 沙箱。
5. 在第三方 App 立项时，从本文件 Goal 0 重新开始，不把本地 `Granted` 状态误用为服务端安全授权。

## 7. 开始实施前必须确认的事项

1. RemoteOS 是否在未来两个发布周期内承诺支持不可信第三方 App？
2. 若支持，是否接受每个外置 App 使用独立进程、受限 IPC 和跨平台 VM 验证的产品/运维成本？
3. 生产包是否必须签名；谁拥有发布者信任根、密钥轮换和撤销职责？
4. 用户 grant 是按 Client 设备、RemoteOS 用户、Workspace 还是 Server 主机保存？不同资源类型是否允许不同范围？
5. 哪些能力明确不向 ThirdParty 开放（建议首版包括凭据原文、任意网络、端口监听、进程执行、任意服务管理和 Host Elevation）？

未回答这些问题前，允许完成 Goal 0 的只读审计与文档，但不开始 Goal 1 之后的实现。
