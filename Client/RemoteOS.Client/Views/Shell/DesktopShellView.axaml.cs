using Avalonia.Controls;
using Avalonia.Interactivity;
using Client.ViewModels.Shell;
using RemoteOS.Core.Primitives;

namespace Client.Views.Shell;

public partial class DesktopShellView : UserControl
{
    private Canvas? _host;

    public DesktopShellView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        _host = this.FindControl<Canvas>("PART_WindowHost");
        if (_host == null || DataContext is not DesktopShellViewModel vm)
            return;

        vm.WindowManager.Attach(_host);
        _host.SizeChanged += (_, _) => UpdateHostBounds();
        this.LayoutUpdated += OnFirstLayout;
    }

    private void OnFirstLayout(object? sender, EventArgs e)
    {
        UpdateHostBounds();
        this.LayoutUpdated -= OnFirstLayout;
    }

    private void UpdateHostBounds()
    {
        if (_host == null || DataContext is not DesktopShellViewModel vm)
            return;

        var b = _host.Bounds;
        vm.WindowManager.SetHostBounds(new Rect(0, 0, b.Width, b.Height));
    }

    private void StartBackdrop_OnPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (DataContext is DesktopShellViewModel vm)
            vm.CloseStartCommand.Execute(null);
    }
}
