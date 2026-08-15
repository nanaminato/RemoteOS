# RemoteOS Sketch 产品原型

此目录定义 Sketch 中三个基础设施应用的产品边界、信息架构和 Mock API。它们是可交互 UI 原型的契约，不代表真实宿主机实现；所有接口均为 `127.0.0.1:5088` 上的内存状态机，重启 Server 后恢复种子数据。

| 应用 | 设计文档 | 原型重点 |
|---|---|---|
| Docker Manager | [DockerManager.md](DockerManager.md) | 容器、Compose Stack、镜像、网络、卷和清理预览 |
| Nginx Manager | [NginxManager.md](NginxManager.md) | 站点、反向代理、配置版本、测试、重载和日志 |
| Certificate Manager | [CertificateManager.md](CertificateManager.md) | ACME 账户、证书生命周期、验证方式和续期策略 |

## 共同产品约束

- Client 仅调用 RemoteOS Server；不接触 Docker socket、Nginx 配置文件、私钥或 DNS 凭据。
- 所有改变状态的接口返回 `MockOperationResult`；删除、停止、清理、下线与吊销必须传递显式确认字段。
- 秘密在原型中只以 `secret://` 引用或掩码显示。正式实现只能由服务端安全存储解析。
- Mock API 旨在覆盖空态、正常态、失败态、危险操作确认和刷新后的状态变化，方便 UI 设计和自动化测试。

## 运行

```bash
dotnet run --project Sketch/RemoteOS.Sketch.Server
dotnet run --project Sketch/RemoteOS.Sketch.Desktop
```

先启动 Server。桌面端在服务不可用时仍可打开，但产品级资源数据需要 Mock Server。
