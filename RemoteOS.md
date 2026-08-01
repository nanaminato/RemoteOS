RemoteOS 项目开发规范文档
1. 项目定位

RemoteOS 是一个跨平台云原生桌面操作系统环境。

目标是在 Windows/Linux/macOS 上为Ubuntu提供类似 Windows Desktop 的用户体验。

RemoteOS 不是远程桌面软件。

禁止将 RemoteOS 理解为：

RDP
VNC
AnyDesk
TeamViewer
云桌面串流系统

RemoteOS 的核心理念：

应用程序运行在本地设备，UI 在本地渲染；RemoteServer 负责提供数据、状态同步、存储和远程计算能力。

类似：

Windows:

Windows Kernel
        |
Window Manager
        |
Application
        |
Local Rendering

RemoteOS:

RemoteOS Runtime
        |
Window Manager
        |
RemoteApplication
        |
Local Rendering

        +
        
RemoteServer
(Data / State / Storage / Compute)
2. 技术架构
2.1 RemoteOS Client

技术：

.NET 10
+
Avalonia UI
+
ReactiveUI
+
WebView2

目标：

实现一个跨平台桌面环境。

运行平台：

Windows
Linux
macOS

核心组件：

RemoteOS.Client

├── Shell
│
├── Desktop
│
├── Window Manager
│
├── Taskbar
│
├── Application Runtime
│
├── Resource Manager
│
├── Settings
│
└── Remote Protocol Client
2.2 RemoteServer

技术：

.NET 10
ASP.NET Core
Entity Framework Core
PostgreSQL / LiteDB
WebSocket
REST API

负责：

User Management

Application Data

File Storage

Synchronization

Remote Task Execution

Compute Service

不负责：

UI Rendering

Window Management

Screen Streaming
3. 总体架构
+------------------------------------------------+
|                RemoteOS Client                 |
|                                                |
|  +--------------------------------------------+|
|  |              RemoteOS Shell                ||
|  |                                            ||
|  | Desktop                                     |
|  | Window Manager                              |
|  | Taskbar                                     |
|  | Settings                                    |
|  +--------------------------------------------+|
|                                                |
|  +--------------------------------------------+|
|  |          Application Runtime               ||
|  |                                            ||
|  | RemoteBrowser                              |
|  | RemoteTerminal                             |
|  | RemoteFileManager                          |
|  | RemoteIDE                                  |
|  +--------------------------------------------+|
|                                                |
|              Avalonia Rendering                |
+------------------------------------------------+

                     |
                     |
              RemoteOS Protocol

                     |

+------------------------------------------------+
|                 RemoteServer                   |
|                                                |
| User Service                                   |
| Storage Service                                |
| Sync Service                                   |
| Application Service                            |
| Compute Service                                |
+------------------------------------------------+

4. RemoteOS Client设计规范
4.1 Shell

RemoteOS Shell 是整个系统入口。

负责：

Desktop
Taskbar
Window Manager
Application Launcher

类似：

Windows Explorer.exe

启动：

RemoteOS.exe

        |

RemoteOS Shell

        |

Desktop
5. Window Manager

RemoteOS 的核心。

所有应用必须运行在 Window 中。

例如：

+--------------------------------+
| RemoteBrowser                  |
|--------------------------------|
|                                |
|        WebView2                |
|                                |
+--------------------------------+


Window Manager负责：

生命周期
Create

Open

Close

Suspend

Restore

Destroy
窗口行为

支持：

Move

Resize

Minimize

Maximize

Focus

Z-Index

Dock
6. Application Runtime

RemoteOS应用不是传统 exe。

应用模型：

RemoteApplication

例如：

RemoteBrowser

RemoteTerminal

RemoteExplorer

RemoteIDE

应用结构：

Application Package

├── Manifest.json
│
├── UI
│
├── Logic
│
├── Remote Connector
│
└── State Manager

7. RemoteOS App SDK

提供给应用开发者。

目标：

类似：

Windows SDK

Android SDK

Electron Runtime

提供：

Window API
Window.Create()
Window.Show()
Window.Close()
Storage API
RemoteStorage.Save()
RemoteStorage.Load()
Sync API
SyncService.Push()
SyncService.Pull()
Remote API
RemoteClient.Execute()
8. 内置应用规范
8.1 RemoteBrowser

RemoteOS 浏览器不是 Chrome 镜像。

实现：

RemoteBrowser App

        |

Avalonia Window

        |

WebView2

        |

Chromium Engine


网页：

本地渲染

同步：

History

Bookmark

Cookie

Extension Config

Browser Setting


流程：

WebView2 Profile

        ↕

Sync Service

        ↕

RemoteServer

8.2 RemoteFileManager

不是远程桌面文件管理。

而是：

Remote File Explorer

显示：

/

├── home

├── projects

├── docker


数据来源：

RemoteServer File API

        |

Linux Filesystem


例如：

打开文件：

Double Click

      |

Download Metadata

      |

Open Local Application

8.3 RemoteTerminal

支持两种模式。

Local Terminal
RemoteTerminal

      |

PowerShell

CMD

Bash

Remote Terminal
RemoteTerminal

      |

WebSocket

      |

RemoteServer

      |

SSH Shell

9. RemoteServer API设计

通信方式：

HTTPS REST API

+

WebSocket


主要服务：

User Service
User

Authentication

Permission

Storage Service
File

Config

Application Data

Sync Service
State Synchronization

Compute Service
Remote Task

Background Job

10. MVP开发路线
MVP 0 - RemoteOS Shell

目标：

启动一个虚拟桌面。

实现：

RemoteOS.exe

功能：

✅ Desktop

✅ Wallpaper

✅ Icon

✅ Taskbar

✅ Window Manager

MVP 1 - Application Runtime

实现：

RemoteOS.App.SDK

支持：

创建：

HelloWorld App

功能：

Double Click

Launch App

Create Window

MVP 2 - Built-in Applications

实现：

RemoteBrowser

技术：

Avalonia

+

WebView2

功能：

浏览网页
Profile同步
RemoteTerminal

功能：

Local Shell
Remote Shell
RemoteExplorer

功能：

浏览远程文件
文件同步
MVP 3 - RemoteServer

加入：

Account

Sync

Storage

Application State
11. AI Agent开发约束

IMPORTANT:

RemoteOS is NOT Remote Desktop.

禁止实现：

Screen Capture

Desktop Streaming

RDP

VNC

Remote Framebuffer

Image Transfer

应用必须：

Run locally

Render locally

RemoteServer只能提供：

Data

State

Storage

Compute API
12. 推荐本地项目结构（开发环境）

实际开发时建议初始化以下独立项目：

RemoteOS
│
├── RemoteOS.Client
│
├── RemoteOS.Core
│
├── RemoteOS.App.SDK
│
├── RemoteOS.BuiltInApps
│
│   ├── RemoteBrowser
│   ├── RemoteTerminal
│   └── RemoteExplorer
│
├── RemoteOS.Server
│
├── RemoteOS.Protocol
│
└── RemoteOS.Tools


说明：

这些项目只是代码组织方式。

最终产品不是多个程序组合。

用户看到的是：

RemoteOS
13. 最终定位

一句话：

RemoteOS 是一个跨平台云原生桌面操作系统环境，它提供类似 Windows 的桌面体验，由 RemoteOS Runtime 管理应用，应用界面本地渲染，而用户数据、状态和计算能力可以同步到 RemoteServer。

核心研发方向：

RemoteOS Runtime

RemoteOS Window Manager

RemoteOS App SDK

RemoteOS Protocol

RemoteServer Platform