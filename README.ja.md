<div align="center">

# RemoteOS

**クラウドネイティブデスクトップオペレーティングシステム環境**

[![Avalonia](https://img.shields.io/badge/Avalonia-12.1.0-blue)](https://avaloniaui.net/)
[![dotnet](https://img.shields.io/badge/.NET-10.0-purple)](https://dotnet.microsoft.com/)
[![ASP.NET Core](https://img.shields.io/badge/ASP.NET%20Core-10.0-green)](https://dotnet.microsoft.com/)
[![License: RNCL](https://img.shields.io/badge/License-RNCL-blue)](./LICENSE)

[中文](./README.md) · [English](./README.en.md)

</div>

---

## ✨ プロジェクト紹介

**RemoteOS** はクロスプラットフォームなクラウドネイティブデスクトップOS環境です。ピクセルストリーミングではなく **状態同期（State-Sync）** モデルを採用しています。クライアントはローカルでUIを描画し、サーバーはクラウド機能（アカウント、ストレージ、同期、リモートランタイム）を提供し、どのデバイスでも一貫したデスクトップ体験を実現します。

**RemoteOS は** リモートデスクトップツール（RDP/VNC/Screen Streaming）ではありません。システム状態、アプリケーション状態、ユーザー操作意図を伝送し、画面ピクセルは伝送しません。

### 主な特徴

- 🖥️ **クロスプラットフォームデスクトップシェル** — Avaloniaベース、Windows 11スタイルのインターフェース
- 🌐 **クラウドネイティブアーキテクチャ** — Client/Server分離、サーバーはLinuxとWindows Serverの両方で稼働
- 🔐 **ホストOSアイデンティティ統合** — ホストシステムのユーザーと権限体系を活用（Windows LogonUser / Linux PAM）
- 🪟 **ウィンドウ管理システム** — ウィンドウの完全ライフサイクル：作成、移動、リサイズ、最小化/最大化、Z-Order、モーダルダイアログ
- 🧩 **アプリケーションSDK** — `IRemoteApplication`インターフェース経由で統一されたウィンドウ管理とライフサイクルを提供
- 🔌 **SignalRリアルタイム通信** — ターミナルなどのアプリがSignalR Hub経由でリアルタイム双方向通信
- 🐳 **Docker管理** — リモートDocker Engineの検出、コンテナ/イメージ/Stack管理
- 🛡️ **プロセスガーディアン** — 保護されたワークロード、ヘルスチェック、自動復旧、ネイティブサービス管理
- 🌍 **多言語対応** — 中国語、英語、日本語の言語パックを内蔵
- 🔧 **デベロッパー拡張** — `DevCli`ツール経由でカスタムアプリケーションパッケージのインストールと管理に対応

---

## 🏗️ アーキテクチャ概要

```
┌─────────────────────────────────────────────────────────┐
│                  RemoteOS.Client                        │
│          (Avalonia Desktop Shell · ローカル描画)          │
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
│         (ASP.NET Core · クラウドバックエンド · クロスプラットフォーム) │
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
│       (独立プロセス · 保護ワークロード · ネイティブサービス) │
└─────────────────────────────────────────────────────────┘
```

---

## 🛠️ 技術スタック

| コンポーネント | 技術 | バージョン |
|---------------|------|-----------|
| UIフレームワーク | [Avalonia UI](https://avaloniaui.net/) | 12.1.0 |
| MVVM | CommunityToolkit.Mvvm | 8.4.2 |
| フレームワーク | .NET | 10.0 |
| サーバー | ASP.NET Core | 10.0 |
| リアルタイム通信 | SignalR | 10.0 |
| 認証 | JWT Bearer | — |
| パーシステンス | EF Core + SQLite | 10.0 |
| ターミナルコントロール | RoyalTerminal (Avalonia + PTY) | 0.4.0 |
| ブラウザ | Avalonia.Controls.WebView | 12.0.1 |
| ファイルマネージャUI | Jaya File Manager (BSD-3ライセンス) | — |
| ビデオ再生 | LibVLCSharp.Avalonia | 3.10.0 |

---

## 📁 プロジェクト構造

```
RemoteOS/
├── Client/
│   ├── RemoteOS.Client/          # デスクトップシェル + 内蔵アプリ（クラスライブラリ）
│   │   ├── Apps/                 # 内蔵アプリケーション
│   │   │   ├── Explorer/         # ファイルマネージャ
│   │   │   ├── Terminal/         # ターミナル
│   │   │   ├── Browser/          # ブラウザ
│   │   │   ├── Settings/         # 設定センター
│   │   │   ├── TaskManager/      # タスクマネージャ
│   │   │   ├── Docker/           # Dockerマネージャ
│   │   │   ├── ProcessGuardian/  # プロセスガーディアン
│   │   │   ├── Notepad/          # メモ帳
│   │   │   ├── CodeEditor/       # コードエディタ
│   │   │   ├── ImageViewer/      # 画像ビューア
│   │   │   ├── Welcome/          # ウェルカムページ
│   │   │   └── AppInstaller/     # アプリインストーラー
│   │   ├── Localization/         # 言語リソース（en-US / zh-CN / ja-JP）
│   │   ├── Services/             # 認証、権限、開発モードサービス
│   │   ├── ViewModels/           # Shell / Login ViewModel
│   │   └── Views/                # Shell / Login / MainWindowビュー
│   └── RemoteOS.Client.Desktop/  # プラットフォームエントリーポイント（WinExe）
├── Framework/
│   ├── RemoteOS.Core/            # プラットフォーム非依存プリミティブ（幾何、ウィンドウ、アプリモデル）
│   ├── RemoteOS.UI/              # Avalonia共有テーマ/スタイル
│   ├── RemoteOS.WindowManager/   # ウィンドウマネージャ + RemoteWindowコントロール
│   ├── RemoteOS.App.SDK/         # アプリ開発API（AppContext / IRemoteApplication）
│   └── RemoteOS.Runtime/         # アプリランタイム（ApplicationManager）
├── Shared/
│   └── RemoteOS.Protocol/        # 通信契約（DTO / ルート / Hubインターフェース）
├── RemoteOS.Server/              # サーバー（ASP.NET Core）
├── RemoteOS.Guardian.Agent/      # プロセスガーディアン独立プロセス（ネイティブサービス管理）
├── Tools/
│   └── RemoteOS.DevCli/          # デベロッパーCLIツール
├── examples/
│   ├── VideoPlayer/              # ビデオプレーヤーサンプルアプリ
│   └── ServerMonitor/            # サーバーモニターサンプルアプリ
├── docs/                         # 詳細設計ドキュメント
├── deployment/                   # デプロイスクリプト（Linux / Windows）
├── Directory.Packages.props      # 中央パッケージ管理
└── RemoteOS.sln                  # ソリューションファイル
```

---

## 🧩 内蔵アプリケーション

| アプリケーション | 説明 | ステータス |
|-----------------|------|-----------|
| **Welcome** | ウェルカムオンボーディングページ、RuntimeとWindowManagerの検証 | ✅ 実装済み |
| **Notepad** | テキストファイル編集（エンコーディング対応オープン/セーブ） | ✅ 実装済み |
| **Code Editor** | コードファイル編集（シンタックスハイライト） | ✅ 実装済み |
| **Image Viewer** | 画像ファイル閲覧（ズームとスクロール） | ✅ 実装済み |
| **Settings** | システム設定センター（5カテゴリ、設定はWorkspaceに永続化） | ✅ 実装済み |
| **Terminal** | リモートターミナル（Remote Mode: SignalR + PTY; Local Modeフォールバック） | ✅ 実装済み |
| **Explorer** | リモートファイルマネージャ（REST API + ホストOS権限活用） | ✅ 実装済み |
| **Browser** | 内蔵ブラウザ（ブックマーク/履歴の永続化） | ✅ 実装済み |
| **Task Manager** | リモートタスクマネージャ（CPU/メモリ/ディスク/ネットワーク/GPU + プロセスリスト） | ✅ 実装済み |
| **Docker Manager** | リモートDocker Engine管理（コンテナ/イメージ/Stack/ネットワーク/ボリューム） | 🚧 部分実装 |
| **Process Guardian** | プロセスガーディアン（ヘルスチェック、自動復旧、ネイティブサービス管理） | 🚧 部分実装 |
| **App Installer** | アプリパッケージのインストールと管理 | ✅ 実装済み |

---

## 🚀 クイックスタート

### 前提条件

- **.NET 10.0 SDK** 以降
- **OS**: Windows 10/11、Windows Server 2016+、Ubuntu 20.04+
- （任意）Visual Studio 2022+ または JetBrains Rider

### 1. リポジトリのクローン

```bash
git clone <repository-url>
cd RemoteOS
```

### 2. サーバーの起動

```bash
cd RemoteOS.Server

# 開発モードで実行（デフォルト: http://localhost:5000）
dotnet run
```

> ⚠️ **本番環境**: `appsettings.json` の `Jwt:Secret` を少なくとも32文字のランダム文字列に変更してください。

### 3. クライアントの起動

```bash
cd Client/RemoteOS.Client.Desktop
dotnet run
```

クライアントにログインダイアログが表示されます。ホストシステムのユーザー名とパスワードを入力してログインしてください。

---

## 📖 ドキュメント

| ドキュメント | 説明 |
|-------------|------|
| [RemoteOS.Architecture.md](./docs/RemoteOS.Architecture.md) | アーキテクチャ原則、モジュール依存、階層アーキテクチャ |
| [RemoteOS.Protocol.md](./docs/RemoteOS.Protocol.md) | 通信契約、REST/SignalR、シリアライズ規約 |
| [RemoteOS.Workspace.md](./docs/RemoteOS.Workspace.md) | ユーザー/Workspace/Session/Device、マルチデバイスモデル |
| [RemoteOS.Authentication.md](./docs/RemoteOS.Authentication.md) | ログインシステム、アイデンティティモデル、OSユーザー統合 |
| [RemoteOS.Desktop.md](./docs/RemoteOS.Desktop.md) | デスクトップシェル、ウィンドウ制御、モーダルダイアログ |
| [RemoteOS.Terminal.md](./docs/RemoteOS.Terminal.md) | ターミナルアプリ、SignalR、PTY、セッション管理 |
| [RemoteOS.Explorer.md](./docs/RemoteOS.Explorer.md) | ファイルマネージャ、REST API、権限活用 |
| [RemoteOS.Browser.md](./docs/RemoteOS.Browser.md) | ブラウザ、ブックマーク/履歴 |
| [RemoteOS.Settings.md](./docs/RemoteOS.Settings.md) | 設定センター、設定永続化、マルチデバイス同期 |
| [RemoteOS.TaskManager.md](./docs/RemoteOS.TaskManager.md) | タスクマネージャ、システムメトリクス、プロセス管理 |
| [RemoteOS.DockerManager.md](./docs/RemoteOS.DockerManager.md) | Dockerマネージャ、コンテナ/イメージ/Stack管理 |
| [RemoteOS.ProcessGuardian.md](./docs/RemoteOS.ProcessGuardian.md) | プロセスガーディアン、ヘルスチェック、ネイティブサービス管理 |
| [RemoteOS.Storage.md](./docs/RemoteOS.Storage.md) | サーバーパーシステンス、EF Core + SQLite |
| [RemoteOS.Security.md](./docs/RemoteOS.Security.md) | セキュリティ設計、権限昇格、危険操作 |
| [RemoteOS.Localization.md](./docs/RemoteOS.Localization.md) | 多言語メカニズム、言語パック構造 |
| [RemoteOS.Develop.md](./docs/RemoteOS.Develop.md) | デベロッパークイックスタート、コード構造、デバッグガイド |
| [RemoteOS.DeveloperMode.md](./docs/RemoteOS.DeveloperMode.md) | デベロッパーモード、DevCli、アプリパッケージ公開 |
| [RemoteOS.BuiltInApplication.Conventions.md](./docs/RemoteOS.BuiltInApplication.Conventions.md) | 内蔵アプリ設計制約、国際化、クロスプラットフォーム |
| [RemoteOS.ApplicationCompatibility.md](./docs/RemoteOS.ApplicationCompatibility.md) | アプリケーション互換性、プラットフォーム適応、フォールバック |
| [RemoteOS.NetworkInspector.md](./docs/RemoteOS.NetworkInspector.md) | ネットワークインスペクター、診断ツール、ネットワーク分析 |
| [RemoteOS.Login.md](./docs/RemoteOS.Login.md) | ログインモジュール実装詳細、mstscスタイルログインウィンドウ |
| [RemoteOS.md](./docs/RemoteOS.md) | プロジェクト構造、コードマップ、現在の進捗 |

---

## 🔧 開発と拡張

RemoteOSでは、`DevCli`ツール経由でRemoteOS Shellにインストールできるカスタムアプリケーションパッケージ（`.roapp`）の構築がサポートされています。

### サンプルアプリのビルド

```bash
cd examples/VideoPlayer
./build-package.ps1
```

### アプリパッケージのインストール

```bash
# 開発トークンを設定（パラメータで渡すことも可能）
export REMOTEOS_DEV_TOKEN="<pairing-token>"

# アプリをインストール
dotnet run --project Tools/RemoteOS.DevCli -- install ./examples/VideoPlayer/bin/Release/net10.0/RemoteOS.Example.VideoPlayer.roapp

# 変更を監視して自動更新
dotnet run --project Tools/RemoteOS.DevCli -- watch ./examples/VideoPlayer/bin/Release/net10.0/RemoteOS.Example.VideoPlayer.roapp
```

### アプリ開発モデル

```csharp
// IRemoteApplicationインターフェースを実装するか、RemoteApplicationBaseを継承
public class MyApp : RemoteApplicationBase
{
    public override string Id => "com.example.myapp";
    public override string DisplayName => "My Application";

    public override void Activate(AppContext context)
    {
        // ウィンドウを作成
        context.ShowWindow("My Window", contentFactory: () => new MyView());
    }
}
```

---

## 🌍 多言語

RemoteOSには3つの言語のサポートが内蔵されています：

| 言語 | コード | 言語パックのパス |
|------|--------|----------------|
| 🇨🇳 簡体字中国語 | `zh-CN` | `Client/RemoteOS.Client/Localization/zh-CN/` |
| 🇺🇸 英語 | `en-US` | `Client/RemoteOS.Client/Localization/en-US/` |
| 🇯🇵 日本語 | `ja-JP` | `Client/RemoteOS.Client/Localization/ja-JP/` |

言語パックはJSONキー値構造を使用しています。言語切り替え後、UIはリアルタイムで更新されます。

---

## ⚠️ 第三者通知

このプロジェクトには以下の第三者リソースが使用されています：

- **Jaya File Manager**（BSD 3-Clause License）— ファイルマネージャUI構造をJayaから移植。詳細は [THIRD_PARTY_NOTICES.md](./THIRD_PARTY_NOTICES.md) をご覧ください。
- NuGetパッケージのライセンス情報については、各パッケージのページを参照してください。

---

## 📄 ライセンス

このプロジェクトは **RemoteOS Non-Commercial Source-Available License** のもとでライセンスされています。

**許可**：無料使用、変更、開発、学習、非営利目的での配布。
**禁止**：商業的販売、再販、SaaSホスティング、その他の商業用途。

作者はすべての商業的権利を留保します。商業ライセンスについては、直接作者にお問い合わせください。

詳細は [`LICENSE`](./LICENSE) ファイルを参照してください。第三者コンポーネントのライセンスについては [`THIRD_PARTY_NOTICES.md`](./THIRD_PARTY_NOTICES.md) を参照してください。

---

## 🤝 コントリビューション

コントリビューションを歓迎します！以下の手順でお願いします：

1. このリポジトリをフォーク
2. 機能ブランチを作成（`git checkout -b feature/amazing-feature`）
3. 変更をコミット（`git commit -m 'Add: amazing feature'`）
4. ブランチにプッシュ（`git push origin feature/amazing-feature`）
5. Pull Requestを作成

---

<div align="center">

**RemoteOS** — デスクトップをデバイスの壁を越えて。状態が体験を定義する。

</div>
