using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
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
            var loginViewModel = Services.GetRequiredService<LoginViewModel>();
            var loginWindow = new LoginWindow
            {
                DataContext = loginViewModel,
            };
            desktop.MainWindow = loginWindow;
            loginWindow.Show();

            session.StateChanged += (_, e) =>
            {
                if (e.State != AuthSessionState.Authenticated) return;
                // 切换到桌面必须在 UI 线程执行。
                Dispatcher.UIThread.Post(async () =>
                {
                    if (e.RememberedProfileSaveResult is { } saveResult
                        && saveResult != RememberedProfileSaveResult.Saved)
                        await ShowRememberedProfileSaveWarningAsync(loginWindow, saveResult);

                    var shell = Services.GetRequiredService<DesktopShellViewModel>();
                    var mainWindow = new MainWindow { DataContext = shell };
                    desktop.MainWindow = mainWindow;
                    mainWindow.Show();
                    loginWindow.Close();
                    mainWindow.Closed += (_, _) => desktop.Shutdown();
                });
            };

            // Keep the login window visible so the user can choose one of several remembered servers.
            // Selecting an entry with a saved password logs in without asking for it.
            _ = loginViewModel.LoadSavedProfilesAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task ShowRememberedProfileSaveWarningAsync(
        Window owner, RememberedProfileSaveResult saveResult)
    {
        var localization = Services.GetRequiredService<LoginLocalizationService>();
        var messageKey = saveResult == RememberedProfileSaveResult.SecureStorageUnavailable
            ? "login.password_save.secure_storage_unavailable"
            : "login.password_save.local_storage_failed";
        var fallback = saveResult == RememberedProfileSaveResult.SecureStorageUnavailable
            ? "You signed in, but the password could not be saved securely. The server and username were saved. " +
              "On Linux, start or unlock a Secret Service provider such as GNOME Keyring or KWallet, then sign in again."
            : "You signed in, but RemoteOS could not save this remembered connection. Check that the system credential store and local data directory are available, then sign in again.";

        var dialog = new Window
        {
            Title = localization.Get("login.password_save.title", "Password not saved"),
            Width = 500,
            MinHeight = 190,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var acknowledge = new Button
        {
            Content = localization.Get("common.ok", "OK"),
            MinWidth = 88,
            Padding = new Thickness(16, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        acknowledge.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 18,
            Children =
            {
                new TextBlock
                {
                    Text = localization.Get(messageKey, fallback),
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 13,
                },
                acknowledge,
            },
        };
        await dialog.ShowDialog(owner);
    }
}
