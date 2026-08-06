using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Avalonia.Services;
using RoyalTerminal.Terminal;
using RoyalTerminal.Terminal.Services;
using RoyalTerminal.Terminal.Transport.Ssh;
using RoyalTerminal.Terminal.Transport.Ssh.SshNet;

namespace Client.Apps;

/// <summary>Hosts one terminal session and applies the active workspace's appearance settings.</summary>
public partial class TerminalView : UserControl
{
    private readonly SignalRTransportFactory _transportFactory;
    private readonly TerminalControl _terminal;

    public TerminalView()
    {
        InitializeComponent();
        _transportFactory = new SignalRTransportFactory();
        _terminal = CreateTerminalControl(_transportFactory);
        TerminalHost.Children.Add(_terminal);
        _terminal.PointerPressed += OnTerminalPressed;
    }

    private static TerminalControl CreateTerminalControl(ITerminalTransportFactory transportFactory)
    {
        var control = new TerminalControl(
            new TerminalSessionService(),
            new DefaultTerminalInputAdapter(),
            new DefaultTerminalSelectionService(),
            new DefaultTerminalScrollService(),
            new DefaultVtProcessorFactory(),
            new DefaultPtyFactory(),
            new NullSshCredentialProvider(),
            new KnownHostsSshHostKeyValidator(),
            transportFactory);

        control.Focusable = true;
        control.Columns = 120;
        control.Rows = 32;
        control.ScrollbackLimit = 10000;
        control.TerminalFontSize = 14;
        return control;
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        if (DataContext is not TerminalViewModel vm)
            return;

        vm.PropertyChanged += OnViewModelPropertyChanged;
        try { await vm.AttachAsync(_terminal, _transportFactory); }
        catch { /* keep the window usable if the first connection fails */ }
        ApplyAppearance(vm.Appearance);
        FocusTerminal();
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (DataContext is TerminalViewModel vm)
        {
            vm.PropertyChanged -= OnViewModelPropertyChanged;
            vm.Detach();
        }
        base.OnUnloaded(e);
    }

    private void OnTerminalPressed(object? sender, PointerPressedEventArgs e) =>
        Dispatcher.UIThread.Post(FocusTerminal);

    private void FocusTerminal() => _terminal.Focus();

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is TerminalViewModel { Appearance: var appearance }
            && e.PropertyName == nameof(TerminalViewModel.Appearance))
            ApplyAppearance(appearance);
    }

    private void ApplyAppearance(RemoteOS.Protocol.Workspace.TerminalSettingsDto appearance)
    {
        _terminal.FontFamilyName = appearance.FontFamily;
        _terminal.TerminalFontSize = appearance.FontSize;
        TerminalHost.Background = new SolidColorBrush(Color.Parse(appearance.BackgroundColor));

        // Renderer palette APIs differ between RoyalTerminal renderers; apply values when present.
        ApplyColor(_terminal, "TextColor", appearance.ForegroundColor);
        ApplyColor(_terminal, "CursorColor", appearance.CursorColor);
        ApplyColor(_terminal.Theme, "BackgroundColor", appearance.BackgroundColor);
        ApplyColor(_terminal.Theme, "ForegroundColor", appearance.ForegroundColor);
        ApplyColor(_terminal.Theme, "CursorColor", appearance.CursorColor);
    }

    private static void ApplyColor(object? target, string propertyName, string value)
    {
        if (target is null || target.GetType().GetProperty(propertyName) is not { CanWrite: true } property)
            return;

        try
        {
            if (property.PropertyType == typeof(Color))
            {
                property.SetValue(target, Color.Parse(value));
                return;
            }

            var parse = property.PropertyType.GetMethod("Parse", [typeof(string)]);
            if (parse is not null)
                property.SetValue(target, parse.Invoke(null, [value]));
        }
        catch { /* unsupported renderer palette value */ }
    }
}
