# Nginx Manager 产品设计（Sketch）

## 定位

Nginx Manager 管理网站反向代理的**声明式配置**，而非任意 shell 或文件编辑器。它吸收 1Panel 的站点、反向代理、证书绑定与日志体验，同时将测试、版本和重载作为一条可审计的发布流水线。

## 信息架构与组件

```text
Overview
├─ Sites             域名、上游、启停、证书绑定、站点向导
├─ Configuration     server block 预览、版本历史、差异和回滚
├─ Test & Reload     语法测试结果、风险提示、重载确认
├─ Logs              访问/错误日志、站点和状态码筛选
└─ Activity          变更记录和操作者
```

### 站点编辑器

必填项是名称、域名和绝对上游 URL。高级区可容纳路径规则、WebSocket、访问限制、缓存、重定向和安全头；这些字段应保存为结构化模型后再渲染为 Nginx 配置，不能让 UI 直接写入任意配置片段。保存只创建待发布版本；操作员必须依次执行 **Test → Review → Reload**。

### 关键状态

| 状态 | UI 行为 |
|---|---|
| Service offline | 显示服务不可用，不伪装为部署成功；允许查看配置和测试结果 |
| Test failed | 禁用 Reload，定位至失败文件/行号，并保留当前生产版本 |
| Changed but not reloaded | 顶部发布条显示待应用版本和差异入口 |
| Certificate expiring | Sites 表格显示警告，并链接到 Certificate Manager |

## Mock API

前缀：`/api/sketch/nginx`。

| 方法 | 路由 | 用途 |
|---|---|---|
| GET | `/overview` | 服务与站点指标、活动流 |
| GET / POST | `/sites` | 读取、新建站点 |
| PUT / DELETE | `/sites/{id}` | 更新；删除必须 `?confirmed=true` |
| POST | `/configuration/test` | 返回 `NginxTestResult`，供测试结果面板使用 |
| POST | `/configuration/reload?confirmed=true` | 在确认并测试通过后应用配置 |
| GET | `/configuration/versions` | 版本、作者、摘要与配置预览 |
| GET | `/logs` | 访问/错误日志条目 |

## 正式实现边界

- Server 在私有配置目录生成候选文件，使用固定参数调用 `nginx -t`；成功后才原子切换/重载。
- 配置快照不可包含私钥；敏感上游认证应引用安全存储项。
- `reload`、回滚、删除站点及全局模板修改需要 `server.nginx.manage` 权限和审计。服务安装、启停另设宿主管理员权限。
- 日志应分页和限速；用户无权读取其他租户/站点的日志。
