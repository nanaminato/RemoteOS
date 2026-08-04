# RemoteOS Explorer 模块设计

> 内置文件管理器：UI 移植自 [Jaya File Manager](https://github.com/waliarubal/Jaya)（BSD-3-Clause），所有文件操作经 Server 端 REST API 执行，复用宿主 OS 用户/权限体系。
>
> - 架构原则见 [`RemoteOS.Architecture.md`](./RemoteOS.Architecture.md)
> - 项目当前状态见 [`RemoteOS.md`](./RemoteOS.md)（§6 内置应用 / §7 RemoteExplorer）
> - 桌面外壳与窗口管理见 [`RemoteOS.Desktop.md`](./RemoteOS.Desktop.md)
> - 登录与身份认证见 [`RemoteOS.Login.md`](./RemoteOS.Login.md)（Explorer 复用 `IAuthSession` JWT）
> - 通信协议契约见 [`RemoteOS.Protocol.md`](./RemoteOS.Protocol.md)（§Files DTO 与路由）

---

## 1. 定位

RemoteExplorer 是 RemoteOS 的内置文件管理器，用于浏览与操作**服务端宿主 OS** 的文件系统。

- **架构归属**：§6.2 Remote Service Application —— UI 在 Client 本地渲染，文件 IO 在 Server 端执行。
- **复用宿主 OS 权限**（project_memory 硬约束）：Server 以宿主 OS 进程身份执行 `System.IO`，复用宿主用户/权限，**不另建** RemoteOS ACL 系统。权限提升（sudo/UAC）后续接入（见 §7 后续）。
- **不存储密码**：认证委托宿主 OS（已完成于登录模块），Explorer 仅消费 `IAuthSession.Tokens.AccessToken`。
- **UI 来源**：移植自 Jaya File Manager（BSD-3）。保留 Jaya 的导航树 + Explorer 网格 + 地址栏 + 工具栏 + 状态栏布局；**去除**插件系统（仅保留 FileSystem 单 provider）、Ribbon（Phase 6 延后）、About/ManagePlugins/Update 等非 MVP 视图。原始版权头保留于移植文件。

**MVP 范围**：浏览（驱动器/目录/文件）+ 基本操作（新建文件夹/删除/重命名/复制/移动/上传/下载）。

---

## 2. 包与集成方式

### 2.1 NuGet 包

| 包 | 版本 | 用途 |
|----|------|------|
| `Xaml.Behaviors.Avalonia` | 12.0.0.1 | Avalonia 12 兼容的 behaviors（双击导航等交互；wieslawsolotes fork） |
| `Newtonsoft.Json` | 13.0.3 | Jaya 配置模型序列化（保留 Jaya 原始依赖；线协议 DTO 仍用 System.Text.Json） |

- 中心化包管理：版本声明在 [`Directory.Packages.props`](../Directory.Packages.props)。
- **DataGrid**：Avalonia 11+ 起 DataGrid 已并入主 `Avalonia` 包，无需单独引用。MVP 实际用 `ListBox` + `DataTemplate` 实现条目网格（更轻量，列宽自适应）。
- **不引入** Jaya 原依赖：`AvaloniaUIRibbon`（不兼容 12.1）、`RestSharp`（去 UpdateService）、`Avalonia.ReactiveUI`、`Avalonia.Controls.DataGrid`（旧独立包）、`Avalonia.Xaml.Behaviors`（旧 0.10 线，已弃用）。

### 2.2 嵌入而非替换 Shell

`ExplorerMainView` 作为 `UserControl` 塞进 `RemoteWindow`，与 Notepad / Terminal 同构：

```text
ExplorerApp (RemoteApplicationBase)
    |
    AppContext.ShowWindow("RemoteExplorer", view)
    |
    WindowManager.Create → RemoteWindow
    |
    ExplorerMainView (UserControl)
    ├── MenuView（顶部：文件/编辑/查看/帮助）
    ├── ToolbarView（顶部：后退/前进/向上/刷新/新建/删除/重命名/下载/上传）
    ├── AddressbarView（顶部：路径 TextBox + 转到）
    ├── StatusbarView（底部：状态文本 + 加载指示）
    └── Grid（主体）
        ├── NavigationView（左：TreeView 懒加载）
        ├── GridSplitter
        └── ExplorerView（右：ListBox + 列标题）
```

### 2.3 去插件化

Jaya 原架构通过 `ServiceLocator` 反射扫描 `Jaya.Provider.*.dll` 加载多 provider（FileSystem/Dropbox/Ftp/GoogleDrive/S3）。本次移植**去除**整个插件系统：

- 删除 4 个云 provider 项目（Dropbox/Ftp/GoogleDrive/S3）。
- `ServiceLocator` 反射加载 + `AppDomain.GetAssemblies` 扫描 → **删除**。
- `IProviderService` 接口边界 → **替换为** `IExplorerClient`（typed HttpClient，直接调 Server REST API）。
- 多账户模型（`AccountModelBase`）→ **删除**（单 provider 单宿主机，无需账户配置）。
- `ViewModelLocator.AutoWireViewModel` 反射装配 → **替换为** `ExplorerApp.Activate` 中显式 `new ExplorerViewModel(client)` + DI。

---

## 3. 架构：REST（非 SignalR）

### 3.1 数据流

```text
┌───────────── Client (RemoteOS.Client/Apps/Explorer) ─────────────┐    ┌──────── Server (RemoteOS.Server/Files) ────────┐
│                                                                  │    │                                                │
│  ExplorerMainView (UserControl)                                  │    │  FileEndpoints (minimal API, [Authorize], JWT) │
│    └─ DataContext = ExplorerViewModel                            │    │    └─ GET drives/list/info/download            │
│                                                                  │    │       POST directory/rename/move/copy/upload   │
│  ExplorerViewModel                                               │    │       DELETE files                              │
│    ├─ Nodes: ObservableCollection<TreeNodeModel>（导航树）       │    │                                                │
│    ├─ Entries: ObservableCollection<FileSystemEntryDto>（网格）  │    │  IFileService                                  │
│    ├─ AddressbarPath / StatusText / IsBusy                       │    │    └─ LocalFileService (System.IO, 平台感知)    │
│    ├─ 历史栈（Back/Forward/Up）                                  │    │         = 移植自 Jaya FileSystemService         │
│    └─ 文件操作命令（NewFolder/Delete/Rename/Copy/Move/U/D）      │    │         Windows 盘符 / Linux "/" 根特殊处理     │
│                                                                  │    │         复用宿主 OS 进程身份与权限              │
│  IExplorerClient (typed HttpClient, JWT via IAuthSession)        │    │                                                │
│    └─ ExplorerClient                                             │    │                                                │
└──────────────────────────────────────────────────────────────────┘    └────────────────────────────────────────────────┘
              ↕  REST /api/v1/files/*  +  Shared/RemoteOS.Protocol/Files (DTO + FileApiRoutes)
```

### 3.2 传输选型：REST HTTP（非 SignalR）

与终端模块（SignalR 流式字节）不同，Explorer 用 **REST HTTP**：

1. **请求/响应天然契合**目录列举（一次请求返回完整 `DirectoryDto`）。
2. 与 Auth 端点同构（`Results.Ok` / `Results.Problem`），错误处理复用 `RemoteOsAuthException`。
3. 文件下载用 `Results.File(stream, ...)` 流式返回；上传用 `multipart/form-data`。
4. SignalR 仅未来 watch（目录变化推送）/大文件分块流式才需要，MVP 不引入。

### 3.3 认证复用 IAuthSession

`IExplorerClient` 从 `IAuthSession` 取 `ServerUrl` + `Tokens.AccessToken`：

- 不 mutate `HttpClient.BaseAddress`（每个请求用 `serverUrl` 构造绝对 URI，避免共享实例并发竞态——与 `IRemoteOsClient` 同模式）。
- 未登录（`State != Authenticated`）调用抛 `InvalidOperationException`；`ExplorerApp.Activate` 在未登录时弹提示窗。
- 所有端点 `[Authorize]`，错误统一 RFC 7807 `ProblemDetails`（错误码在 `type` URI，无 `Errors` 字典——见 Protocol.md）。

---

## 4. Server 端

### 4.1 IFileService / LocalFileService

[`RemoteOS.Server/Files/IFileService.cs`](../RemoteOS.Server/Files/IFileService.cs) 定义接口；[`LocalFileService.cs`](../RemoteOS.Server/Files/LocalFileService.cs) 实现。

- **移植自** Jaya `FileSystemService.GetDirectoryAsync` 的目录枚举逻辑（`DirectoryInfo.EnumerateDirectories` / `EnumerateFiles`）。
- **平台感知**：`GetDrives()` 返回 `DriveInfo.GetDrives()`；`GetDirectory(null)` 在 Windows 返回盘符聚合视图，在 Linux 返回 "/" 根列举。
- **UnauthorizedAccessException 吞并**：列举时部分子目录不可访问不应导致整列失败（与 Jaya 一致）。
- **新增操作**（Jaya 原本 NotImplemented）：`CreateDirectory` / `Delete`（递归）/ `Rename` / `Move` / `Copy` / `Upload` / `OpenRead`（下载）。
- **以宿主 OS 进程身份运行**：Server 进程的权限即文件操作权限（复用宿主用户/权限，不另建 ACL——project_memory 硬约束）。

### 4.2 FileEndpoints

[`RemoteOS.Server/Endpoints/FileEndpoints.cs`](../RemoteOS.Server/Endpoints/FileEndpoints.cs) — 静态 `MapFileEndpoints(this IEndpointRouteBuilder)`，minimal API，全部 `RequireAuthorization()`。错误用 `Results.Problem(detail, statusCode, title, type: "https://remoteos.app/problems/" + suffix)`（仿 `AuthEndpoints.cs`）。

### 4.3 REST 端点签名

路由常量见 [`FileApiRoutes`](../Shared/RemoteOS.Protocol/Files/FileApiRoutes.cs)，均 `$"/api/v1/files/..."`。

| 方法 | 入参 | 返回 | 错误码（type suffix） |
|------|------|------|----------------------|
| `GET /files/drives` | — | `IReadOnlyList<DriveDto>` | — |
| `GET /files/list?path=` | `string? path`（空=盘符根） | `DirectoryDto` | `not-found` / `access-denied` / `invalid-path` |
| `GET /files/info?path=` | `string path` | `FileSystemEntryDto`（404 if 缺） | `not-found` / `access-denied` |
| `GET /files/download?path=` | `string path` | `Results.File(stream, "application/octet-stream", fileName)` | `not-found` / `access-denied` |
| `POST /files/directory?path=` | `string path` | `Results.Created(path, FileSystemEntryDto)` | `already-exists` / `access-denied` |
| `DELETE /files?path=` | `string path` | `Results.NoContent()` | `not-found` / `access-denied` / `io-error` |
| `POST /files/rename` | body `RenameRequest` | `FileSystemEntryDto` | `not-found` / `already-exists` / `access-denied` |
| `POST /files/move` | body `MoveRequest` | `FileSystemEntryDto` | `not-found` / `already-exists` / `access-denied` |
| `POST /files/copy` | body `CopyRequest` | `FileSystemEntryDto` | `not-found` / `already-exists` / `access-denied` |
| `POST /files/upload?path=` | `string path` + `IFormFile` | `Results.Created(path, FileEntryDto)` | `not-found` / `access-denied` / `io-error` |

### 4.4 Program.cs 注册

```csharp
builder.Services.AddSingleton<Server.Files.IFileService, Server.Files.LocalFileService>();
// ...
app.MapFileEndpoints();   // 紧随 app.MapAuthEndpoints();
```

---

## 5. Client 端

### 5.1 文件清单

```
Client/RemoteOS.Client/Apps/Explorer/
├── ExplorerApp.cs                 RemoteApplicationBase，Activate 创建 VM+View+Window，注入对话框回调
├── IExplorerClient.cs             typed HttpClient 抽象
├── ExplorerClient.cs              实现：JWT from IAuthSession，绝对 URI，ProblemDetails → RemoteOsAuthException
├── Models/
│   └── TreeNodeModel.cs           导航树节点（懒加载 + dummy child 模式，移植自 Jaya TreeNodeModel）
├── ViewModels/
│   └── ExplorerViewModel.cs       主 VM：合并 Jaya Explorer/Navigation/Addressbar/Toolbar/Statusbar VM
├── Views/
│   ├── ExplorerMainView.axaml     布局：Menu/Toolbar/Addressbar/Statusbar + Navigation Tree + Explorer Grid
│   └── ExplorerMainView.axaml.cs  code-behind：地址栏回车/转到/双击进入
├── Dialogs/
│   ├── TextInputDialogView.axaml(.cs)   通用文本输入对话框（新建文件夹/重命名/复制/移动目标）
│   ├── TextInputDialogViewModel.cs
│   ├── ConfirmDialogView.axaml(.cs)     通用确认对话框（删除确认/About 消息）
│   └── ConfirmDialogViewModel.cs
└── Converters/
    └── EntryConverters.cs         EntryType→图标可见性/类型名/大小友好字符串（SizeSuffix 移植自 Jaya）
```

### 5.2 ExplorerViewModel 设计

合并 Jaya 的 5 个 VM（`ExplorerViewModel` / `NavigationViewModel` / `AddressbarViewModel` / `ToolbarViewModel` / `StatusbarViewModel`）为单一 `ExplorerViewModel`，适配 RemoteOS DI 约定（避免引入 Jaya 的 `ServiceLocator` / `EventAggregator` / `ViewModelLocator` 反射基础设施）。

- **导航树懒加载**：`TreeNodeModel.AddDummyChild()` 保证展开箭头显示；首次展开触发 `ExpandRequested` 回调 → `IExplorerClient.GetDirectoryAsync` → 填充子目录（与 Jaya `OnNodeExpanded` 同模式）。
- **历史栈**：`_history` + `_historyIndex` 支持 `GoBack` / `GoForward` / `GoUp`（`CanGoBack`/`CanGoForward`/`CanGoUp` 驱动命令可用性）。
- **双击行为**：目录/驱动器 → `NavigateToAsync`；文件 → MVP 不打开（后续接入预览）。
- **文件操作命令**：`NewFolder` / `Delete` / `Rename` / `Copy` / `Move` / `Upload` / `Download` / `About` / `Close`，均通过 `[RelayCommand]` 生成，操作后调 `RefreshAsync` 刷新视图。

### 5.3 对话框回调注入

`ExplorerApp.WireDialogs` 将宿主能力注入 VM（仿 `NotepadApp` 的 `RequestTextAsync` 模式）：

| VM 属性 | 用途 | 实现 |
|---------|------|------|
| `RequestTextInputAsync` | 新建/重命名/复制/移动目标输入 | `AppContext.ShowDialogAsync<string?>` + `TextInputDialogView` |
| `RequestConfirmAsync` | 删除确认 | `AppContext.ShowDialogAsync<bool?>` + `ConfirmDialogView` |
| `ShowMessageAsync` | About 消息 | `ConfirmDialogView`（单按钮） |
| `RequestLocalOpenFileAsync` | 上传本地源文件选择 | `StorageProvider.OpenFilePickerAsync`（TopLevel = MainWindow） |
| `RequestLocalSaveFileAsync` | 下载本地保存路径 | `StorageProvider.SaveFilePickerAsync` |
| `CloseAction` | 关闭 Explorer 窗口 | `WindowManager.Close(window)` |

**TopLevel 获取**：`ManagedWindow` / `RemoteWindow` 不是 `TopLevel`（桌面外壳 Canvas 内的 `TemplatedControl`），故 `StorageProvider` 用 `Application.Current.ApplicationLifetime.MainWindow`（实际 Avalonia `Window`）作为根。

### 5.4 Bootstrapper 注册

```csharp
services.AddHttpClient<Client.Apps.Explorer.IExplorerClient, Client.Apps.Explorer.ExplorerClient>();
services.AddSingleton<IRemoteApplication, Client.Apps.Explorer.ExplorerApp>();
```

`DesktopShellViewModel.PopulateDesktop()` 自动遍历所有 `IRemoteApplication`，Explorer 图标（📁）出现在桌面与开始菜单。

---

## 6. Protocol 层

[`Shared/RemoteOS.Protocol/Files/`](../Shared/RemoteOS.Protocol/Files/) — 零 Newtonsoft，纯 `System.Text.Json`，`sealed record` + `[property: JsonPropertyName("...")]`（camelCase，对齐 `RemoteOsJsonOptions.Default`）。

| 文件 | 职责 |
|------|------|
| `FileSystemEntryType.cs` | enum `File/Directory/Drive`（`JsonStringEnumConverter` camelCase） |
| `FileSystemEntryDto.cs` | 通用条目元数据：path/name/size/type/created/modified/accessed/isHidden/isSystem |
| `FileEntryDto.cs` | 文件条目（含 extension，非空 size）——用于 `DirectoryDto.Files` 列表 |
| `DirectoryDto.cs` | 目录列举结果：目录自身元数据 + `Directories[]` + `Files[]` |
| `DriveDto.cs` | 驱动器/根挂载点：name/path/totalSize/isReady |
| `RenameRequest.cs` / `MoveRequest.cs` / `CopyRequest.cs` | 操作请求 body |
| `FileApiRoutes.cs` | 路由常量（路径含 `/api/v1` 前缀，Server 注册与 Client 拼接共用） |

**设计说明**：`FileEntryDto` 与 `FileSystemEntryDto` 是独立 record（非继承），因为 `FileEntryDto` 多 `Extension` 字段且 `Size` 为非空 `long`。Client 网格绑定 `FileSystemEntryDto`，`ExplorerViewModel` 在填充 `Entries` 时将 `FileEntryDto` 转为 `FileSystemEntryDto`（`Type=File`），丢弃 `Extension`（网格不展示）。

---

## 7. 后续

- **Ribbon**（Phase 6）：引 `AvaloniaControls.Ribbon.Flowery`（Avalonia 11.3+/net8+ 兼容 fork），移植 `RibbonView.axaml`。
- **预览/详情面板**：移植 Jaya `PreviewView` / `DetailsView`（图片/文本预览、文件属性详情）。
- **视图模式切换**：Details/Icons/List/Tiles/Content（Jaya `PaneConfigModel.ViewMode`）。
- **权限提升**：危险操作（如删除系统目录）委托宿主 OS（Linux: sudo / Windows: UAC、RunAs）——project_memory 硬约束。
- **目录 watch**：SignalR Hub 推送目录变化（`FileSystemWatcher` → Hub → Client 刷新）。
- **大文件流式**：分块上传/断点续传（替代当前一次性 `multipart/form-data`）。
- **配置持久化**：Jaya `PaneConfigModel` / `ToolbarConfigModel` / `ApplicationConfigModel` 本地保存（保留 Newtonsoft 序列化）。

---

## 8. AI Agent 规则

实现/修改 Explorer 时必须遵守：

1. **所有文件 IO 必须经 `IExplorerClient` → REST API**。客户端不得直接访问本地文件系统（上传/下载的本地源/目标除外，走 `StorageProvider`）。Server 端 IO 只在 `LocalFileService` 内。
2. **复用宿主 OS 权限，不另建 ACL**（project_memory 硬约束）。Server 以进程身份执行 `System.IO`，权限不足返回 `access-denied`（403）。MVP 不做 sudo/UAC 提升。
3. **JWT 复用 `IAuthSession`**：`IExplorerClient` 不持有独立凭据；未登录调 `ExplorerApp.Activate` 弹提示窗，不崩溃。
4. **错误统一 RFC 7807**：Server `Results.Problem(..., type: "https://remoteos.app/problems/" + suffix)`；Client `ExplorerClient` 解析 `ProblemDetails` 抛 `RemoteOsAuthException`，VM catch 后写 `StatusText`。
5. **路由常量共用 `FileApiRoutes`**：Server 注册与 Client 拼接 URL 必须用同一常量，禁止硬编码字符串。
6. **DTO 用 `sealed record` + `[property: JsonPropertyName]`**（Protocol 约定），线协议用 `System.Text.Json`（`RemoteOsJsonOptions.Default`）。Jaya 配置模型（如未来引入 `PaneConfigModel`）保留 Newtonsoft，但不进入线协议。
7. **移植 Jaya 文件保留原始版权头**（`// Copyright (c) Rubal Walia...`），不删改；新增文件用 RemoteOS 自己的版权头。Jaya 归属见 [`THIRD_PARTY_NOTICES.md`](../THIRD_PARTY_NOTICES.md)。
8. **不引入 Jaya 的 `ServiceLocator` / `ViewModelLocator` / `EventAggregator` 反射基础设施**。新代码用 RemoteOS DI（`Microsoft.Extensions.DependencyInjection`）+ `CommunityToolkit.Mvvm`（`[ObservableProperty]` / `[RelayCommand]`）。
9. **对话框走 `AppContext.ShowDialogAsync`**（与 Notepad 同模式），不直接创建 Avalonia `Window`。本地文件选择走 `StorageProvider`（TopLevel = `MainWindow`）。
10. **编译验证**：`dotnet build RemoteOS.sln -c Debug` 必须 0 错误（NU1903 Microsoft.OpenApi 与 CS0169 TerminalSession._disposed 为既有警告，非本模块引入）。
