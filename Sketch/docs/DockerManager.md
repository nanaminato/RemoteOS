# Docker Manager 产品设计（Sketch）

## 定位

Docker Manager 面向单台 RemoteOS 主机，提供日常容器运维闭环。信息架构参考 1Panel 的“容器—编排—镜像—网络—存储”工作方式，但不暴露 Docker daemon，也不把它实现为远程 Docker API 的代理。

本 Sketch 交付可用的 UI 组件和**状态化 Mock API**：操作会改变内存状态，供交互、错误与确认流程演示。真正实现应遵循主项目的 [Docker Manager 设计](../../docs/applications/RemoteOS.DockerManager.md) 的权限、审计与本机传输边界。

## 信息架构与组件

```text
Overview
├─ Containers      表格、状态筛选、详情抽屉、日志、统计、生命周期操作
├─ Stacks          Compose 编辑器、模板、校验、保存草稿、部署/下线/重部署
├─ Images          镜像列表、标签/大小/使用状态、拉取和清理预览
├─ Networks        网络及连接容器
├─ Volumes         卷、挂载点及使用者
└─ Activity        最近操作、操作者、结果和 OperationId
```

每个列表页面具有：搜索/筛选、空态、加载骨架、刷新、表格行操作与详情侧栏。危险按钮（停止、删除、Stack down、清理）必须进入确认对话框，显示影响资源及确认文字；不得以 Toast 代替确认。

### 核心流程

| 流程 | 组件 | 成功反馈 | 失败/边界 |
|---|---|---|---|
| 启动、停止、重启容器 | 行内操作 + 确认框 | 状态行和 Activity 即时更新 | 停止/删除未确认时提示 `Confirmation is required` |
| 编辑 Stack | Compose 编辑器 + 校验面板 | 保存为 draft，部署后变为 running | 缺少 `services:` 时显示字段错误 |
| 清理镜像 | 清理预览弹层 | 显示释放空间并从列表移除未使用镜像 | 未确认不得执行 |
| 检查资源 | Details drawer | 展示环境变量（敏感值掩码）、网络、挂载与日志 | 404 显示资源已被删除并刷新列表 |

## Mock API

前缀：`/api/sketch/docker`。所有修改接口都返回 `MockOperationResult`，包含 `succeeded`、用户可见 `message`、时间和 `operationId`。

| 方法 | 路由 | 用途 |
|---|---|---|
| GET | `/overview` | 卡片指标、健康状态和活动流 |
| GET | `/containers` | 容器分页模型（原型中返回完整列表） |
| GET | `/containers/{id}` | 详情、掩码环境变量、挂载、网络、日志 |
| POST | `/containers/{id}/actions` | body: `{ action, confirmed }`；start/stop/restart/pause/unpause/delete |
| GET/POST | `/stacks` | Stack 列表；保存 Compose 草稿 |
| POST | `/stacks/{name}/actions/{action}` | deploy/redeploy/down；down 要求 `?confirmed=true` |
| GET | `/images` | 镜像及是否被使用 |
| GET / POST | `/images/prune-preview` / `/images/prune` | 先查看影响，再以 `?confirmed=true` 清理 |
| GET | `/networks`、`/volumes` | 网络、卷和依赖摘要 |

## 真实服务端的实现要求

- 以 `IDockerEngineService` 和受控 `IDockerComposeService` 为唯一宿主边界；Client 不得拼接命令或访问 socket。
- 实现 RBAC：read、manage、install 分离；所有危险操作审计操作者、目标、确认方式和 `OperationId`。
- Compose 仅接受结构化定义，在受控目录操作，`.env` 和注册表凭据必须是安全引用。
- 真实 API 需要游标分页、取消、长任务通道和稳定错误码；Mock 的同步结果只用于体验原型。
