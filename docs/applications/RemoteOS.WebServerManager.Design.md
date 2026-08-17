# RemoteOS WebServerManager / Nginx 集成设计

## 1. 设计背景

RemoteOS 是一个仅管理当前主机的服务器管理程序，后端采用 .NET 10，支持 Windows 和 Linux。

在证书管理器设计中，已经确定以下原则：

- 证书签发与证书部署分离。
- RemoteOS 自身作为 ACME Client。
- Nginx / IIS / Apache 只作为证书部署和 HTTP-01 暴露路径的可选集成对象。
- 不要求 RemoteOS 自己长期占用 TCP 80。
- 不应把 Nginx 作为 RemoteOS 的强依赖。

因此，Web Server 管理模块应设计为一个通用的 Web Server 抽象层，其中 Nginx 只是第一个完整实现的 Provider。

---

## 2. 核心设计目标

### 2.1 不强依赖 Nginx

RemoteOS 的核心功能必须可以在以下场景正常工作：

- 没有安装 Nginx。
- 只有 IIS。
- 只有 Apache。
- 用户自行维护现有 Nginx。
- RemoteOS 仅使用 Kestrel 提供 HTTPS。
- 后续接入 Caddy、OpenResty 等其他 Web Server。

因此，不建议把核心架构命名或设计为：

```text
RemoteOS
   ↓
NginxManager
   ↓
所有 Web 功能
```

推荐：

```text
RemoteOS
   ↓
WebServerManager
   ↓
IWebServerProvider
   ├── Nginx
   ├── IIS
   ├── Apache
   └── ...
```

---

## 3. 总体模块结构

推荐结构：

```text
RemoteOS.Server
│
├── WebServer
│   │
│   ├── IWebServerManager.cs
│   ├── WebServerManager.cs
│   │
│   ├── Models
│   │   ├── WebServerInstance.cs
│   │   ├── WebServerCandidate.cs
│   │   ├── WebServerCapabilities.cs
│   │   └── WebServerManagementMode.cs
│   │
│   ├── Providers
│   │   ├── Nginx
│   │   │   ├── NginxWebServerProvider.cs
│   │   │   ├── NginxDetector.cs
│   │   │   ├── NginxRuntime.cs
│   │   │   ├── NginxConfigurationManager.cs
│   │   │   ├── NginxInstaller.cs
│   │   │   ├── NginxSiteProvider.cs
│   │   │   ├── NginxConfigRenderer.cs
│   │   │   └── NginxCertificateDeployer.cs
│   │   │
│   │   ├── IIS
│   │   └── Apache
│   │
│   └── Deployment
│       └── CertificateDeploymentCoordinator.cs
│
├── Certificate
│   ├── CertificateManager.cs
│   ├── Acme
│   ├── Challenges
│   ├── Storage
│   └── Renewal
│
└── Platform
    ├── Windows
    └── Linux
```

---

## 4. Web Server 的三种管理模式

建议不要仅使用“已安装 / 未安装”区分状态，而是增加管理模式：

```csharp
public enum WebServerManagementMode
{
    External,
    Integrated,
    Managed
}
```

### 4.1 External

RemoteOS 发现 Web Server，但不修改它。

RemoteOS 可以：

- 检测进程。
- 显示版本。
- 显示配置路径。
- 显示运行状态。
- 读取有限信息。

RemoteOS 不可以：

- 修改配置。
- 升级。
- 卸载。
- 自动创建站点。
- 自动写入 HTTPS 配置。

典型场景：

```text
/usr/sbin/nginx
/etc/nginx/nginx.conf

状态：Running
模式：External
```

---

### 4.2 Integrated

RemoteOS 与现有 Web Server 集成，但不拥有它。

典型原则：

- 不负责安装。
- 不负责升级。
- 不负责卸载。
- 不全面接管主配置。
- 只管理 RemoteOS 自己创建的配置片段。

例如：

```text
/etc/nginx/
├── nginx.conf
├── conf.d/
│
└── remoteos.d/
    ├── remoteos.conf
    ├── acme.conf
    └── sites/
```

RemoteOS 只拥有：

```text
/etc/nginx/remoteos.d/*
```

这是推荐的默认集成模式。

---

### 4.3 Managed

Nginx 由 RemoteOS 安装并完整管理。

支持：

- Install
- Upgrade
- Start
- Stop
- Restart
- Reload
- Config
- Site
- HTTPS
- Uninstall

