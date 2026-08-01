# RemoteOS Workspace 模型设计文档

> 本文档定义 RemoteOS 的用户环境模型：
>
> - User
> - Workspace
> - Device
> - Session
> - Controller / Observer
> - Workspace 生命周期
> - 多设备连接模型
>
> 本文档描述 RemoteOS 作为云操作系统时的运行模型。
>
> 模块架构见：
>
> `RemoteOS.Architecture.md`
>
> 当前代码实现见：
>
> `RemoteOS.md`


---

# 1. 设计目标


RemoteOS 不采用传统远程桌面的用户模型。


传统远程桌面：


User

|

+-- Session A

Desktop Instance

+-- Session B

Desktop Instance


每个 Session 是独立桌面。


RemoteOS 不采用这种模型。


RemoteOS 采用：


User

|

Workspace

|

Session

|

Device



核心思想：

> 一个用户拥有一个持续存在的 RemoteOS Workspace，多个设备作为终端连接该 Workspace。


---

# 2. 核心对象关系


整体关系：



User

|

|

Workspace

|

|

Session

|

|

Device



说明：

|对象|含义|
|-|-|
|User|身份主体|
|Workspace|用户的 RemoteOS 环境|
|Session|设备连接 Workspace 的会话|
|Device|访问 RemoteOS 的终端设备|


---

# 3. User


## 3.1 定义


User 是 RemoteOS 的身份主体。


负责：

- 登录认证
- 权限管理
- 数据归属
- Workspace 所有权


示例：


User

Id:
10001

Username:
alice



---

## 3.2 Workspace 关系


默认：


One User

|

One Personal Workspace



例如：


Alice

|

Alice Workspace



未来可扩展：


Alice

|

+-- Personal Workspace

+-- Work Workspace

+-- Development Workspace



---

# 4. Workspace


## 4.1 定义


Workspace 是 RemoteOS 的核心运行实例。


Workspace 表示：

> 一个持续存在的 RemoteOS 用户环境。


Workspace 不属于某个设备。


设备只是连接 Workspace 的入口。


---

## 4.2 Workspace 包含内容


Workspace 保存：


Workspace

Desktop State
Application State
Runtime State
User Data
Permission Context
Remote Service State


---

# 5. Desktop State


Desktop State 表示桌面环境状态。


包含：


Wallpaper

Theme

Desktop Layout

Icon Position

Taskbar State



例如：


Desktop

Wallpaper:
default

Theme:
Dark

Icons:

Browser
Terminal
Explorer



---

# 6. Application State


Application State 表示应用状态。


注意：

Application State 不是 UI 图像。


RemoteOS 保存的是：

- 应用配置
- 运行状态
- 用户数据


例如：

## RemoteBrowser


保存：


Tabs

History

Bookmark

Cookie

Extension Config



不保存：


Browser Screenshot



---

## RemoteTerminal


保存：


Terminal Session Id

Working Directory

Environment

Process State



例如：


Terminal

Session:

id=10001

cwd:

/home/alice/project



---

# 7. Runtime State


Runtime State 表示持续运行的服务。


例如：


RemoteTerminal

    |

    PTY

    |

    Shell Process


Client 断开：


RemoteOS.Client

Offline



Workspace：


Running



Runtime：


Continue



重新连接：


Restore Session



---

# 8. Workspace 生命周期


Workspace 默认持续存在。


生命周期：



Created

|

Running

|

Idle

|

Sleeping

|

Running



---

# 9. Workspace Running


Running 状态：

表示：

- 有连接设备
- 有活动 Runtime
- 有后台任务


例如：


Workspace

Controller:

Laptop

Runtime:

Terminal

Browser Service



---

# 10. Workspace Idle


Idle 状态：

表示：

- 无 Controller
- 无用户操作


但是：


Workspace State

仍然存在



---

# 11. Workspace Sleeping


为了降低资源消耗：

Workspace 可以进入 Sleep。


条件：

