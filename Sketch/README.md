# RemoteOS Sketch

独立的 UI 设计沙盒。它不依赖主 RemoteOS Client、Server、权限系统或本地化资源；Mock Server 只提供固定的登录和管理器状态，用于验证窗口结构、空状态和安装引导。

```bash
dotnet run --project Sketch/RemoteOS.Sketch.Server
dotnet run --project Sketch/RemoteOS.Sketch.Desktop
```

默认 mock 登录：任意用户名和密码均可登录。桌面端在 Server 未启动时也会使用内建的离线 mock 数据显示草图。