Windows 示例：

```text
C:\ProgramData\RemoteOS\
└── webserver\
    └── nginx\
        ├── nginx.exe
        ├── conf\
        ├── logs\
        └── ...
```

---

## 5. Provider 架构

不建议让其他模块直接依赖 `INginxManager`。

推荐通用 Provider：

```csharp
public interface IWebServerProvider
{
    string ProviderId { get; }

    WebServerType Type { get; }

    Task<WebServerDetectionResult> DetectAsync(
        CancellationToken cancellationToken);

    Task<WebServerRuntimeStatus> GetStatusAsync(
        WebServerInstance instance,
        CancellationToken cancellationToken);

    Task<TestConfigurationResult> TestConfigurationAsync(
        WebServerInstance instance,
        CancellationToken cancellationToken);

    Task ReloadAsync(
        WebServerInstance instance,
        CancellationToken cancellationToken);
}
```

Nginx：

```csharp
public sealed class NginxWebServerProvider
    : IWebServerProvider
{
}
```

未来：

```text
IISWebServerProvider
ApacheWebServerProvider
CaddyWebServerProvider
```

---

## 6. 能力接口拆分

不要把所有能力都塞到 `IWebServerProvider`。

建议按能力拆分。

### 6.1 生命周期

```csharp
public interface IWebServerLifecycle
{
    Task StartAsync(
        WebServerInstance instance,
        CancellationToken cancellationToken);

    Task StopAsync(
        WebServerInstance instance,
        CancellationToken cancellationToken);

    Task RestartAsync(
        WebServerInstance instance,
        CancellationToken cancellationToken);

    Task ReloadAsync(
        WebServerInstance instance,
        CancellationToken cancellationToken);
}
```

### 6.2 安装

```csharp
public interface IWebServerInstaller
{
    Task<WebServerInstallResult> InstallAsync(
        WebServerInstallOptions options,
        CancellationToken cancellationToken);

    Task UpgradeAsync(
        WebServerInstance instance,
        CancellationToken cancellationToken);

    Task UninstallAsync(
        WebServerInstance instance,
        CancellationToken cancellationToken);
}
```

### 6.3 配置

```csharp
public interface IWebServerConfiguration
{
    Task<TestConfigurationResult> TestAsync(
        WebServerInstance instance,
        CancellationToken cancellationToken);

    Task<WebServerConfigSnapshot> BackupAsync(
        WebServerInstance instance,
        CancellationToken cancellationToken);

    Task RestoreAsync(
        WebServerInstance instance,
        WebServerConfigSnapshot snapshot,
        CancellationToken cancellationToken);
}
```

### 6.4 站点

```csharp
public interface IWebSiteProvider
{
    Task<IReadOnlyList<WebSiteInfo>> ListAsync(
        WebServerInstance instance,
        CancellationToken cancellationToken);

    Task<WebSiteInfo> CreateAsync(
        WebServerInstance instance,
        WebSiteDefinition site,
        CancellationToken cancellationToken);

    Task UpdateAsync(
        WebServerInstance instance,
        WebSiteDefinition site,
        CancellationToken cancellationToken);

    Task DeleteAsync(
        WebServerInstance instance,
        Guid siteId,
        CancellationToken cancellationToken);
}
```

---

## 7. WebServerInstance 与 Provider 分离

Provider 表示一种实现能力。

Instance 表示当前机器上实际存在的 Web Server 实例。

```csharp
public enum WebServerType
{
    Nginx,
    OpenResty,
    IIS,
    Apache,
    Caddy,
    Unknown
}
```

```csharp
public sealed record WebServerInstance
{
    public required Guid Id { get; init; }

    public required WebServerType Type { get; init; }

    public required string ProviderId { get; init; }

    public required WebServerManagementMode ManagementMode { get; init; }

    public required string ExecutablePath { get; init; }

    public string? MainConfigPath { get; init; }

    public string? ConfigDirectory { get; init; }

    public string? Version { get; init; }

    public bool IsRemoteOsManaged { get; init; }
}
```

不要把系统限制为单个 Nginx。

同一主机理论上可能同时存在：

```text
/usr/sbin/nginx
/usr/local/openresty/nginx/sbin/nginx
C:\ProgramData\RemoteOS\webserver\nginx\nginx.exe
```

内部推荐统一使用：

```csharp
IReadOnlyList<WebServerInstance>
```

即使 V1 UI 只选择一个活动实例。

---

