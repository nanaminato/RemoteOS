# RemoteOS Authentication & Identity Design

> 本文档定义 RemoteOS 登录系统、用户身份模型以及 Linux 用户集成方式。
>
> RemoteOS 不实现独立操作系统级用户体系，而是在 Linux 用户系统之上提供统一登录体验和 Workspace 管理。
>
> RemoteOS 的定位是云原生桌面操作系统：Server 端运行于 Linux，复用 Linux 用户与权限体系；Client 端提供跨平台桌面 Shell。主要应用场景为个人服务器、小型团队服务器的桌面化管理。
>
> 本文档属服务端身份层设计，将随系统逐步实现（当前 `RemoteOS.Server` 尚为占位）。
>
> 相关文档：
>
> - [`RemoteOS.Architecture.md`](./RemoteOS.Architecture.md)
> - [`RemoteOS.Workspace.md`](./RemoteOS.Workspace.md)
> - [`RemoteOS.Security.md`](./RemoteOS.Security.md)
> - [`RemoteOS.md`](./RemoteOS.md)

---

## 1. 设计目标

RemoteOS 面向：

- 个人服务器用户
- 网站管理员
- 小型团队服务器

而不是：

- SaaS 多租户平台
- 云计算租户隔离系统

因此 RemoteOS 不重新设计用户、权限、文件系统。

核心目标：

> 提供类似 Windows / macOS 的服务器桌面操作体验，同时复用 Linux 已有用户和权限体系。

---

## 2. 用户模型

RemoteOS 用户模型：

```text
RemoteOS Identity
    |
    v
Linux User
    |
    v
Operating System
```

**RemoteOS Identity** 负责：

- 登录体验
- Workspace 管理
- Session 管理
- Device 管理

**Linux User** 负责：

- 文件权限
- Process 权限
- Service 权限
- sudo 权限

---

## 3. Identity Provider

RemoteOS 通过 Identity Provider 获取用户身份。

MVP 阶段：

```text
Linux Identity Provider
```

流程：

```text
RemoteOS Login
    |
    v
Linux Authentication
    |
    v
Linux User Context
```

未来可扩展：

```text
RemoteOS Identity
    |
    +-- Linux User Provider
    +-- LDAP Provider
    +-- Cloud Identity Provider
```

---

## 4. 登录流程

### 4.1 Client 登录

```text
RemoteOS.Client Start
    |
    v
Authentication Request
    |
    v
RemoteOS.Server
    |
    v
Linux Authentication
    |
    v
Create Session
    |
    v
Load Workspace
    |
    v
Desktop Ready
```

---

## 5. User

User 表示 RemoteOS 身份对象，与 Linux User 建立映射。

例如：

```text
RemoteOS User
  Id:       550e8400
  Username: alice
  Mapping:  Linux User: alice
```

- RemoteOS **不保存** Linux 密码。
- 密码认证由 Linux PAM 处理。

---

## 6. Workspace

一个 User 默认拥有一个 Workspace。

关系：

```text
User
  |
  v
Workspace
  |
  v
Session
  |
  v
Device
```

Workspace **不等同于** Linux Home。

Workspace 保存：

- Desktop State
- Application State
- RemoteOS Preference
- Session State
- Device Binding
- Linux Identity Context

Linux Home（`/home/alice`）保存：

- User Files
- Application Data
- System Files

---

## 7. Permission Model

RemoteOS 不实现独立权限系统。所有实际权限由 Linux 管理。

模型：

```text
RemoteOS Application
    |
    v
Linux User Context
    |
    v
Linux Permission System
    |
    v
Operating System
```

Linux 已提供：User、Group、File Permission、sudo、Capability。RemoteOS 不重复实现。

---

## 8. 权限提升

当用户执行需要更高权限的操作时，RemoteOS 请求 Linux 权限提升。

例如：

```text
Delete /etc/nginx
    |
    v
Linux Permission Check
    |
    +-- Success
    |
    +-- Permission Denied
            |
            v
      Request Authentication
            |
            v
          sudo
            |
            v
        Execute
```

类似：Windows UAC、macOS Administrator Authentication。

---

## 9. Database Design

RemoteOS 数据库只保存 RemoteOS 状态。

**保存**：RemoteOS User、Linux User Mapping、Workspace、Session、Device。

**不保存**：Linux Password、Linux Permission、ACL。

数据库：SQLite / PostgreSQL。首批实现推荐 SQLite。

---

## 10. User Table

Table: `users`

| 字段 | 类型 | 说明 |
|------|------|------|
| id | uuid | RemoteOS User ID |
| username | string | RemoteOS 用户名 |
| linux_username | string | Linux 用户名映射 |
| created_at | datetime | 创建时间 |
| last_login_at | datetime | 最后登录时间 |

Example:

```text
id:            550e8400
username:      alice
linux_username: alice
```

---

## 11. Workspace Table

Table: `workspace`

| 字段 | 类型 | 说明 |
|------|------|------|
| id | uuid | Workspace ID |
| user_id | uuid | 所属用户 |
| name | string | Workspace 名称 |
| state | string | 状态 |
| created_at | datetime | 创建时间 |

Example:

```text
Alice Workspace
State: Running
```

---

## 12. Session Table

Table: `session`

| 字段 | 类型 | 说明 |
|------|------|------|
| id | uuid | Session ID |
| workspace_id | uuid | Workspace |
| device_id | uuid | 设备 |
| created_at | datetime | 创建时间 |
| last_active_at | datetime | 最后活动 |
| status | string | 状态 |

状态：`Connected` / `Disconnected`

---

## 13. Device Table

Table: `device`

| 字段 | 类型 | 说明 |
|------|------|------|
| id | uuid | Device ID |
| name | string | 设备名称 |
| platform | string | 平台 |
| client_version | string | 客户端版本 |
| last_login_at | datetime | 最后连接 |

---

## 14. Linux Integration

RemoteOS Server 运行于 Linux 之上：

```text
remoteos-server
    |
    v
Linux System
```

RemoteOS **不创建** `/etc/passwd`、`/etc/shadow`。Linux 用户由系统管理。

---

## 15. RemoteTerminal

RemoteTerminal 使用当前 Linux 用户执行。

模型：

```text
RemoteTerminal
    |
    v
PTY
    |
    v
Shell
    |
    v
Linux User
```

例如：

```text
RemoteOS User: alice
Terminal:      bash -- alice
```

sudo：

```text
sudo command
    |
    v
Linux Authentication
    |
    v
Execute
```

---

## 16. 实现路线

首批实现：

- Linux User 登录
- Workspace 创建
- Session 管理
- Device 管理
- Linux Identity Mapping

后续逐步完善：

- 独立密码系统（不实现，复用 Linux）
- RemoteOS ACL / Role（不实现，复用 Linux 权限）
- 多租户隔离（不实现）

---

## 17. AI Agent Rules

实现登录系统时：

**必须**：

- 使用 Linux User 作为最终执行身份
- 保留 Workspace 模型
- 保留 Session / Device 模型
- 不复制 Linux 权限体系

**禁止**：

- 创建新的 Linux 替代用户体系
- 创建 RemoteOS ACL 系统
- 将 Workspace 等同于 Linux Home
- 实现 SaaS 多租户权限模型
