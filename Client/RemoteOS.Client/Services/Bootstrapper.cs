using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Client.Apps;
using Client.Apps.CodeEditor;
using Client.Apps.Notepad;
using Client.Apps.Settings;
using Client.Apps.Terminal;
using Client.Apps.Welcome;
using Client.Services.Auth;
using Client.Services.AppPermissions;
using Client.Services.WindowLayout;
using Client.ViewModels.Login;
using Client.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;
using RemoteOS.AppSDK;
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
        services.AddSingleton<ShellSettings>();
        services.AddSingleton<ApplicationManager>(sp =>
            new ApplicationManager(sp.GetRequiredService<IWindowManager>(), sp));

        // Auth（登录模块）：typed HttpClient + 仅内存认证会话 + 登录视图模型。
        services.AddHttpClient<IRemoteOsClient, RemoteOsClient>();
        services.AddHttpClient<ITerminalSettingsClient, TerminalSettingsClient>();
        services.AddSingleton<IRememberedSessionStore, RememberedSessionStore>();
        services.AddSingleton<IAuthSession, AuthSession>();
        services.AddSingleton<LoginViewModel>();

        // Explorer（文件管理器）：typed HttpClient（JWT from IAuthSession）+ 应用注册。
        services.AddHttpClient<Client.Apps.Explorer.IExplorerClient, Client.Apps.Explorer.ExplorerClient>();

        // Browser（浏览器）：typed HttpClient（JWT from IAuthSession）+ 应用注册。
        // NativeWebView 用平台原生引擎（Win=WebView2/macOS=WKWebView/Linux=WebKitGTK），网页内容走客户端网络；
        // Server 仅持久化书签与历史记录（按用户隔离）。
        services.AddHttpClient<Client.Apps.Browser.IBrowserClient, Client.Apps.Browser.BrowserClient>();

        // TaskManager（任务管理器）：typed HttpClient（JWT from IAuthSession，与 Browser/Explorer 同模式）。
        // 拉取服务端采集的宿主 OS 资源占用（CPU/内存/磁盘/网络/GPU）与进程列表；结束进程权限不足提示需在宿主 OS 提权。
        services.AddHttpClient<Client.Apps.TaskManager.ITaskManagerClient, Client.Apps.TaskManager.TaskManagerClient>();

        // Settings（设置中心）：typed HttpClient（JWT from IAuthSession，与 Browser/Explorer 同模式）。
        // 偏好持久化到服务端 Workspace（/workspaces/{id}/preferences），多设备共享。
        services.AddHttpClient<ISettingsClient, SettingsClient>();
        services.AddHttpClient<IWindowLayoutClient, WindowLayoutClient>();
        services.AddSingleton<WindowLayoutStore>();
        services.AddSingleton<DefaultAppRegistry>();
        services.AddSingleton<IAppPermissionManager, JsonAppPermissionManager>();
        services.AddSingleton<ExternalAppContextFactory>();
        // PreferencesSync 监听登录态，登录后把服务端偏好应用到 ShellSettings + DefaultAppRegistry。
        services.AddSingleton<PreferencesSync>();

        // Built-in applications.
        services.AddSingleton<IRemoteApplication, WelcomeApp>();
        services.AddSingleton<IRemoteApplication, NotepadApp>();
        services.AddSingleton<IRemoteApplication, CodeEditorApp>();
        services.AddSingleton<IRemoteApplication, SettingsApp>();
        services.AddSingleton<IRemoteApplication, TerminalApp>();
        services.AddSingleton<IRemoteApplication, Client.Apps.Explorer.ExplorerApp>();
        services.AddSingleton<IRemoteApplication, Client.Apps.Browser.BrowserApp>();
        services.AddSingleton<IRemoteApplication, Client.Apps.TaskManager.TaskManagerApp>();

        services.AddSingleton<DesktopShellViewModel>(sp =>
        {
            var wm = sp.GetRequiredService<WindowManager>();
            var apps = sp.GetRequiredService<ApplicationManager>();
            var settings = sp.GetRequiredService<ShellSettings>();
            var session = sp.GetRequiredService<IAuthSession>();
            Action shutdown = () =>
            {
                if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                    desktop.Shutdown();
            };
            return new DesktopShellViewModel(wm, apps, settings, session, shutdown);
        });

        var provider = services.BuildServiceProvider();

        windowManager.LayoutStore = provider.GetRequiredService<WindowLayoutStore>();

        // Register applications with the runtime.
        var manager = provider.GetRequiredService<ApplicationManager>();
        foreach (var application in provider.GetServices<IRemoteApplication>())
            manager.Register(application);

        // Build the desktop / start menu entries.
        provider.GetRequiredService<DesktopShellViewModel>().PopulateDesktop();

        // Eagerly start preferences sync so it catches the login StateChanged event and
        // applies server-side preferences to the shell as soon as the workspace connects.
        provider.GetRequiredService<PreferencesSync>();

        return provider;
    }
}