## 8. NginxDetector

建议定义：

```csharp
public interface IWebServerDetector
{
    Task<IReadOnlyList<WebServerCandidate>> DetectAsync(
        CancellationToken cancellationToken);
}
```

Nginx 检测来源：

1. PATH 中的 nginx。
2. 常见 Linux 安装路径。
3. systemd nginx.service。
4. systemd openresty.service。
5. Windows PATH。
6. RemoteOS Managed Nginx。
7. 已运行的 nginx 进程。

Linux 常见路径：

```text
/usr/sbin/nginx
/usr/local/nginx/sbin/nginx
/usr/local/openresty/nginx/sbin/nginx
```

Windows 可检测：

```text
C:\nginx\nginx.exe
C:\Program Files\nginx\nginx.exe
C:\ProgramData\RemoteOS\webserver\nginx\nginx.exe
```

最终应执行类似：

```text
nginx -V
```

以确认：

- 是否为 nginx。
- 版本。
- prefix。
- conf-path。
- modules-path。
- 编译参数。

---

## 9. 不自动接管已有 Nginx

RemoteOS 首次发现已有 nginx 时，不应该自动修改配置。

推荐流程：

```text
Detected
   ↓
用户选择
   ├── 忽略
   ├── 仅监控
   └── 启用集成
```

只有用户明确同意，才进入：

```text
Integrated
```

只有用户明确选择安装 RemoteOS 管理版本，才进入：

```text
Managed
```

---

## 10. 配置 Ownership

建议给配置引入 Ownership：

```csharp
public enum ConfigOwnership
{
    External,
    Shared,
    RemoteOs
}
```

规则：

### External

```text
只读
```

例如：

```text
/etc/nginx/nginx.conf
/etc/nginx/conf.d/foo.conf
```

### Shared

```text
可以修改，但必须备份并验证
```

### RemoteOs

```text
RemoteOS 可以完全管理
```

例如：

```text
/etc/nginx/remoteos.d/*
```

这样可以避免删除站点、卸载 RemoteOS、配置恢复时误伤用户配置。

---

## 11. 配置目录策略

对于现有 Nginx，建议只做一次最小侵入式修改：

```nginx
include /etc/nginx/remoteos.d/*.conf;
include /etc/nginx/remoteos.d/sites/*.conf;
```

之后所有 RemoteOS 配置都进入：

```text
/etc/nginx/remoteos.d/
```

不要持续修改：

```text
/etc/nginx/nginx.conf
```

对于 RemoteOS Managed Nginx，则可以完全拥有配置结构。

---

## 12. 配置修改必须事务化

不要采用：

```text
写配置
↓
reload
```

推荐：

```text
生成配置
    ↓
Backup
    ↓
写临时文件
    ↓
nginx -t
    ↓
成功？
 ┌──┴───┐
Yes     No
 │       │
Commit  Rollback
 │
reload
 │
成功？
 ┌──┴───┐
Yes     No
 │       │
Finish  Rollback
```

推荐抽象：

```csharp
public interface IConfigTransaction
{
    Task<ConfigTransactionResult> ExecuteAsync(
        Func<CancellationToken, Task> mutation,
        CancellationToken cancellationToken);
}
```

Nginx 典型过程：

1. Snapshot
2. Mutate
3. `nginx -t`
4. Commit
5. Reload
6. 失败时回滚

---

## 13. Nginx 内部拆分

推荐：

```text
Nginx/
│
├── NginxWebServerProvider.cs
│
├── Detection/
│   └── NginxDetector.cs
│
├── Runtime/
│   ├── INginxRuntime.cs
│   ├── LinuxNginxRuntime.cs
│   └── WindowsNginxRuntime.cs
│
├── Installation/
│   ├── NginxInstaller.cs
│   ├── LinuxNginxInstaller.cs
│   └── WindowsNginxInstaller.cs
│
├── Configuration/
│   ├── NginxConfigurationManager.cs
│   ├── NginxConfigValidator.cs
│   ├── NginxConfigTransaction.cs
│   └── NginxConfigRenderer.cs
│
├── Sites/
│   └── NginxSiteProvider.cs
│
└── Certificates/
    └── NginxCertificateDeployer.cs
```

`NginxWebServerProvider` 作为 facade，不承载全部细节。

---

## 14. Windows / Linux 差异

核心逻辑不要到处写：

```csharp
if (OperatingSystem.IsWindows())
{
    ...
}
else
{
    ...
}
```

