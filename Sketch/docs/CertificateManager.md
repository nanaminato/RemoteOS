# Certificate Manager 产品设计（Sketch）

## 定位

Certificate Manager 负责 HTTPS 证书的申请、绑定、续期和吊销。它将 ACME 账户、DNS 凭据引用和 Web 服务绑定分离，避免把私钥或云提供商 Token 暴露给 UI。体验参考 1Panel 的证书列表/申请/续签流程，但把验证、授权和审计显式化。

## 信息架构与组件

```text
Overview
├─ Certificates      域名、签发者、有效期、状态、自动续期和使用站点
├─ Request           域名 SAN、HTTP-01 / DNS-01、站点绑定、签发进度
├─ ACME accounts     邮箱、目录 URL、账户健康与密钥引用
├─ DNS providers     供应商、已配置状态、凭据引用（绝不显示 token）
├─ Renewal policy    提前天数、维护窗口、失败告警
└─ Activity          申请、验证、续期、部署和吊销审计
```

### 关键流程

1. 输入主域名与 SAN，选择 HTTP-01 或 DNS-01；DNS-01 必须选择已配置的 provider reference。
2. Server 创建短期验证任务，UI 展示 challenge、轮询/推送进度和失败原因（不展示秘密）。
3. 成功后，将证书作为受保护实体保存，再显式绑定到 Nginx Site 或其他服务。
4. 自动续期在到期前策略窗口运行；失败会产生高优先级活动项，现有证书在有效期内继续使用。
5. 吊销必须强确认并说明会立刻使已部署 HTTPS 证书失效。

## Mock API

前缀：`/api/sketch/certificates`。

| 方法 | 路由 | 用途 |
|---|---|---|
| GET | `/overview` | 有效、即将过期、账户和提供商指标 |
| GET / POST | `/items` | 证书列表；以 `CertificateIssueRequest` 发起申请 |
| POST | `/items/{id}/actions/renew` | 续期；`?force=true` 表示立即执行 |
| POST | `/items/{id}/actions/revoke?force=true` | 吊销，需要显式强确认 |
| GET | `/acme-accounts` | ACME 账户元数据 |
| GET | `/dns-providers` | DNS 供应商和凭据引用状态 |
| GET | `/renewal-policy` | 自动续期窗口和提前天数 |

## 正式实现边界

- 私钥只允许由服务端生成、导入或从 OS/HSM 安全存储读取；API 与审计只能显示 key reference 和指纹。
- 每一次签发、续期、部署、导出和吊销都必须有权限控制和不可抵赖审计。
- Challenge、DNS propagation 和部署属于可取消的长任务；客户端断开不能中断服务端安全清理。
- 证书签发者、ACME Directory、DNS provider 和账户密钥均按管理员允许列表管理，禁止任意 URL/脚本执行。
