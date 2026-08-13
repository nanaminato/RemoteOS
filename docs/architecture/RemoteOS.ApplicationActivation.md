# RemoteOS Application Activation

> 本文定义同一 Client 设备内的应用启动、深链和窗口实例复用。它不是 Client↔Server
> 协议；远端文件、容器等资源的真源仍在 Server，资源变更通知另行通过受授权的 Hub 设计。

## 1. 三种本地协作方式

| 方式 | 用途 | 返回值 | 范围 |
| --- | --- | --- | --- |
| activation | 打开或导航 UI | `AppActivationResult` | 当前 Client |
| host action（后续） | 受控的本地副作用，如建立 loopback 转发 | 强类型结果 | 当前 Client |
| resource event（后续） | 文件等远端资源发生变更的提示 | 无；客户端重新读取真源 | Workspace |

应用不得直接引用另一个应用的实现、ViewModel 或本地服务。activation 的入口为
`IAppActivation`（应用上下文）和 Shell 所有的 `IAppActivationService`。

## 2. URI 约定

仅接受绝对 `remoteos://` URI；scheme、host 和路径由 Shell 验证，不能动态反射调用应用。
当前已注册：

```text
remoteos://settings/personalization
remoteos://settings/apps
remoteos://settings/apps/{appId}/permissions
remoteos://file/open?appId={appId}&path={encodedPath}
```

最后一条仅允许 `remoteos.explorer` 作为来源，以兼容现有内置文件打开模型；外置包不能用它
传递宿主路径，而应使用其受限的文件 capability。调用方应使用
`RemoteOsActivationUris`，不能手工拼接字符串。

未匹配、歧义或不合法的 URI 分别得到 `RouteNotFound` 或 `InvalidUri`；应用不应猜测目标或
降级为直接调用另一个应用。

## 3. 同 URI 与窗口策略

`ApplicationManifest.InstancePolicy` 定义逻辑主窗口的复用行为：

| 策略 | 同一 URI/重复启动行为 |
| --- | --- |
| `MultiWindow` | 每次 activation 都运行一次应用激活流程，通常新建窗口。Notepad 和 Code Editor 明确使用此策略。 |
| `SingleWindow` | Runtime 查找非模态主窗口，将 URI 投递给 `IAppActivationHandler`，恢复并聚焦该窗口；不会再调用 `Activate` 或创建第二个主窗口。 |
| `SingleWindowPerActivationKey` | 预留给按 Shell 规范化 key（如工作区根）复用的应用；当前未注册 key，因此不应在新应用中使用。 |

模态对话框不计为应用实例。应用路由处理器负责把重复 activation 变成页面切换、对象选中或
内部标签页导航；Runtime 只负责阻止额外主窗口并聚焦已有窗口。

文件打开也遵循该规则：当前所有文件处理程序都是多窗口；未来的单窗口文件程序在已有窗口时
会先被 Runtime 聚焦，随后应实现自己的 activation 路由，把文件放入已有的标签或文档模型。

Settings 是首个 `SingleWindow` 应用：再次打开个性化页会切换现有窗口的页面；打开
`apps/{appId}/permissions` 会选中应用并打开其权限编辑器。Task Manager、Port Forwarding、
Firewall、Process Guardian 与 Docker 也已声明单窗口；Docker 尚未接入预览 action。

## 4. 扩展规则

新增公开路线时，应用实现 `IAppActivationHandler`，并在其应用设计文档中定义：路径、参数、
权限、同 URI 行为、单/多窗口策略和本地化错误 UX。Shell 要拒绝两个应用同时声明同一路线。

将来的 Docker 预览不能仅靠 URI：镜像本身没有运行服务或已发布端口。它应先以受确认的
host action 创建仅 Server-loopback 的端口映射，再请求本机 Port Forwarding action 得到实际
`localhost` URL，最后通过 Browser activation 打开该 URL；不得默认发布到 `0.0.0.0`。
