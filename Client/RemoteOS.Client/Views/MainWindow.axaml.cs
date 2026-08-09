using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Client.Services.Auth;
using Client.Services.WindowLayout;
using Client.Services;
using Client.Services.Developer;
using Client.Services.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Client.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _hideBarTimer;
    private bool _isPinned;
    private bool _isFullScreen;
    private WindowState _windowStateBeforeFullScreen = WindowState.Maximized;
    private readonly LocalizationService _localization;

    public MainWindow()
    {
        InitializeComponent();
        _localization = App.Services.GetRequiredService<LocalizationService>();
        _localization.LanguageChanged += (_, _) => RefreshLocalizedText();
        RefreshLocalizedText();
        _hideBarTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _hideBarTimer.Tick += (_, _) => HideConnectionBar();
    }

    private void ConnectionInfo_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ConnectionInfo.IsVisible = !ConnectionInfo.IsVisible;

    private void Pin_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        PinButton.Content = T(_isPinned ? "shell.connection_bar.pinned" : "shell.connection_bar.pin", _isPinned ? "Pinned" : "Pin");
        ToolTip.SetTip(PinButton, T(_isPinned ? "shell.connection_bar.unpin_tooltip" : "shell.connection_bar.pin_tooltip", _isPinned ? "Unpin connection bar" : "Pin connection bar"));
        if (_isPinned)
        {
            _hideBarTimer.Stop();
            ConnectionBar.IsVisible = true;
        }
    }

    private void FullScreen_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!_isFullScreen)
            _windowStateBeforeFullScreen = WindowState;

        _isFullScreen = !_isFullScreen;
        WindowState = _isFullScreen ? WindowState.FullScreen : _windowStateBeforeFullScreen;
        FullScreenButton.Content = T(_isFullScreen ? "shell.full_screen.exit" : "shell.full_screen.enter", _isFullScreen ? "Exit full screen" : "Full screen");
        ToolTip.SetTip(FullScreenButton, T(_isFullScreen ? "shell.full_screen.exit" : "shell.full_screen.enter_tooltip", _isFullScreen ? "Exit full screen" : "Enter full screen"));
        ConnectionInfo.IsVisible = false;
        WindowTitleBar.IsVisible = !_isFullScreen;

        if (_isFullScreen && !_isPinned)
            ScheduleConnectionBarHide();
        else
            ConnectionBar.IsVisible = true;
    }

    private async void Disconnect_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await DisconnectAsync();
    }

    private async void CloseWindow_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        await DisconnectAsync();
    }

    private async Task DisconnectAsync()
    {
        ConnectionInfo.IsVisible = false;
        try
        {
            await App.Services.GetRequiredService<WindowLayoutStore>().FlushAsync();
            await App.Services.GetRequiredService<IAuthSession>().LogoutAsync();
        }
        finally
        {
            Close();
        }
    }

    private void Minimize_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void Maximize_OnClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var isMaximized = WindowState == WindowState.Maximized;
        WindowState = isMaximized ? WindowState.Normal : WindowState.Maximized;
        MaximizeButton.Content = isMaximized ? "\u25A1" : "\u2750";
        ToolTip.SetTip(MaximizeButton, T(isMaximized ? "common.maximize" : "common.restore", isMaximized ? "Maximize" : "Restore"));
    }

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && WindowState == WindowState.Normal)
            BeginMoveDrag(e);
    }

    private void Resize_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (WindowState != WindowState.Normal || sender is not Control { Tag: string edgeName })
            return;

        if (Enum.TryParse<WindowEdge>(edgeName, out var edge))
            BeginResizeDrag(edge, e);
    }

    private void Root_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_isFullScreen && !_isPinned && e.GetPosition(this).Y <= 6)
        {
            _hideBarTimer.Stop();
            ConnectionBar.IsVisible = true;
        }
    }

    private void Root_OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.I || e.KeyModifiers != (KeyModifiers.Control | KeyModifiers.Shift))
            return;

        e.Handled = true;
        App.Services.GetRequiredService<NetworkInspectorWindowService>().Open();
    }

    private void ConnectionBar_OnPointerEntered(object? sender, PointerEventArgs e) => _hideBarTimer.Stop();

    private void ConnectionBar_OnPointerExited(object? sender, PointerEventArgs e) => ScheduleConnectionBarHide();

    private void ScheduleConnectionBarHide()
    {
        if (_isFullScreen && !_isPinned)
        {
            _hideBarTimer.Stop();
            _hideBarTimer.Start();
        }
    }

    private void HideConnectionBar()
    {
        _hideBarTimer.Stop();
        if (_isFullScreen && !_isPinned)
        {
            ConnectionBar.IsVisible = false;
            ConnectionInfo.IsVisible = false;
        }
    }

    private void RefreshLocalizedText()
    {
        PinButton.Content = T(_isPinned ? "shell.connection_bar.pinned" : "shell.connection_bar.pin", _isPinned ? "Pinned" : "Pin");
        ToolTip.SetTip(PinButton, T(_isPinned ? "shell.connection_bar.unpin_tooltip" : "shell.connection_bar.pin_tooltip", _isPinned ? "Unpin connection bar" : "Pin connection bar"));
        FullScreenButton.Content = T(_isFullScreen ? "shell.full_screen.exit" : "shell.full_screen.enter", _isFullScreen ? "Exit full screen" : "Full screen");
        ToolTip.SetTip(FullScreenButton, T(_isFullScreen ? "shell.full_screen.exit" : "shell.full_screen.enter_tooltip", _isFullScreen ? "Exit full screen" : "Enter full screen"));
    }

    private string T(string key, string englishFallback) => _localization.Get(key, englishFallback);
}
