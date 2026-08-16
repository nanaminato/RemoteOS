using Avalonia.Controls;
using Avalonia.Interactivity;
using Client.ViewModels.Shell;
using RemoteOS.Core.Primitives;

namespace Client.Views.Shell;

public partial class DesktopShellView : UserControl
{
    private Canvas? _host;
    private Canvas? _fullScreenHost;
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
        _host.SizeChanged += (_, _) => UpdateHostBounds();
        _fullScreenHost.SizeChanged += (_, _) => UpdateFullScreenHostBounds();
        this.LayoutUpdated += OnFirstLayout;
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

    private void DesktopAppIcon_OnDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: AppEntryViewModel app })
            app.LaunchCommand.Execute(null);
    }

    private void DesktopFileIcon_OnDoubleTapped(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: DesktopFileEntryViewModel file }
            || DataContext is not DesktopShellViewModel shell)
            return;

        shell.OpenDesktopEntryCommand.Execute(file);
    }

    private void TaskbarPreviewBackdrop_OnPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (DataContext is DesktopShellViewModel vm)
            vm.CloseTaskbarPreviewCommand.Execute(null);
    }
}