- 长时间无连接
- 无重要 Runtime
- 无后台任务


Sleep：


Memory State

保存

Runtime

暂停或者迁移



恢复：


Device Login

|

Wake Workspace

|

Restore State



---

# 12. Device


## 12.1 定义


Device 表示访问 RemoteOS 的终端。


例如：



Device

Name:

Office-PC

Platform:

Windows 11

Client:

RemoteOS.Client 1.0



---

## 12.2 Device 保存信息


包含：


DeviceId

Name

Platform

Client Version

Last Login Time

Trust Status



---

# 13. Session


## 13.1 定义


Session 表示：

> 一个 Device 与 Workspace 的连接关系。


关系：



Workspace

|

Session

|

Device



---

## 13.2 Session 与 Workspace 区别


Session 消失：


Device Disconnect



不代表：


Workspace Destroy



例如：



Laptop Shutdown

Session:

Disconnected

Workspace:

Running



---

# 14. 多设备连接模型


RemoteOS 使用：


# Active Controller + Observer


目标：

支持多个设备访问同一个 Workspace。


但是：

同一时间只有一个设备拥有完整控制权。


---

# 15. Controller


Controller 是当前控制设备。


拥有：



Keyboard Input

Mouse Input

Window Operation

Application Control

System Command



例如：


Workspace

Controller:

Office-PC



---

# 16. Observer


Observer 是观察设备。


拥有：


View Workspace State

View Running Application

Receive Notification



不拥有：


Window Control

Input Control

Application Modification



---

# 17. Controller / Observer 状态


Workspace：



Workspace

Controller

|

Laptop

Observers

|
Phone
Tablet


---

# 18. Control Transfer


Observer 可以请求控制权。


流程：



Observer

Request Control

    |

Workspace

    |

Controller Change



例如：


Before:



Office-PC

Controller



After:



Laptop

Controller

Office-PC

Observer



---

# 19. Controller 断开处理


Controller 可能因为：

- 网络断开
- Client 崩溃
- 设备睡眠


导致：


Connection Lost



RemoteOS 不立即释放 Controller。


---

## 19.1 Grace Period


流程：



Controller Lost

    |

Keep Controller Lease

    |

Timeout

    |

Release Controller



例如：


5 minutes



---

## 19.2 恢复连接


如果原设备在 Grace Period 内恢复：



Reconnect

    |

Restore Controller



---

## 19.3 超时后接管


超过时间：



New Device

    |

Request Control

    |

Become Controller



---

# 20. 登录流程


## 20.1 首次登录



RemoteOS.Client Start

    |

Authentication

    |

Create User Workspace

    |

Become Controller

    |

Load Workspace State



---

# 21. 已存在 Workspace 登录


例如：


当前：



Office-PC

Controller



新设备：


Laptop Login



RemoteOS：


Workspace Active

Current Controller:

Office-PC



提供：



[Join Observer]

[Request Control]

[Cancel]



---

# 22. 与传统 Windows Server 模型区别


Windows Server：


User

|

Multiple Desktop Session



RemoteOS：


User

|

One Persistent Workspace

|

Multiple Device Session



---

区别：


| |Windows Server|RemoteOS|
|-|-|-|
|用户环境|Session|Workspace|
|设备关系|独立登录|连接同一环境|
|桌面状态|Session内部|Workspace共享|
|应用状态|Session内部|Workspace持久|
|多设备|多个桌面|多个终端|


---

# 23. AI Agent 理解规则


实现 RemoteOS 用户系统时必须遵守：


不要设计：


User

|

Multiple Independent Desktop



不要创建：


Device Local Workspace



正确模型：


User

|

Persistent Workspace

|

Session

|

Device



---

必须保持：

- Workspace 持久化
- Runtime 状态独立于 Device
- Session 只是连接关系
- Controller 管理控制权


RemoteOS 的目标：

不是：

> 多用户远程桌面服务器


而是：

> 一个用户拥有持续运行的云操作系统环境，多个设备作为终端访问该环境。