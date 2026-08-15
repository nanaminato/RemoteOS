# Docker Manager 产品设计（Sketch）

## 定位

Docker Manager 面向单台 RemoteOS 主机，提供日常容器运维闭环。信息架构参考 1Panel 的“容器—编排—镜像—网络—存储”工作方式，但不暴露 Docker daemon，也不把它实现为远程 Docker API 的代理。

本 Sketch 交付可用的 UI 组件和**状态化 Mock API**：操作会改变内存状态，供交互、错误与确认流程演示。真正实现应遵循主项目的 [Docker Manager 设计](../../docs/applications/RemoteOS.DockerManager.md) 的权限、审计与本机传输边界。

## 当前草图范围

当前版本将 1Panel 的信息架构收敛为普通用户可以直接完成的 Docker 使用闭环：

| 页面 | 已实现的功能 | 本阶段不做 |
|---|---|---|
| 概览 | 引擎状态、容器/镜像/网络/卷数量、最近操作 | CPU、内存、运行时长等资源观测 |
| 容器 | 创建、启动、停止、重启、编辑参数、删除；停止与删除有确认步骤 | 终端、日志、实时统计、关联资源详情 |
| 镜像 | 拉取公共镜像；从镜像列表直接进入“运行容器” | 本地构建、导入/导出、私有仓库凭据 |
| 网络 | 创建 bridge/host/none 网络，安全删除未使用网络 | CIDR、网关、容器关联详情 |
| 存储卷 | 创建 local 卷，安全删除未挂载卷 | 使用者与挂载点观测 |

容器参数包括名称、镜像、端口映射、启动命令、环境变量、挂载、网络和重启策略。为避免误导，Mock 服务要求镜像先被拉取，再允许创建容器；所有状态改变都会立即反映在列表与概览中。

## 信息架构与组件

```text
Overview
├─ Containers      状态、端口、启动/停止/重启、参数编辑和删除
├─ Images          公共镜像列表、拉取与一键运行
├─ Networks        创建与安全删除
├─ Volumes         创建与安全删除
└─ Activity        最近操作和结果
```

每个列表页面有刷新、空态、表格行操作与明确的成功/失败反馈。停止容器和删除容器、网络、卷必须进入确认对话框；不得以 Toast 代替确认。

### 核心流程

| 流程 | 组件 | 成功反馈 | 失败/边界 |
|---|---|---|---|
| 启动、停止、重启容器 | 行内操作 + 确认框 | 状态行和 Activity 即时更新 | 停止/删除未确认时提示 `Confirmation is required` |
| 拉取并运行镜像 | 镜像拉取表单 + 容器参数表单 | 新镜像显示在列表中，可直接运行 | 仅接受公共镜像格式；镜像未拉取时不能运行 |
| 编辑容器参数 | 容器编辑表单 | 新端口、命令、环境变量、挂载、网络和重启策略立即保存 | 容器名必须唯一；网络必须存在 |
| 管理网络与卷 | 创建表单 + 删除确认框 | 新资源立即显示在列表中 | 已被使用的网络或卷不可删除 |

## Mock API

前缀：`/api/sketch/docker`。所有修改接口都返回 `MockOperationResult`，包含 `succeeded`、用户可见 `message`、时间和 `operationId`。

| 方法 | 路由 | 用途 |
|---|---|---|
| GET | `/overview` | 卡片指标、健康状态和活动流 |
| GET | `/containers` | 容器分页模型（原型中返回完整列表） |
| POST / PUT | `/containers`、`/containers/{id}` | 创建容器、编辑可运行参数 |
| POST | `/containers/{id}/actions` | body: `{ action, confirmed }`；start/stop/restart/delete |
| GET / POST | `/images`、`/images/pull` | 列出镜像、拉取公共镜像 |
| GET / POST / DELETE | `/networks`、`/networks/{id}` | 列出、创建和删除网络 |
| GET / POST / DELETE | `/volumes`、`/volumes/{name}` | 列出、创建和删除存储卷 |

## 真实服务端的实现要求

- 以 `IDockerEngineService` 和受控 `IDockerComposeService` 为唯一宿主边界；Client 不得拼接命令或访问 socket。
- 实现 RBAC：read、manage、install 分离；所有危险操作审计操作者、目标、确认方式和 `OperationId`。
- 公共镜像拉取应采用允许的镜像引用格式；如未来添加私有仓库，凭据必须以服务端安全引用存储。
- 真实 API 需要游标分页、取消、长任务通道和稳定错误码；Mock 的同步结果只用于体验原型。