建议通过平台实现隔离差异。

### Linux

可能使用：

```text
systemctl start nginx
systemctl stop nginx
systemctl reload nginx
```

或者原生命令：

```text
nginx
nginx -s quit
nginx -s reload
```

### Windows

对于 RemoteOS Managed Nginx，可以由 RemoteOS Service 负责 nginx 进程生命周期。

```text
RemoteOS Service
    ↓
nginx.exe
```

Windows 官方 nginx 本身不需要成为 RemoteOS 的核心系统服务依赖。

---

## 15. 与 CertificateManager 的关系

不要设计：

```text
CertificateManager
    ↓
NginxManager
```

推荐：

```text
CertificateManager
       │
       │ CertificateIssuedEvent
       ▼
CertificateDeploymentCoordinator
       │
       ├── KestrelCertificateDeployer
       ├── NginxCertificateDeployer
       ├── IISCertificateDeployer
       └── ApacheCertificateDeployer
```

定义：

```csharp
public interface ICertificateDeployer
{
    string TargetType { get; }

    Task<CertificateDeploymentResult> DeployAsync(
        ManagedCertificate certificate,
        CertificateDeploymentTarget target,
        CancellationToken cancellationToken);
}
```

这样 CertificateManager 不知道 Nginx 的存在。

---

## 16. Certificate 与 Deployment 分离

不要在 CertificateRecord 中加入：

```text
NginxSiteId
NginxConfigPath
```

推荐单独建立：

```text
CertificateDeploymentRecord
```

例如：

```text
Id
CertificateId

TargetType
TargetInstanceId
TargetResourceId

Status
LastDeployedAt
LastError
```

这样一张证书可以同时部署到：

```text
Certificate
   ├── RemoteOS Kestrel
   └── Nginx
```

---

## 17. HTTP-01 与 Nginx 的关联

证书管理器继续保持：

```text
IHttp01ChallengeProvider
├── DirectHttp01ChallengeProvider
└── WebRootHttp01ChallengeProvider
```

WebRoot 只负责写 challenge 文件。

例如：

```text
Linux:
  /var/lib/remoteos/acme-challenge/

Windows:
  C:\ProgramData\RemoteOS\acme-challenge\
```

然后额外增加：

```csharp
public interface IHttp01WebServerIntegrator
{
    Task<Http01IntegrationResult> EnsureConfiguredAsync(
        WebServerInstance instance,
        string domain,
        string webRootPath,
        CancellationToken cancellationToken);

    Task RemoveAsync(
        WebServerInstance instance,
        string domain,
        CancellationToken cancellationToken);
}
```

Nginx 实现仅负责暴露：

```nginx
location /.well-known/acme-challenge/ {
    root /var/lib/remoteos;
}
```

核心关系：

```text
Certificate
     │
     ▼
IHttp01ChallengeProvider
     │
     ▼
challenge file
```

以及：

```text
WebServer
     │
     ▼
IHttp01WebServerIntegrator
     │
     ▼
Nginx / IIS / Apache
```

两者由 Coordinator 协调，不直接相互依赖。

---

## 18. 站点模型

不要使用：

```text
NginxSite
```

推荐统一：

```csharp
public sealed record WebSiteDefinition
{
    public required Guid Id { get; init; }

    public required string Name { get; init; }

    public required IReadOnlyList<string> Domains { get; init; }

    public required IReadOnlyList<WebSiteBinding> Bindings { get; init; }

    public WebSiteTarget? Target { get; init; }

    public Guid? CertificateId { get; init; }
}
```

反向代理：

```csharp
public sealed record ReverseProxyTarget
{
    public required Uri Address { get; init; }

    public bool WebSocket { get; init; }
}
```

NginxProvider 负责把通用模型 Render 为 nginx 配置。

未来 IISProvider 可以将同一模型转换为 IIS Binding / Rewrite 配置。

---

## 19. WebServerCapabilities

为了避免前端判断：

```csharp
if (server.Type == WebServerType.Nginx)
{
    ...
}
```

推荐：

```csharp
[Flags]
public enum WebServerCapabilities
{
    None                = 0,

    Lifecycle           = 1 << 0,
    Reload              = 1 << 1,
    Configuration       = 1 << 2,
    Sites               = 1 << 3,
    ReverseProxy        = 1 << 4,
    Https               = 1 << 5,
    Http01Integration   = 1 << 6,
    Logs                = 1 << 7,
    Install             = 1 << 8,
    Upgrade             = 1 << 9,
    Metrics             = 1 << 10
}
```

