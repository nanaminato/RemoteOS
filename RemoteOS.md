# RemoteOS 项目说明文档

> 本文档描述 RemoteOS 当前实现状态：
>
> - Solution 结构
> - 项目列表
> - 代码地图
> - 当前 MVP 进度
> - 开发状态
>
> 本文档不是架构规范。
>
> 架构设计原则见：
>
> `RemoteOS.Architecture.md`
>
> 用户 Workspace 模型见：
>
> `RemoteOS.Workspace.md`
>
> 当文档冲突时：
>
> - 本文档代表当前代码实现
> - Architecture 文档代表设计原则


---

# 1. RemoteOS 简介


RemoteOS 是一个基于 Avalonia 的跨平台桌面操作系统 Shell。


目标：

构建一个类似现代操作系统的桌面环境：

- Desktop
- Window Manager
- Application Runtime
- Application SDK


未来扩展：

- RemoteServer
- Workspace
- Storage
- Sync
- Remote Service


---

# 2. 当前开发阶段


当前重点：

完成本地 RemoteOS Shell。


包括：

- 桌面环境
- 窗口管理
- 应用运行时
- 应用开发模型


当前不实现：

- 用户系统
- 云同步
- 权限系统
- 文件服务器
- Docker 管理


---

# 3. Solution Structure


`RemoteOS.sln` 当前包含以下项目：



Client/

RemoteOS.Client

RemoteOS.Client.Desktop

Framework/

RemoteOS.Core

RemoteOS.UI

RemoteOS.WindowManager

RemoteOS.App.SDK

RemoteOS.Runtime

Shared/

RemoteOS.Protocol

RemoteOS.Server


---

# 4. 项目职责


## 4.1 RemoteOS.Client.Desktop


类型：


Executable (WinExe)



定位：

RemoteOS 平台启动入口。


类似：

- Windows Boot Loader
- Desktop Entry


职责：

- Avalonia AppBuilder
- 平台初始化
- 字体配置
- 日志配置
- 启动 RemoteOS.Client


不包含：

- Shell 逻辑
- 应用逻辑
- 窗口逻辑


---

# 4.2 RemoteOS.Client


类型：


Class Library



定位：

RemoteOS Shell。


类似：


explorer.exe



职责：

- Desktop
- Taskbar
- StartMenu
- MainWindow
- Shell 生命周期


包含：

- Welcome
- Notepad
- Settings


负责：

系统启动时装配：


WindowManager

ApplicationManager

Shell Services



---

# 4.3 RemoteOS.Core


定位：

基础抽象。


所有模块依赖 Core。


包含：


## Window Model



WindowId

WindowInfo

WindowState



## Application Model



AppId

ApplicationManifest

ApplicationInfo



## Geometry



Point

Size

Rect



要求：

Core 必须保持纯净。


禁止引用：

- Avalonia
- Network
- Database


---

# 4.4 RemoteOS.UI


定位：

RemoteOS UI 组件库。


负责：

- Theme
- Style
- Control Template


目标：

统一 Windows 11 风格视觉。


包含：

- Button Style
- TextBox Style
- List Style
- Window Style


---

# 4.5 RemoteOS.WindowManager


定位：

RemoteOS 窗口系统。


负责：

模拟操作系统窗口管理。


核心：



WindowManager

    |

RemoteWindow

    |

Avalonia Control



职责：

- 创建窗口
- 关闭窗口
- 移动
- Resize
- Focus
- Minimize
- Maximize
- Z Order


窗口创建流程：


Application Launch

    |

AppContext.ShowWindow

    |

WindowManager.Create

    |

RemoteWindow



---

# 4.6 RemoteOS.App.SDK


定位：

RemoteOS 应用开发接口。


类似：

- Windows SDK
- Android SDK


提供：

## Window API



AppContext.ShowWindow()



规划：


## Storage API



Storage.Save()

Storage.Load()



## Sync API



Sync.Push()

Sync.Pull()



## Remote API



RemoteClient.Execute()



应用通过：



IRemoteApplication

or

RemoteApplicationBase



接入系统。


---

# 4.7 RemoteOS.Runtime


定位：

应用运行时。


RemoteOS Application 不是普通 exe。


Runtime 负责：


- Application Registry
- Application Loading
- Application Lifecycle


流程：



Desktop Icon

    |

ApplicationManager.Launch

    |

