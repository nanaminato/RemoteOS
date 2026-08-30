<div align="center">

# RemoteOS

**Cloud-Native Desktop Operating System Environment**

[![Avalonia](https://img.shields.io/badge/Avalonia-12.1.0-blue)](https://avaloniaui.net/)
[![dotnet](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![ASP.NET Core](https://img.shields.io/badge/ASP.NET%20Core-10.0-green)](https://dotnet.microsoft.com/)
[![License: RNCL](https://img.shields.io/badge/License-RNCL-blue)](./LICENSE)

[中文](./README.md) · [日本語](./README.ja.md)

</div>

---

## ✨ Introduction

**RemoteOS** is a cross-platform, cloud-native desktop operating system environment that uses a **State-Sync** model instead of pixel streaming. The client renders the UI locally while the server provides cloud capabilities (accounts, storage, synchronization, remote runtime), giving users a consistent desktop experience across any device.

**RemoteOS is NOT** a remote desktop tool (RDP/VNC/Screen Streaming). It transmits system state, application state, and user interaction intent — not screen pixels.

### Key Features

- 🖥️ **Cross-Platform Desktop Shell** — Based on Avalonia with a Windows 11-inspired interface
- 🌐 **Cloud-Native Architecture** — Client/Server separation; server runs on both Linux and Windows Server
- 🔐 **Host OS Identity Integration** — Reuses host system users and permissions (Windows LogonUser / Linux PAM)
- 🪟 **Window Management System** — Complete window lifecycle: create, move, resize, minimize/maximize, Z-order, modal dialogs
- 🧩 **Application SDK** — Applications plug in via the `IRemoteApplication` interface with unified window management and lifecycle
- 🔌 **SignalR Real-Time Communication** — Applications like Terminal use SignalR Hubs for real-time bidirectional interaction
- 🐳 **Docker Management** — Remote Docker Engine detection, container/image/Stack management
- 🛡️ **Process Guardian** — Guarded workloads, health checks, auto-recovery, native service management
- 🌍 **Multi-Language Support** — Built-in language packs for Chinese, English, and Japanese
- 🔧 **Developer Extensibility** — Install and manage custom application packages via the `DevCli` tool

---

## 🏗️ Architecture Overview

```
┌─────────────────────────────────────────────────────────┐
│                    RemoteOS.Client                       │
│          (Avalonia Desktop Shell · Local Render)         │
│                                                         │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌───────────┐  │
│  │ Explorer │ │ Terminal │ │ Browser  │ │    ...    │  │
│  └────┬─────┘ └────┬─────┘ └────┬─────┘ └─────┬─────┘  │
│       │             │            │              │       │
│  ┌────┴─────────────┴────────────┴──────────────┴────┐  │
│  │              Application Runtime / SDK              │  │
│  └──────────────────────────┬────────────────────────┘  │
│                              │                           │
│  ┌──────────────────────────┴────────────────────────┐  │
│  │              Window Manager (RemoteWindow)          │  │
│  └──────────────────────────┬────────────────────────┘  │
│                             │                            │
│  ┌──────────────────────────┴────────────────────────┐  │
│  │                    Protocol (DTOs)                   │  │
│  └──────────────────────────┬────────────────────────┘  │
└──────────────────────────────┼──────────────────────────┘
                               │ HTTP REST / SignalR
                               ▼
┌─────────────────────────────────────────────────────────┐
│                   RemoteOS.Server                        │
│            (ASP.NET Core · Cloud Backend · Cross-Platform)│
│                                                         │
│  ┌────────┐ ┌────────┐ ┌────────┐ ┌───────┐ ┌──────┐  │
│  │  Auth  │ │Workspace│ │ Storage│ │Files  │ │Terminal│  │
│  └────────┘ └────────┘ └────────┘ └───────┘ └──────┘  │
│                                                         │
│  ┌──────────────┐ ┌────────────────┐ ┌──────────────┐  │
│  │   Docker     │ │ ProcessGuardian│ │ SystemMonitor │  │
│  └──────────────┘ └────────────────┘ └──────────────┘  │
│                                                         │
│  ┌─────────────────────────────────────────────────┐    │
│  │         OS Abstraction Layer                     │    │
│  │  (IIdentityProvider · ISystemMetricsProvider …)  │    │
│  └─────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────┐
│              RemoteOS.Guardian.Agent                     │
│       (Standalone Process · Guarded Workloads · Native)  │
└─────────────────────────────────────────────────────────┘
```

---

## 🛠️ Tech Stack

| Component | Technology | Version |
|-----------|------------|---------|
| UI Framework | [Avalonia UI](https://avaloniaui.net/) | 12.1.0 |
| MVVM | CommunityToolkit.Mvvm | 8.4.2 |
| Framework | .NET | 10.0 |
| Server | ASP.NET Core | 10.0 |
| Realtime | SignalR | 10.0 |
| Authentication | JWT Bearer | — |
| Persistence | EF Core + SQLite | 10.0 |
| Terminal Control | RoyalTerminal (Avalonia + PTY) | 0.4.0 |
| Browser | Avalonia.Controls.WebView | 12.0.1 |
| File Manager UI | Jaya File Manager (BSD-3 License) | — |
| Video Playback | LibVLCSharp.Avalonia | 3.10.0 |

---

## 📁 Project Structure

```
RemoteOS/
├── Client/
│   ├── RemoteOS.Client/          # Desktop Shell + Built-in Apps (class library)
│   │   ├── Apps/                 # Built-in applications
│   │   │   ├── Explorer/         # File Manager
│   │   │   ├── Terminal/         # Terminal
│   │   │   ├── Browser/          # Browser
│   │   │   ├── Settings/         # Settings Center
│   │   │   ├── TaskManager/      # Task Manager
│   │   │   ├── Docker/           # Docker Manager
│   │   │   ├── ProcessGuardian/  # Process Guardian
│   │   │   ├── Notepad/          # Notepad
│   │   │   ├── CodeEditor/       # Code Editor
│   │   │   ├── ImageViewer/      # Image Viewer
│   │   │   ├── Welcome/          # Welcome Page
│   │   │   └── AppInstaller/     # App Installer
│   │   ├── Localization/         # Language Resources (en-US / zh-CN / ja-JP)
│   │   ├── Services/             # Auth, Permissions, Dev Mode services
│   │   ├── ViewModels/           # Shell / Login ViewModels
│   │   └── Views/                # Shell / Login / MainWindow Views
│   └── RemoteOS.Client.Desktop/  # Platform Entry Point (WinExe)
├── Framework/
│   ├── RemoteOS.Core/            # Platform-agnostic primitives (geometry, window, app models)
│   ├── RemoteOS.UI/              # Avalonia shared themes/styles
│   ├── RemoteOS.WindowManager/   # Window Manager + RemoteWindow control
│   ├── RemoteOS.App.SDK/         # App development API (AppContext / IRemoteApplication)
│   └── RemoteOS.Runtime/         # App runtime (ApplicationManager)
├── Shared/
│   └── RemoteOS.Protocol/        # Communication contracts (DTOs / routes / Hub interfaces)
├── RemoteOS.Server/              # Server (ASP.NET Core)
├── RemoteOS.Guardian.Agent/      # Process guardian standalone (native service management)
├── Tools/
│   └── RemoteOS.DevCli/          # Developer CLI tool
├── examples/
│   ├── VideoPlayer/              # Video Player example app
│   └── ServerMonitor/            # Server Monitor example app
├── docs/                         # Detailed design documentation
├── deployment/                   # Deployment scripts (Linux / Windows)
├── Directory.Packages.props      # Central package management
└── RemoteOS.sln                  # Solution file
```

---

## 🧩 Built-in Applications

| Application | Description | Status |
|-------------|-------------|--------|
| **Welcome** | Welcome onboarding page, validates Runtime and WindowManager | ✅ Implemented |
| **Notepad** | Text file editing (encoding-aware open/save) | ✅ Implemented |
| **Code Editor** | Code file editing (syntax highlighting) | ✅ Implemented |
| **Image Viewer** | Image file browsing (zoom and scroll) | ✅ Implemented |
| **Settings** | System settings center (5 categories, preferences persisted to Workspace) | ✅ Implemented |
| **Terminal** | Remote Terminal (Remote Mode: SignalR + PTY; Local Mode fallback) | ✅ Implemented |
| **Explorer** | Remote File Manager (REST API + host OS permission reuse) | ✅ Implemented |
| **Browser** | Built-in Browser (bookmarks/history persistence) | ✅ Implemented |
| **Task Manager** | Remote Task Manager (CPU/Memory/Disk/Network/GPU + process list) | ✅ Implemented |
| **Docker Manager** | Remote Docker Engine management (container/image/Stack/network/volume) | 🚧 Partial |
| **Process Guardian** | Process guardian (health checks, auto-recovery, native service management) | 🚧 Partial |
| **App Installer** | App package installation and management | ✅ Implemented |
| **Registry** | Schema-approved configuration registry browser (read-only first stage) | ✅ Implemented |

---

## 🚀 Getting Started

### Prerequisites

- **.NET 10.0 SDK** or later
- **OS**: Windows 10/11, Windows Server 2016+, Ubuntu 20.04+
- (Optional) Visual Studio 2022+ or JetBrains Rider

### 1. Clone the Repository

```bash
git clone <repository-url>
cd RemoteOS
```

### 2. Start the Server

```bash
cd RemoteOS.Server

# Run in development mode (default: http://localhost:5000)
dotnet run
```

> ⚠️ **Production**: Always change `Jwt:Secret` in `appsettings.json` to at least a 32-character random string.

### 3. Start the Client

```bash
cd Client/RemoteOS.Client.Desktop
dotnet run
```

The client will open a login dialog. Enter your host system username and password to log in.

---

## 📖 Documentation

| Document | Description |
|----------|-------------|
| [RemoteOS.Architecture.md](./docs/architecture/RemoteOS.Architecture.md) | Architecture principles, module dependencies, layered architecture |
| [RemoteOS.Protocol.md](./docs/architecture/RemoteOS.Protocol.md) | Communication contracts, REST/SignalR, serialization conventions |
| [RemoteOS.Workspace.md](./docs/architecture/RemoteOS.Workspace.md) | User/Workspace/Session/Device, multi-device model |
| [RemoteOS.Authentication.md](./docs/platform/RemoteOS.Authentication.md) | Login system, identity model, OS user integration |
| [RemoteOS.Desktop.md](./docs/desktop/RemoteOS.Desktop.md) | Desktop shell, window control, modal dialogs |
| [RemoteOS.Terminal.md](./docs/applications/RemoteOS.Terminal.md) | Terminal app, SignalR, PTY, session management |
| [RemoteOS.Explorer.md](./docs/applications/RemoteOS.Explorer.md) | File manager, REST API, permission reuse |
| [RemoteOS.Browser.md](./docs/applications/RemoteOS.Browser.md) | Browser, bookmarks/history |
| [RemoteOS.Settings.md](./docs/desktop/RemoteOS.Settings.md) | Settings center, preference persistence, multi-device sync |
| [RemoteOS.TaskManager.md](./docs/applications/RemoteOS.TaskManager.md) | Task manager, system metrics, process management |
| [RemoteOS.DockerManager.md](./docs/applications/RemoteOS.DockerManager.md) | Docker manager, container/image/Stack management |
| [RemoteOS.ProcessGuardian.md](./docs/applications/RemoteOS.ProcessGuardian.md) | Process guardian, health checks, native service management |
| [RemoteOS.Storage.md](./docs/platform/RemoteOS.Storage.md) | Server persistence, EF Core + SQLite |
| [RemoteOS.Security.md](./docs/platform/RemoteOS.Security.md) | Security design, privilege elevation, dangerous operations |
| [RemoteOS.Localization.md](./docs/desktop/RemoteOS.Localization.md) | Multi-language mechanism, language pack structure |
| [RemoteOS.Develop.md](./docs/development/RemoteOS.Develop.md) | Developer quick start, code structure, debugging guide |
| [RemoteOS.DeveloperMode.md](./docs/development/RemoteOS.DeveloperMode.md) | Developer mode, DevCli, app package publishing |
| [RemoteOS.BuiltInApplication.Conventions.md](./docs/development/RemoteOS.BuiltInApplication.Conventions.md) | Built-in app design constraints, i18n, cross-platform |
| [RemoteOS.ApplicationCompatibility.md](./docs/development/RemoteOS.ApplicationCompatibility.md) | Application compatibility, platform adaptation, fallback |
| [RemoteOS.NetworkInspector.md](./docs/applications/RemoteOS.NetworkInspector.md) | Network inspector, diagnostics tool, network analysis |
| [RemoteOS.Login.md](./docs/platform/RemoteOS.Login.md) | Login module implementation details, mstsc-style login window |
| [RemoteOS.md](./docs/README.md) | Project structure, code map, current progress |

---

## 🔧 Development & Extensibility

RemoteOS supports building custom application packages (`.roapp`) that can be installed into the RemoteOS Shell via the `DevCli` tool.

### Build, Install, and Watch an Example App

```bash
# Set the development token (or pass it as a parameter)
export REMOTEOS_DEV_TOKEN="<pairing-token>"

# Package and install an app without per-application PowerShell scripts
dotnet run --project Tools/RemoteOS.DevCli -- pack ./examples/VideoPlayer --runtime win-x64 --configuration Release --install

# Watch source changes, package, and update automatically
dotnet run --project Tools/RemoteOS.DevCli -- watch ./examples/VideoPlayer --runtime win-x64 --configuration Debug
```

`pack` creates the `.roapp` in the application's `artifacts/` directory; pure managed applications can omit `--runtime`. See [Developer Mode](./docs/development/RemoteOS.DeveloperMode.md) for the third-party packaging command reference.

### App Development Model

```csharp
// Implement the IRemoteApplication interface or inherit RemoteApplicationBase
public class MyApp : RemoteApplicationBase
{
    public override string Id => "com.example.myapp";
    public override string DisplayName => "My Application";

    public override void Activate(AppContext context)
    {
        // Create a window
        context.ShowWindow("My Window", contentFactory: () => new MyView());
    }
}
```

---

## 🌍 Multi-Language

RemoteOS includes built-in support for three languages:

| Language | Code | Language Pack Path |
|----------|------|-------------------|
| 🇨🇳 Simplified Chinese | `zh-CN` | `Client/RemoteOS.Client/Localization/zh-CN/` |
| 🇺🇸 English | `en-US` | `Client/RemoteOS.Client/Localization/en-US/` |
| 🇯🇵 Japanese | `ja-JP` | `Client/RemoteOS.Client/Localization/ja-JP/` |

Language packs use a JSON key-value structure. The UI updates in real-time when the language is switched.

---

## ⚠️ Third-Party Notices

This project uses the following third-party resources:

- **Jaya File Manager** (BSD 3-Clause License) — File manager UI structure ported from Jaya. See [THIRD_PARTY_NOTICES.md](./THIRD_PARTY_NOTICES.md).
- For NuGet package license information, please refer to each package's page.

---

## 📄 License

This project is licensed under the **RemoteOS Non-Commercial Source-Available License**.

**Allowed**: Free use, modification, development, study, and non-commercial distribution.
**Prohibited**: Commercial sale, resale, SaaS hosting, or other commercial use.

The author reserves all commercial rights. For commercial licensing, please contact the author directly.

See the [`LICENSE`](./LICENSE) file for details. Third-party component licenses are listed in [`THIRD_PARTY_NOTICES.md`](./THIRD_PARTY_NOTICES.md).

---

## 🤝 Contributing

Contributions are welcome! Please:

1. Fork this repository
2. Create your feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add: amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Create a Pull Request

---

<div align="center">

**RemoteOS** — Desktops Beyond Devices. State Defines Experience.

</div>
