# RemoteOS Registry

> **状态：第一阶段已实现。**

## 定位与边界

内置应用 `remoteos.registry` 浏览服务器 schema 明确允许的配置型期望状态及其同步状态。它不提供宿主 Windows Registry、任意 SQLite 表、机密、会话或高风险命令的入口。

## 流程与信息架构

应用采用左侧键树、右侧值表和底部编辑器。用户可新建、修改和删除自己作用域内的逻辑注册表值；刷新从服务端重新读取。同步、版本和重启状态属于内部实现，不展示给用户。

## 边界、存储与升级

Protocol 定义 `RegistryEntryDto`、状态枚举及 REST 路由；Client 使用 typed `HttpClient`；Server 仅根据 JWT subject 查询本人的条目。SQLite `registry_entries` 的复合主键为 `(UserId, Scope, ScopeId, Path, Name)`。服务启动时把现有 Workspace 的终端外观、桌面偏好与浏览器设置导入为 `Synced` 值，已有条目不会覆盖。

## 平台、安全与验收

该应用和 API 在 Windows、Ubuntu 共享相同的托管实现，不访问任何 OS 注册表。路径由代码 schema 白名单控制，读取以 JWT 用户 ID 为租户边界；未认证、未知 scope 与其他用户数据不可访问。验收包括：两位用户的列表互不包含对方数据；既有 Workspace 第一次启动后有三个 `Synced` 项；重复启动不改变 revision；应用可在离线或未登录时给出失败提示。

后续阶段可补充编辑历史、乐观并发、`RegistryWriter` 与 `RegistrySyncWorker`；现有配置 PUT 会按配置域迁入该写入链路。