例如：

```text
External Nginx
──────────────
Configuration      ✓
Reload             ✓
Sites              ✓
Install            ✗
Upgrade            ✗
Uninstall          ✗

Managed Nginx
──────────────
Configuration      ✓
Reload             ✓
Sites              ✓
Install            ✓
Upgrade            ✓
Uninstall          ✓
```

---

## 20. 建议的数据模型

### 20.1 WebServerInstanceRecord

```text
Id
ProviderId
Type
DisplayName

ExecutablePath
MainConfigPath
ConfigDirectory

Version

ManagementMode
Status

CreatedAt
UpdatedAt
```

### 20.2 WebSiteRecord

```text
Id
Name

WebServerInstanceId

Type
Domains
Enabled

ManagedByRemoteOS

CreatedAt
UpdatedAt
```

### 20.3 CertificateDeploymentRecord

```text
Id
CertificateId

TargetType
TargetInstanceId
TargetResourceId

Status
LastDeployedAt
LastError
```

### 20.4 WebServerConfigSnapshot

```text
Id
WebServerInstanceId

Reason
Path
Hash

CreatedAt
```

---

## 21. Source of Truth 原则

RemoteOS 自己创建的站点：

```text
SQLite / Domain Model
        ↓
WebSiteDefinition
        ↓
NginxConfigRenderer
        ↓
*.conf
```

即：

```text
Database = Source of Truth
Nginx Config = Generated Artifact
```

对于用户原有 Nginx 配置：

```text
Nginx Config = External Source of Truth
```

RemoteOS 只读或有限集成。

不要把所有 nginx.conf 解析后强行当作 RemoteOS 数据库。

---

## 22. 状态模型

Runtime 状态：

```csharp
public enum WebServerState
{
    Unknown,

    NotInstalled,
    Installed,

    Starting,
    Running,

    Reloading,
    Stopping,
    Stopped,

    InvalidConfiguration,

    Failed
}
```

集成状态：

```csharp
public enum IntegrationState
{
    None,

    Detected,

    Monitoring,

    Integrated,

    Managed,

    Broken
}
```

示例：

```text
RuntimeState = Running
IntegrationState = Detected
```

表示 Nginx 正在运行，但 RemoteOS 没有接管。

```text
RuntimeState = Running
IntegrationState = Integrated
```

表示用户已有 Nginx，RemoteOS 只管理自己的配置片段。

---

## 23. API 建议

发现 Web Server：

```text
POST /api/webservers/discover
```

列出：

```text
GET /api/webservers
```

状态：

```text
GET /api/webservers/{id}/status
```

启用集成：

```text
POST /api/webservers/{id}/integrate
```

测试配置：

```text
POST /api/webservers/{id}/config/test
```

Reload：

```text
POST /api/webservers/{id}/reload
```

站点：

```text
GET    /api/webservers/{id}/sites
POST   /api/webservers/{id}/sites
PUT    /api/webservers/{id}/sites/{siteId}
DELETE /api/webservers/{id}/sites/{siteId}
```

---

## 24. 创建反向代理站点流程

例如：

```text
remote.example.com
        ↓
http://127.0.0.1:8000
```

流程：

```text
Avalonia
   │
   ▼
Website API
   │
   ▼
WebSiteManager
   │
   ├── 检查域名
   ├── 检查端口冲突
   ├── 保存 WebSiteDefinition
   │
   ▼
IWebSiteProvider
   │
   ▼
NginxSiteProvider
   │
   ▼
NginxConfigRenderer
   │
   ▼
生成 temporary config
   │
   ▼
nginx -t
   │
   ├── Failed
   │      ↓
   │   Rollback
   │
   └── Success
          ↓
       Commit
          ↓
       nginx reload
```

---

## 25. 证书部署流程

```text
Avalonia
   │
   ▼
CertificateManager
   │
   ▼
ACME
   │
   ▼
CertificateStore
   │
   ▼
CertificateIssued
   │
   ▼
CertificateDeploymentCoordinator
   │
   ▼
NginxCertificateDeployer
   │
   ▼
修改 RemoteOS-owned site config
   │
   ▼
nginx -t
   │
   ▼
reload
```

CertificateManager 本身不需要知道 Nginx。

---

## 26. 已有 Nginx + HTTP-01 流程

已有 Nginx 占用 80 时：

