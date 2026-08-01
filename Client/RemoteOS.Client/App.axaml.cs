using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Client.Services;
using Client.ViewModels.Shell;
using Client.Views;
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
            var shell = Services.GetRequiredService<DesktopShellViewModel>();
            desktop.MainWindow = new MainWindow
            {
                DataContext = shell,
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
