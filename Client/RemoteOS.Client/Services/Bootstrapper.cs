using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Client.Apps;
using Client.Services.Auth;
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
        services.AddSingleton<IAuthSession, AuthSession>();
        services.AddSingleton<LoginViewModel>();

        // Built-in applications.
        services.AddSingleton<IRemoteApplication, WelcomeApp>();
        services.AddSingleton<IRemoteApplication, NotepadApp>();
        services.AddSingleton<IRemoteApplication, SettingsApp>();

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

        // Register applications with the runtime.
        var manager = provider.GetRequiredService<ApplicationManager>();
        foreach (var application in provider.GetServices<IRemoteApplication>())
            manager.Register(application);

        // Build the desktop / start menu entries.
        provider.GetRequiredService<DesktopShellViewModel>().PopulateDesktop();

        return provider;
    }
}
