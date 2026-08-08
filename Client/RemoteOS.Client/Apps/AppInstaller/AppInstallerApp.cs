using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Client.Apps.AppInstaller.ViewModels;
using Client.Apps.AppInstaller.Views;
using Client.Apps.Explorer;
using Client.Apps.Explorer.ViewModels;
using Client.Apps.Explorer.Views;
using Client.Services.AppPackages;
using RemoteOS.AppSDK;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using RemoteOS.WindowManager;
using AppContext = RemoteOS.AppSDK.AppContext;
using Rect = RemoteOS.Core.Primitives.Rect;

namespace Client.Apps.AppInstaller;

/// <summary>Built-in, consent-first installer for signed-in users' .roapp packages.</summary>
public sealed class AppInstallerApp : RemoteApplicationBase, IFileOpenApplication
{
    public override ApplicationManifest Manifest { get; } = new(
        new AppId("remoteos.appinstaller"), "App Installer", "1.0.0", "📦",
        "Install or update RemoteOS application packages.", SupportedFileExtensions: [".roapp"]);

    public override void Activate(AppContext context) => OpenInstaller(context, []);

    /// <summary>Explorer invokes this with a server path, so the package is staged locally before review.</summary>
    public void OpenFile(AppContext context, string path) => OpenInstaller(context, [path]);

    private static void OpenInstaller(AppContext context, IReadOnlyList<string> serverPaths)
    {
        var installer = context.Services.GetService(typeof(AppPackageInstallerService)) as AppPackageInstallerService;
        var files = context.Services.GetService(typeof(IExplorerClient)) as IExplorerClient;
        if (installer is null)
            return;

        var viewModel = new AppInstallerViewModel(installer);
        var view = new AppInstallerView { DataContext = viewModel };
        var window = context.ShowWindow("App Installer", view,
            bounds: new Rect(230, 110, 620, 580), iconGlyph: "📦", canResize: true);

        viewModel.RequestLocalPackagesAsync = async () =>
        {
            var topLevel = GetTopLevel();
            if (topLevel is null) return [];
            var selected = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择 RemoteOS 应用包",
                AllowMultiple = true,
                FileTypeFilter = [new FilePickerFileType("RemoteOS 应用包") { Patterns = ["*.roapp"] }],
            });
            return selected.Select(file => file.TryGetLocalPath()).OfType<string>().ToArray();
        };

        viewModel.RequestServerPackagesAsync = async () =>
        {
            if (files is null) return [];
            var result = await context.ShowDialogAsync<IReadOnlyList<string>>(window, "选择服务器应用包", dialog =>
            {
                var picker = new ExplorerViewModel(files,
                    new ExplorerPickerOptions(ExplorerPickerMode.OpenFile, AllowMultiple: true,
                        Filters: [new ExplorerFileFilter("RemoteOS 应用包 (*.roapp)", ["*.roapp"])]),
                    paths => dialog.Close(paths))
                {
                    CancelAction = dialog.Cancel,
                };
                _ = picker.LoadRootAsync();
                return new ExplorerMainView { DataContext = picker };
            }, GetPickerBounds(window));
            return result ?? [];
        };
        viewModel.ShowMessageAsync = (title, message) => context.ShowDialogAsync<bool>(window, title,
            dialog => CreateMessageView(dialog, message));

        EventHandler<ManagedWindow>? closed = null;
        closed = (_, closedWindow) =>
        {
            if (!ReferenceEquals(closedWindow, window)) return;
            context.WindowManager.WindowClosed -= closed;
            viewModel.Dispose();
        };
        context.WindowManager.WindowClosed += closed;

        if (serverPaths.Count > 0)
            _ = viewModel.QueueServerPackagesAsync(serverPaths);
    }

    private static TopLevel? GetTopLevel() => Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
        ? desktop.MainWindow
        : null;

    private static Rect GetPickerBounds(RemoteOS.WindowManager.ManagedWindow owner)
    {
        var bounds = owner.Info.Bounds;
        return new Rect(bounds.X + 24, bounds.Y + 28, Math.Min(760, Math.Max(480, bounds.Width - 48)),
            Math.Min(520, Math.Max(320, bounds.Height - 56)));
    }

    private static Control CreateMessageView(ModalDialog<bool> dialog, string message)
    {
        var confirm = new Button { Content = "知道了", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Padding = new Thickness(16, 6) };
        confirm.Click += (_, _) => dialog.Close(true);
        return new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                confirm,
            },
        };
    }
}