Create AppContext

    |

IRemoteApplication.Activate

    |

Create Window



不负责：

- Window Algorithm
- UI Rendering


---

# 4.8 RemoteOS.Protocol


定位：

通信协议契约。


当前：

占位。


未来负责：


- DTO
- Message
- API Contract
- WebSocket Client
- API Client


规则：

所有 Client / Server 通信必须经过 Protocol。


禁止：

业务代码直接调用：


HTTP

WebSocket



---

# 4.9 RemoteOS.Server


定位：

RemoteOS Cloud Backend。


当前：

ASP.NET Core 默认模板占位。


未来负责：

- Authentication
- Workspace
- Storage
- Sync
- Remote Runtime
- Compute


不负责：

- UI Rendering
- Window Management
- Screen Streaming


---

# 5. Application 开发模型


RemoteOS Application 结构：



Application Package

├── Manifest

├── UI

├── Logic

├── State Manager

└── Remote Connector



MVP 阶段：

Manifest 由代码创建。


未来：

支持应用包加载。


---

# 6. 内置应用规划


## 6.1 Welcome


用途：

验证：

- Runtime
- WindowManager


状态：

已实现。


---

## 6.2 Notepad


用途：

验证：

- Application Lifecycle
- Window Interaction


状态：

已实现。


---

## 6.3 Settings


用途：

系统设置入口。


状态：

已实现。


---

# 7. 未来应用规划


## RemoteBrowser


定位：

不是远程浏览器。


结构：


RemoteBrowser

    |

Avalonia Window

    |

WebView2

    |

Chromium



网页：

本地加载。


同步：

Server：

- History
- Bookmark
- Cookie
- Extension Config


---

## RemoteTerminal


支持两种模式。


### Local Mode


运行：

Client


例如：


PowerShell

CMD

Bash



---

### Remote Mode


运行：

Server


结构：


Terminal UI

    |

WebSocket

    |

RemoteServer

    |

PTY

Shell



---

## RemoteExplorer


定位：

远程文件管理。


不是：

远程桌面文件浏览。


结构：


Explorer UI

    |

RemoteServer API

    |

Remote File System



---

# 8. MVP 开发计划


|阶段|内容|状态|
|-|-|-|
|MVP 0|Desktop / Wallpaper / Icon / Taskbar / WindowManager|完成|
|MVP 1|Runtime / App.SDK / Launch App / Create Window|完成|
|MVP 2|RemoteBrowser / RemoteTerminal / RemoteExplorer|进行中|
|MVP 3|RemoteServer：Account / Workspace / Sync / Storage / Remote State|计划|


---

# 9. 当前开发重点


开发顺序：


RemoteOS.Client
RemoteOS.WindowManager
RemoteOS.Core
RemoteOS.Runtime
RemoteOS.App.SDK


---

# 10. 当前禁止提前实现


在 MVP 阶段不要实现：


- 用户系统
- 登录系统
- 权限系统
- 云同步
- 文件服务器
- Docker 管理


原因：

先完成：

> 一个可以运行应用、管理窗口的本地桌面操作系统。


---

# 11. 开发约束


修改代码时必须保持：

## Window

窗口逻辑：

只属于：


RemoteOS.WindowManager



---

## Application

应用生命周期：

只属于：


RemoteOS.Runtime



---

## Shell

系统入口：

只属于：


RemoteOS.Client



---

## Communication

网络通信：

只经过：


RemoteOS.Protocol



---

# 12. AI Agent 快速理解


修改 RemoteOS 代码前：

必须理解：



RemoteOS

=

Operating System Shell

RemoteOS.Client

=

Desktop Shell

RemoteOS.WindowManager

=

Window System

RemoteOS.Runtime

=

Application Runtime

RemoteOS.Server

=

Cloud Backend



---

不要将 RemoteOS 实现为：



Remote Desktop Tool

或者

Web Management Dashboard



正确方向：



Application State

    +

Local Rendering

    +

Cloud Capability



---

# 13. 文档索引


## 架构设计


RemoteOS.Architecture.md



用于：

- 模块设计
- 依赖关系
- 架构原则


---

## Workspace 模型


RemoteOS.Workspace.md



用于：

- 用户
- 登录
- 多设备
- 云桌面状态


---

## 当前实现


RemoteOS.md



用于：

- 项目结构
- 代码位置
- 当前进度