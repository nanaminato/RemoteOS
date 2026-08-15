# RemoteOS Sketch

独立的 UI 设计沙盒。项目结构沿用主项目的 Avalonia 模板分层：`Desktop` 只负责平台入口，`Client` 负责 App、窗口与页面，`Protocol` 存放契约，`Server` 提供 Mock API。它不依赖主 RemoteOS 的应用运行时、权限系统或本地化资源。

启动后会显示一个 RemoteOS 桌面，其中包含 Docker、Nginx 和证书管理器。点击桌面图标或底部任务栏图标会打开相应的独立窗口，而非在桌面窗口内切换页面。

请直接打开 `RemoteOS.Sketch.sln`。在 IDE 中将 `RemoteOS.Sketch.Server` 和 `RemoteOS.Sketch.Desktop` 配为多启动项目，先启动 Server。

```bash
dotnet run --project Sketch/RemoteOS.Sketch.Server
dotnet run --project Sketch/RemoteOS.Sketch.Desktop
```

默认 mock 登录：任意用户名和密码均可登录。桌面端在 Server 未启动时也会使用内建的离线 mock 数据显示草图。
