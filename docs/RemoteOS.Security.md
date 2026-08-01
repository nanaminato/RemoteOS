# RemoteOS Security Design

> 本文档定义 RemoteOS 安全设计原则。
>
> RemoteOS 不实现独立权限系统，而是基于 Linux 原生用户、权限、sudo、Capability 机制提供安全操作体验。
>
> RemoteOS 的目标不是替代 Linux Security，而是将 Linux 管理能力包装成类似现代桌面操作系统的安全交互模型。
>
> 相关文档：
>
> - RemoteOS.Authentication.md
> - RemoteOS.Workspace.md
> - RemoteOS.Architecture.md

---

# 1. 设计目标


RemoteOS 面向：

- 个人服务器用户
- 网站管理员
- 小型团队服务器


主要场景：

- 文件管理
- 网站部署
- 服务管理
- Docker 管理
- Terminal 操作


RemoteOS 不追求：

- 多租户安全隔离
- 云平台级权限控制
- 企业 IAM 系统


核心目标：

> 让用户可以安全地管理 Linux Server，同时减少误操作风险。


---

# 2. Security Model


RemoteOS 安全模型：



RemoteOS Application

    |

    v

Linux User Context

    |

    v

Linux Security Model

    |

    v

Operating System



RemoteOS 不拥有最终权限。


最终决定权：


Linux Kernel



---

# 3. Responsibility Boundary


## RemoteOS 负责


- 操作确认
- 风险提示
- 用户交互
- sudo 请求
- 操作日志
- 状态恢复


## Linux 负责


- 用户身份
- 文件权限
- Process 权限
- Service 权限
- Network 权限
- Kernel Security


---

# 4. Principle


## 4.1 默认最小权限


RemoteOS Application 默认使用当前用户身份。


例如：



Login:

alice

Application:

RemoteExplorer

Process:

alice



不会默认：


root



---

## 4.2 权限不足时提升


RemoteOS 不提前判断所有权限。


直接执行操作：



User Action

    |

    v

Linux Operation

    |

    +---- Success

    |

    +---- Permission Denied

                |

                v

          Request Privilege


原因：

Linux 权限模型已经非常复杂：

- ACL
- Group
- Capability
- SELinux
- AppArmor


RemoteOS 不重复实现。


---

# 5. Privilege Escalation


权限提升采用 Linux 原生机制。


例如：



RemoteOS

|

v

sudo

|

v

Linux Authentication

|

v

root Capability



---

# 6. File Operation Security


RemoteExplorer 管理文件。


普通操作：

例如：


Create File

Rename File

Move File



直接执行。


---

危险操作：

例如：


Delete System File

Delete Directory

Modify Configuration



流程：



User Click Delete

    |

    v

Check Linux Permission

    |

    +---- Allowed

    |

    +---- Denied

                |

                v

          Request Authentication

                |

                v

             Execute


---

# 7. Dangerous Operation Confirmation


RemoteOS 对危险操作提供二次确认。


例如：


删除：


/etc/nginx



提示：



This operation requires administrator permission.

Target:

/etc/nginx

Continue?



确认后：


sudo rm



---

# 8. Operation Risk Level


RemoteOS 可以根据操作风险提供提示。


## Level 0


普通操作：



Read File

Open Application

View Status



无需确认。


---

## Level 1


用户目录操作：



Delete User File

Modify Application Data



普通确认。


---

## Level 2


系统配置：



Modify:

/etc

/usr

/var

System Service



需要管理员确认。


---

## Level 3


高风险操作：



Delete Disk

Modify Boot

Change Firewall

Remove User



需要：

- 明确确认
- 管理员认证
- 操作记录


---

# 9. RemoteTerminal Security


RemoteTerminal 不创建新的 Shell 权限。


模型：



RemoteTerminal

    |

    v

PTY

    |

    v

Shell

    |

    v

Linux User



例如：



whoami

alice



---

# 10. sudo Handling


用户执行：



sudo apt install nginx



RemoteOS 不拦截。


交给 Linux：



sudo

|

v

PAM Authentication

|

v

Execute



RemoteOS 只负责：

- 显示认证界面
- 保存 Session 状态
- 显示执行结果


---

# 11. Application Security


RemoteOS Application 不直接访问系统。


应用访问系统资源：



Application

    |

    v

RemoteOS App SDK

    |

    v

System API

    |

    v

Linux



禁止：


Application

    |

    v

Direct Shell



---

# 12. Application Capability


未来 Application 可以声明能力。


例如：


Manifest:



Application:

RemoteExplorer

Capabilities:

filesystem.read

filesystem.write



但是：

Capability 不替代 Linux Permission。


最终：



Application Capability

    +

Linux Permission

    |

    v

Allowed Operation



---

# 13. Docker Security


Docker 是特殊资源。


原因：

Docker 默认拥有接近 root 权限。


RemoteOS 不应该默认允许：


docker exec

docker rm

docker run --privileged



操作流程：



Docker Operation

    |

    v

Check Linux Docker Permission

    |

    v

Require Confirmation

    |

    v

Execute



---

# 14. Service Management


管理系统服务：


例如：


systemctl restart nginx



流程：



RemoteOS Settings

    |

    v

systemctl

    |

    v

Linux Permission

    |

    v

Service Change



危险服务操作：

需要：

- 确认
- 管理员认证


---

# 15. Audit Log


RemoteOS 保存用户操作记录。


例如：


AuditLog

User:

alice

Action:

Restart nginx

Time:

2026-01-01

Result:

Success



记录：

- 用户
- 时间
- 操作
- 目标资源
- 执行结果


不记录：

- 密码
- Token
- 私密数据


---

# 16. Session Security


Session 保存：


User

Device

Workspace

Authentication State



Session 生命周期：



Created

|

Active

|

Disconnected

|

Expired



---

# 17. RemoteOS Server Security


RemoteOS Server 应：

- 最小 Linux 权限运行
- 不默认 root
- 使用 sudo 执行需要权限的任务


推荐：



remoteos-server

    |

    v

sudo limited commands



而不是：



remoteos-server

    |

    v

root



---

# 18. MVP Implementation


MVP 实现：


必须：

- Linux User Context
- sudo 支持
- 文件权限处理
- Terminal 用户隔离
- 基础操作确认


暂不实现：

- RemoteOS ACL
- RBAC
- IAM
- 多租户隔离
- 自定义安全策略


---

# 19. AI Agent Rules


实现 RemoteOS 安全相关功能时：


必须：

- 使用 Linux Security Model
- 使用 sudo / PAM
- 保持最小权限
- 高风险操作需要确认


禁止：

- 创建新的权限体系
- 绕过 Linux Permission
- 默认使用 root
- 将 RemoteOS 设计为多租户平台
- 存储 Linux 密码