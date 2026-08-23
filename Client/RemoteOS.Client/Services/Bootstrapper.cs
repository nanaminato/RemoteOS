using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Client.Apps;
using Client.Apps.CodeEditor;
using Client.Apps.ImageViewer;
using Client.Apps.Notepad;
using Client.Apps.Settings;
using Client.Apps.Terminal;
using Client.Apps.Welcome;
using Client.Services.Auth;
using Client.Services.AppPermissions;
using Client.Services.AppSettings;
using Client.Services.AppPackages;
using Client.Services.Developer;
using Client.Services.DesktopRestore;
using Client.Services.Diagnostics;
using Client.Services.WindowLayout;
using Client.ViewModels.Login;
using Client.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Runtime;
using RemoteOS.WindowManager;

namespace Client.Services;

/// <summary>Composes the client-side DI container and registers built-in applications.</summary>
public static class Bootstrapper
{
    public static IServiceProvider Build(Application app)
    {
        var services = new ServiceCollection();

        var windowManager = new WindowManager();
        services.AddSingleton(windowManager);
        services.AddSingleton<IWindowManager>(windowManager);
        services.AddSingleton<LocalLanguageStore>();
        services.AddSingleton<LoginNotificationPreferenceStore>();
        services.AddSingleton<ShellSettings>();
        services.AddSingleton<LocalizationService>();
        services.AddSingleton<LoginLocalizationService>();
        services.AddSingleton<ISystemLanguage>(sp => sp.GetRequiredService<LocalizationService>());
        services.AddTransient<AcceptLanguageHandler>();
        services.AddSingleton<ApplicationManager>(sp =>
            new ApplicationManager(sp.GetRequiredService<IWindowManager>(), sp));
        services.AddSingleton<IAppActivationService>(sp => sp.GetRequiredService<ApplicationManager>());

        // Auth（登录模块）：typed HttpClient + 仅内存认证会话 + 登录视图模型。
        services.AddHttpClient<IRemoteOsClient, RemoteOsClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "auth"))
            .AddHttpMessageHandler<AcceptLanguageHandler>();
        services.AddHttpClient<ITerminalSettingsClient, TerminalSettingsClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "terminal-settings"))
            .AddHttpMessageHandler<AcceptLanguageHandler>();
        services.AddSingleton<IRememberedSessionStore, RememberedSessionStore>();
        services.AddSingleton<IAuthSession, AuthSession>();
        services.AddSingleton<ApplicationCompatibilityService>();
        services.AddSingleton<IApplicationCompatibilityEvaluator>(sp => sp.GetRequiredService<ApplicationCompatibilityService>());
        services.AddSingleton<IApplicationCompatibilityNotifier>(sp => sp.GetRequiredService<ApplicationCompatibilityService>());
        services.AddSingleton<LoginViewModel>();

        // Explorer（文件管理器）：typed HttpClient（JWT from IAuthSession）+ 应用注册。
        services.AddHttpClient<Client.Apps.Explorer.IExplorerClient, Client.Apps.Explorer.ExplorerClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "explorer"))
            .AddHttpMessageHandler<AcceptLanguageHandler>();
        services.AddSingleton<Client.Apps.Explorer.IRemoteFileClipboard, Client.Apps.Explorer.RemoteFileClipboard>();

        // Browser（浏览器）：typed HttpClient（JWT from IAuthSession）+ 应用注册。
        // NativeWebView 用平台原生引擎（Win=WebView2/macOS=WKWebView/Linux=WebKitGTK），网页内容走客户端网络；
        // Server 仅持久化书签与历史记录（按用户隔离）。
        services.AddHttpClient<Client.Apps.Browser.IBrowserClient, Client.Apps.Browser.BrowserClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "browser"))
            .AddHttpMessageHandler<AcceptLanguageHandler>();

        // TaskManager（任务管理器）：typed HttpClient（JWT from IAuthSession，与 Browser/Explorer 同模式）。
        // 拉取服务端采集的宿主 OS 资源占用（CPU/内存/磁盘/网络/GPU）与进程列表；结束进程权限不足提示需在宿主 OS 提权。
        services.AddHttpClient<Client.Apps.TaskManager.ITaskManagerClient, Client.Apps.TaskManager.TaskManagerClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "task-manager"))
            .AddHttpMessageHandler<AcceptLanguageHandler>();
        services.AddHttpClient<Client.Apps.Docker.IRemoteDockerClient, Client.Apps.Docker.RemoteDockerClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "docker"))
            .AddHttpMessageHandler<AcceptLanguageHandler>();
        services.AddHttpClient<Client.Apps.ProcessGuardian.IProcessGuardianClient, Client.Apps.ProcessGuardian.ProcessGuardianClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "process-guardian"))
            .AddHttpMessageHandler<AcceptLanguageHandler>();
        services.AddHttpClient<Client.Apps.Firewall.IRemoteFirewallClient, Client.Apps.Firewall.RemoteFirewallClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "firewall"))
            .AddHttpMessageHandler<AcceptLanguageHandler>();

        // Settings（设置中心）：typed HttpClient（JWT from IAuthSession，与 Browser/Explorer 同模式）。
        // 偏好持久化到服务端 Workspace（/workspaces/{id}/preferences），多设备共享。
        services.AddHttpClient<ISettingsClient, SettingsClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "settings"))
            .AddHttpMessageHandler<AcceptLanguageHandler>();
        services.AddHttpClient<IImageMirrorClient, ImageMirrorClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "image-mirrors"))
            .AddHttpMessageHandler<AcceptLanguageHandler>();
        services.AddHttpClient<IWallpaperClient, WorkspaceWallpaperClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "wallpaper"))
            .AddHttpMessageHandler<AcceptLanguageHandler>();
        services.AddHttpClient<IWindowLayoutClient, WindowLayoutClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "window-layout"))
            .AddHttpMessageHandler<AcceptLanguageHandler>();
        services.AddSingleton<WindowLayoutStore>();
        services.AddSingleton<DefaultAppRegistry>();
        services.AddSingleton<IUriSchemeDefaultResolver>(sp => sp.GetRequiredService<DefaultAppRegistry>());
        services.AddSingleton<IUriSchemeRoutingUi, UriSchemeRoutingUi>();
        services.AddSingleton<IAppActivationDiagnostics, UriSchemeRoutingDiagnostics>();
        services.AddSingleton<WallpaperService>();
        services.AddSingleton<TextEditorEncodingSettings>();
        services.AddSingleton<ITextFileSniffer, TextFileSniffer>();
        services.AddSingleton<IAppPermissionManager, JsonAppPermissionManager>();
        services.AddSingleton<IAppPermissionRequestService, AppPermissionRequestService>();
        services.AddSingleton<IAppDataManager, AppDataManager>();
        services.AddSingleton<DeveloperModeService>();
        // The session must be resolved only when diagnostics are used. Resolving it while an
        // auth HttpClient handler is constructed would recursively construct that same client.
        services.AddSingleton<NetworkDiagnosticsService>(sp => new NetworkDiagnosticsService(
            sp.GetRequiredService<DeveloperModeService>(),
            () => sp.GetRequiredService<IAuthSession>()));
        services.AddSingleton<NetworkInspectorWindowService>();
        services.AddHttpClient<IAppCapabilityClient, AppCapabilityClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "capabilities"))
            .AddHttpMessageHandler<AcceptLanguageHandler>();
        services.AddHttpClient<IAppSettingsClient, AppSettingsClient>()
            .AddHttpMessageHandler(sp => new NetworkDiagnosticsHandler(sp.GetRequiredService<NetworkDiagnosticsService>(), "app-settings"))
            .AddHttpMessageHandler<AcceptLanguageHandler>();
        services.AddSingleton<ISettingsNavigation, SettingsNavigationService>();
        services.AddSingleton<ExternalAppContextFactory>();
        services.AddSingleton<DeveloperPackageManager>();
        services.AddSingleton<AppPackageInstallerService>();
        services.AddSingleton<DeveloperBridgeService>();
        // Port forwarding owns local ssh processes and a device-local, non-secret settings file.
        // It is intentionally not part of Workspace preference synchronization.
        services.AddSingleton<Client.Apps.PortForwarding.PortForwardingSettingsStore>();
        services.AddSingleton<Client.Apps.PortForwarding.IPortForwardingService, Client.Apps.PortForwarding.PortForwardingService>();
        // PreferencesSync 监听登录态，登录后把服务端偏好应用到 ShellSettings + DefaultAppRegistry。
        services.AddSingleton<PreferencesSync>();

        // Built-in applications.
        services.AddSingleton<IRemoteApplication, WelcomeApp>();
        services.AddSingleton<IRemoteApplication, NotepadApp>();
        services.AddSingleton<IRemoteApplication, CodeEditorApp>();
        services.AddSingleton<IRemoteApplication, ImageViewerApp>();
        services.AddSingleton<IRemoteApplication, SettingsApp>();
        services.AddSingleton<TerminalApp>();
        services.AddSingleton<IRemoteApplication>(sp => sp.GetRequiredService<TerminalApp>());
        services.AddSingleton<IDesktopRestoreParticipant, TerminalDesktopRestoreParticipant>();
        services.AddSingleton<IRemoteApplication, Client.Apps.Explorer.ExplorerApp>();
        services.AddSingleton<IRemoteApplication, Client.Apps.Browser.BrowserApp>();
        services.AddSingleton<IRemoteApplication, Client.Apps.PortForwarding.PortForwardingApp>();
        services.AddSingleton<IRemoteApplication, Client.Apps.TaskManager.TaskManagerApp>();
        services.AddSingleton<IRemoteApplication, Client.Apps.Docker.DockerManagerApp>();
        services.AddSingleton<IRemoteApplication, Client.Apps.ProcessGuardian.ProcessGuardianApp>();
        services.AddSingleton<IRemoteApplication, Client.Apps.Firewall.FirewallApp>();
        services.AddSingleton<IRemoteApplication, Client.Apps.AppInstaller.AppInstallerApp>();

        services.AddSingleton<DesktopShellViewModel>(sp =>
        {
            var wm = sp.GetRequiredService<WindowManager>();
            var apps = sp.GetRequiredService<ApplicationManager>();
            var settings = sp.GetRequiredService<ShellSettings>();
            var localization = sp.GetRequiredService<LocalizationService>();
            var session = sp.GetRequiredService<IAuthSession>();
            Action shutdown = () =>
            {
                if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    desktop.Shutdown();
            };
            return new DesktopShellViewModel(
                wm, apps, settings, localization, session, shutdown,
                sp.GetRequiredService<DesktopRestoreOrchestrator>(),
                sp.GetRequiredService<Client.Apps.Explorer.IExplorerClient>(),
                sp.GetRequiredService<Client.Apps.Explorer.IRemoteFileClipboard>(),
                sp.GetRequiredService<DefaultAppRegistry>(),
                sp.GetRequiredService<ISettingsClient>(),
                sp.GetRequiredService<IAppActivationDiagnostics>(),
                sp.GetRequiredService<ITextFileSniffer>());
        });

        services.AddSingleton<DesktopRestoreOrchestrator>();

        var provider = services.BuildServiceProvider();

        // Create both language services before their respective windows and package contexts.
        // The login service is intentionally independent from the workspace language service.
        provider.GetRequiredService<LocalizationService>();
        provider.GetRequiredService<LoginLocalizationService>();

        windowManager.LayoutStore = provider.GetRequiredService<WindowLayoutStore>();

        // Register applications with the runtime.
        var manager = provider.GetRequiredService<ApplicationManager>();
        foreach (var application in provider.GetServices<IRemoteApplication>())
            manager.Register(application);

        // Development packages follow the same runtime registry as built-in applications.
        provider.GetRequiredService<DeveloperPackageManager>().LoadInstalled();
        provider.GetRequiredService<DeveloperBridgeService>();

        // Build the desktop / start menu entries.
        provider.GetRequiredService<DesktopShellViewModel>().PopulateDesktop();

        // Eagerly start preferences sync so it catches the login StateChanged event and
        // applies server-side preferences to the shell as soon as the workspace connects.
        provider.GetRequiredService<PreferencesSync>();

        return provider;
    }
}
