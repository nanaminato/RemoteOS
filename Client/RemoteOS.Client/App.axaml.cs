using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Client.Services;
using Client.Services.Auth;
using Client.ViewModels.Login;
using Client.ViewModels.Shell;
using Client.Views;
using Client.Views.Login;
using Microsoft.Extensions.DependencyInjection;

namespace Client;

public partial class App : Application
{
    /// <summary>Root DI container for the RemoteOS client shell.</summary>
    public static IServiceProvider Services { get; private set; } = default!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
#if DEBUG
        this.AttachDeveloperTools();
#endif
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Services = Bootstrapper.Build(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 启动分叉（mstsc 风格）：先弹独立登录窗，登录成功后再进入桌面。
            // OnExplicitShutdown 防止登录窗→桌面切换时进程提前退出。
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var session = Services.GetRequiredService<IAuthSession>();
            var loginWindow = new LoginWindow
            {
                DataContext = Services.GetRequiredService<LoginViewModel>(),
            };
            desktop.MainWindow = loginWindow;
            loginWindow.Show();

            session.StateChanged += (_, e) =>
            {
                if (e.State != AuthSessionState.Authenticated) return;
                // 切换到桌面必须在 UI 线程执行。
                Dispatcher.UIThread.Post(() =>
                {
                    var shell = Services.GetRequiredService<DesktopShellViewModel>();
                    var mainWindow = new MainWindow { DataContext = shell };
                    desktop.MainWindow = mainWindow;
                    mainWindow.Show();
                    loginWindow.Close();
                    mainWindow.Closed += (_, _) => desktop.Shutdown();
                });
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
