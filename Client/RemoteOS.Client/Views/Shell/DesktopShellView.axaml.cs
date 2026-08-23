using Avalonia.Controls;
using Avalonia.Interactivity;
using Client.Apps.Explorer.Dialogs;
using Client.Localization;
using Client.ViewModels.Shell;
using Microsoft.Extensions.DependencyInjection;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using RemoteOS.Runtime;
using RemoteOS.WindowManager;

namespace Client.Views.Shell;

public partial class DesktopShellView : UserControl
{
    private Canvas? _host;
    private Canvas? _fullScreenHost;
    private DesktopFileEntryViewModel? _contextFile;
    private readonly CancellationTokenSource _desktopLifetime = new();

    public DesktopShellView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += (_, _) => _desktopLifetime.Cancel();
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _host = this.FindControl<Canvas>("PART_WindowHost");
        _fullScreenHost = this.FindControl<Canvas>("PART_FullScreenWindowHost");
        if (_host == null || _fullScreenHost == null || DataContext is not DesktopShellViewModel vm)
            return;

        vm.WindowManager.Attach(_host);
        vm.WindowManager.AttachFullScreenHost(_fullScreenHost);
        ConfigureDesktopFileActions(vm);
        _host.SizeChanged += (_, _) => UpdateHostBounds();
        _fullScreenHost.SizeChanged += (_, _) => UpdateFullScreenHostBounds();
        this.LayoutUpdated += OnFirstLayout;
        // 桌面与对话框基础设施就绪后，检查是否需要首次配置引导
        _ = vm.TryTriggerFirstTimeSetupAsync();
    }

    private void OnFirstLayout(object? sender, EventArgs e)
    {
        UpdateHostBounds();
        UpdateFullScreenHostBounds();
        this.LayoutUpdated -= OnFirstLayout;
        if (DataContext is DesktopShellViewModel vm)
            _ = vm.RestoreDesktopStateAsync(_desktopLifetime.Token);
    }

    private void UpdateHostBounds()
    {
        if (_host == null || DataContext is not DesktopShellViewModel vm)
            return;

        var b = _host.Bounds;
        vm.WindowManager.SetHostBounds(new Rect(0, 0, b.Width, b.Height));
    }

    private void UpdateFullScreenHostBounds()
    {
        if (_fullScreenHost == null || DataContext is not DesktopShellViewModel vm)
            return;

        var b = _fullScreenHost.Bounds;
        vm.WindowManager.SetFullScreenHostBounds(new Rect(0, 0, b.Width, b.Height));
    }

    private void StartBackdrop_OnPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (DataContext is DesktopShellViewModel vm)
            vm.CloseStartCommand.Execute(null);
    }

    private void DesktopBackground_OnPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (DataContext is DesktopShellViewModel shell)
            shell.ClearDesktopSelectionCommand.Execute(null);
    }

    private void DesktopAppIcon_OnDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AppEntryViewModel app })
            app.LaunchCommand.Execute(null);
    }

    private void DesktopIcon_OnPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not Button { DataContext: { } item } || !e.GetCurrentPoint(this).Properties.IsRightButtonPressed)
            return;

        // Match Windows: the context-menu target is selected before the menu is displayed.
        SelectDesktopItem(item);
        if (item is DesktopFileEntryViewModel file)
        {
            _contextFile = file;
            LogDesktopFileMenu($"right-click target captured: entry={file.DisplayName}, type={file.Entry.Type}.");
        }
    }

    private void DesktopFileIcon_OnDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DesktopFileEntryViewModel file }
            || DataContext is not DesktopShellViewModel shell)
            return;

        shell.OpenDesktopEntryCommand.Execute(file);
    }

    private void DesktopAppContextMenu_OnOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu { PlacementTarget.DataContext: AppEntryViewModel app } menu) return;
        SelectDesktopItem(app);
        SetMenuEntry(menu, app);
    }

    private void DesktopFileContextMenu_OnOpened(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu { PlacementTarget.DataContext: DesktopFileEntryViewModel file } menu)
        {
            var placementType = (sender as ContextMenu)?.PlacementTarget?.DataContext?.GetType().Name ?? "<none>";
            LogDesktopFileMenu($"context menu opened without a desktop-file placement target: dataContextType={placementType}; using last captured target={_contextFile?.DisplayName ?? "<none>"}.");
            return;
        }
        _contextFile = file;
        LogDesktopFileMenu($"context menu opened: entry={file.DisplayName}, type={file.Entry.Type}, menuItems={menu.Items.Count}");
        SelectDesktopItem(file);
        SetMenuEntry(menu, file);
    }

    private void DesktopAppOpenMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: AppEntryViewModel app } && DataContext is DesktopShellViewModel shell)
            shell.OpenDesktopAppCommand.Execute(app);
    }

    private void DesktopAppDetailsMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: AppEntryViewModel app } && DataContext is DesktopShellViewModel shell)
            shell.ShowDesktopAppDetailsCommand.Execute(app);
    }

    private void DesktopFileOpenMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (GetContextFile(sender) is { } file && DataContext is DesktopShellViewModel shell)
        {
            shell.RecordDesktopFileMenuDiagnostic($"open click dispatched: entry={file.DisplayName}, tagPresent={(sender as MenuItem)?.Tag is DesktopFileEntryViewModel}.");
            shell.OpenDesktopEntryCommand.Execute(file);
        }
        else
            LogDesktopFileMenu("open click ignored: no resolved desktop file or shell data context.");
    }

    private void DesktopFileOpenWithMenuItem_OnClick(object? sender, RoutedEventArgs e) =>
        ExecuteDesktopFileCommand(sender, "open-with", shell => shell.OpenDesktopEntryWithCommand);

    private void DesktopFileCopyMenuItem_OnClick(object? sender, RoutedEventArgs e) =>
        ExecuteDesktopFileCommand(sender, "copy", shell => shell.CopyDesktopEntryCommand);

    private void DesktopFileCutMenuItem_OnClick(object? sender, RoutedEventArgs e) =>
        ExecuteDesktopFileCommand(sender, "cut", shell => shell.CutDesktopEntryCommand);

    private void DesktopFileDeleteMenuItem_OnClick(object? sender, RoutedEventArgs e) =>
        ExecuteDesktopFileCommand(sender, "delete", shell => shell.DeleteDesktopEntryCommand);

    private void DesktopFileOpenInExplorerMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (GetContextFile(sender) is { } file && DataContext is DesktopShellViewModel shell)
        {
            shell.RecordDesktopFileMenuDiagnostic($"show-in-explorer click dispatched: entry={file.DisplayName}.");
            shell.ShowDesktopEntryInExplorerCommand.Execute(file);
        }
        else
            LogDesktopFileMenu("show-in-explorer click ignored: no resolved desktop file or shell data context.");
    }

    private void DesktopFilePropertiesMenuItem_OnClick(object? sender, RoutedEventArgs e) =>
        ExecuteDesktopFileCommand(sender, "properties", shell => shell.ShowDesktopEntryPropertiesCommand);

    private void DesktopPasteMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopShellViewModel shell)
        {
            shell.RecordDesktopFileMenuDiagnostic("desktop background paste click dispatched.");
            shell.PasteDesktopCommand.Execute(null);
        }
        else
            LogDesktopFileMenu("desktop background paste click ignored: no shell data context.");
    }

    private void DesktopRefreshMenuItem_OnClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DesktopShellViewModel shell)
            shell.RefreshDesktopCommand.Execute(null);
    }

    private void SelectDesktopItem(object item)
    {
        if (DataContext is DesktopShellViewModel shell)
            shell.SelectDesktopItemCommand.Execute(item);
    }

    private static void SetMenuEntry(ContextMenu menu, object entry)
    {
        foreach (var item in menu.Items.OfType<MenuItem>())
            item.Tag = entry;
    }

    private DesktopFileEntryViewModel? GetContextFile(object? sender) =>
        (sender as MenuItem)?.Tag as DesktopFileEntryViewModel ?? _contextFile;

    private void ExecuteDesktopFileCommand(object? sender, string action,
        Func<DesktopShellViewModel, System.Windows.Input.ICommand> command)
    {
        if (GetContextFile(sender) is { } file && DataContext is DesktopShellViewModel shell)
        {
            shell.RecordDesktopFileMenuDiagnostic($"{action} click dispatched: entry={file.DisplayName}, tagPresent={(sender as MenuItem)?.Tag is DesktopFileEntryViewModel}.");
            command(shell).Execute(file);
        }
        else
            LogDesktopFileMenu($"{action} click ignored: no resolved desktop file or shell data context.");
    }

    private void LogDesktopFileMenu(string message)
    {
        if (DataContext is DesktopShellViewModel shell)
            shell.RecordDesktopFileMenuDiagnostic(message);
    }

    private void ConfigureDesktopFileActions(DesktopShellViewModel shell)
    {
        shell.RequestDesktopConfirmAsync = (title, message, confirmLabel) =>
            ShowDesktopDialogAsync<bool>(shell, title, new Size(460, 220), complete => new ConfirmDialogView
            {
                DataContext = new ConfirmDialogViewModel(message, complete, confirmLabel),
            }).ContinueWith(task => task.Result == true);
        shell.RequestDesktopOpenWithAsync = (applications, extension) =>
            ShowDesktopDialogAsync<OpenWithChoice>(shell, LocalizedText.Get("explorer.open_with"), new Size(500, 360), complete => new OpenWithDialogView
            {
                DataContext = new OpenWithDialogViewModel(applications, extension, complete),
            });
        shell.ShowDesktopPropertiesAsync = properties =>
            ShowDesktopDialogAsync<bool>(shell, LocalizedText.Get("explorer.properties"), new Size(720, 620), complete => new FilePropertiesDialogView
            {
                DataContext = new FilePropertiesDialogViewModel(
                    properties,
                    unixMode => shell.SetDesktopUnixPermissionsAsync(properties.Path, unixMode),
                    () => complete(true)),
            });

        // ── 桌面显示配置对话框 ──
        var applications = App.Services.GetRequiredService<ApplicationManager>();
        shell.RequestOpenDesktopDisplaySettingsAsync = () =>
            ShowDesktopDialogAsync<bool>(shell, LocalizedText.Get("shell.desktop_display.title"),
                new Size(560, 520),
                complete => new DesktopDisplayDialogs(
                    shell.Settings,
                    applications,
                    () => shell.SavePreferencesFireAndForgetAsync(),
                    result => complete(result),
                    isFirstTime: false))
            .ContinueWith(task => task.Result == true);

        shell.RequestFirstTimeDesktopSetupAsync = async () =>
        {
            // 首次配置：先把「跳过也算完成」写入 HasCompletedFirstTimeSetup
            var confirmed = await ShowDesktopDialogAsync<bool>(shell, LocalizedText.Get("shell.desktop_display.welcome_title"),
                new Size(580, 560),
                complete => new DesktopDisplayDialogs(
                    shell.Settings,
                    applications,
                    async () =>
                    {
                        shell.Settings.HasCompletedFirstTimeSetup = true;
                        await shell.SavePreferencesFireAndForgetAsync();
                    },
                    result => complete(result),
                    isFirstTime: true));
            // 无论是确认还是跳过，都标记首次配置已完成
            if (confirmed == false)
            {
                shell.Settings.HasCompletedFirstTimeSetup = true;
                await shell.SavePreferencesFireAndForgetAsync();
            }
            return confirmed;
        };
    }

    private static Task<TResult?> ShowDesktopDialogAsync<TResult>(DesktopShellViewModel shell, string title, Size preferredSize,
        Func<Action<TResult?>, Control> contentFactory)
        => shell.WindowManager.ShowShellDialogAsync<TResult>(
            title,
            dialog => contentFactory(result => dialog.Close(result!)),
            preferredSize);

    private void TaskbarPreviewBackdrop_OnPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (DataContext is DesktopShellViewModel vm)
            vm.CloseTaskbarPreviewCommand.Execute(null);
    }
}