```text
CertificateManager
       │
       ▼
WebRootHttp01ChallengeProvider
       │
       ▼
acme-challenge directory
```

Nginx 只负责：

```text
/.well-known/acme-challenge/*
```

映射到对应目录。

RemoteOS 不需要停止 Nginx，也不需要自己抢占 80。

---

## 27. 分阶段开发顺序

### 第一阶段：基础 Web Server 抽象

优先实现：

```text
WebServerInstance
IWebServerProvider
WebServerManager
NginxDetector
NginxRuntime
WebServerManagementMode
WebServerCapabilities
```

目标：

- 能发现 Nginx。
- 能查看状态。
- 能区分 External / Integrated / Managed。
- 能安全执行 Reload / Test。

---

### 第二阶段：配置事务与站点

实现：

```text
NginxConfigurationManager
NginxConfigTransaction
NginxConfigRenderer
NginxSiteProvider
WebSiteDefinition
```

目标：

- 创建反向代理站点。
- 自动生成 RemoteOS-owned 配置。
- 支持 nginx -t。
- 支持自动回滚。
- 支持 reload。

---

### 第三阶段：证书自动部署

实现：

```text
CertificateDeploymentCoordinator
NginxCertificateDeployer
IHttp01WebServerIntegrator
```

目标：

- 证书续期后自动部署到 Nginx。
- HTTP-01 WebRoot 自动集成。
- 保持 CertificateManager 与 Nginx 解耦。

---

### 第四阶段：跨 Web Server 扩展

增加：

```text
IISWebServerProvider
ApacheWebServerProvider
CaddyWebServerProvider
```

验证 WebServer 抽象是否合理。

---

## 28. 最终架构

```text
┌──────────────────────────────────────────────────────┐
│                  Avalonia Client                     │
└───────────────────────┬──────────────────────────────┘
                        │
                        ▼
┌──────────────────────────────────────────────────────┐
│                   RemoteOS API                       │
└──────────────┬───────────────────────┬───────────────┘
               │                       │
               ▼                       ▼
     CertificateManager         WebServerManager
               │                       │
       ┌───────┼───────┐        ┌──────┼─────────┐
       │       │       │        │      │         │
      ACME   Store  Renewal   Nginx   IIS     Apache
       │
       ▼
 Certificate
       │
       ▼
CertificateDeploymentCoordinator
       │
       ├─────────────┬─────────────┐
       ▼             ▼             ▼
    Kestrel        Nginx          IIS
    Deployer       Deployer       Deployer
```

HTTP-01：

```text
CertificateManager
        │
        ▼
ChallengeCoordinator
        │
        ├── Direct HTTP-01
        │
        ├── WebRoot HTTP-01
        │        │
        │        └── IWebServerChallengeIntegrator
        │                    │
        │             ┌──────┴──────┐
        │             ▼             ▼
        │           Nginx          IIS
        │
        └── DNS-01
```

---

## 29. 最终结论

RemoteOS 不应该被设计成：

> RemoteOS 自带 Nginx。

也不应该变成：

> RemoteOS 是一个 Nginx 管理面板。

更合适的定位是：

> **RemoteOS 能够发现本机 Web Server，并根据用户授权选择监控、集成或者托管；Nginx 是第一个完整实现的 Web Server Provider。**

推荐关系：

```text
                  RemoteOS
                     │
             WebServer Abstraction
                     │
          ┌──────────┼──────────┐
          ▼          ▼          ▼
        Nginx       IIS       Apache
       可选        可选        可选
```

Nginx 推荐默认策略：

```text
已有 nginx
    ↓
Detected
    ↓
用户允许
    ↓
Integrated
    ↓
RemoteOS 只管理 remoteos.d/
```

如果没有 Nginx：

```text
没有 nginx
    ↓
用户选择“安装 Nginx”
    ↓
Managed
    ↓
RemoteOS 完整管理
```

这一设计保持了以下原则：

- Web Server 与 RemoteOS 核心解耦。
- Nginx 是可选 Provider，而不是基础依赖。
- 证书签发与部署解耦。
- HTTP-01 与具体 Web Server 解耦。
- 用户原有配置默认不被接管。
- RemoteOS 自己创建的资源有明确 Ownership。
- 配置修改全部经过验证、备份和回滚。
- Windows 与 Linux 平台差异被封装在实现层。
- 为 IIS、Apache、Caddy 等未来扩展保留稳定边界。
