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
using RemoteOS.Protocol.Workspace;

namespace Client.Apps.Terminal;

/// <summary>Hosts one terminal session and applies the active workspace's appearance settings.</summary>
public partial class TerminalView : UserControl
{
    private const int InitialColumns = 80;
    private const int InitialRows = 24;
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
        // 120 columns cannot fit this app's 820-DIP initial window at a readable 14-DIP
        // font size. RoyalTerminal scales its actual font down to honour fixed dimensions.
        control.Columns = InitialColumns;
        control.Rows = InitialRows;
        control.ScrollbackLimit = 10000;
        control.TerminalFontSize = 14;
        control.FontFamilyName = ResolveFontFamily(TerminalSettingsDto.Default.FontFamily);
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
        _terminal.FontFamilyName = ResolveFontFamily(appearance.FontFamily);
        _terminal.TerminalFontSize = Math.Clamp(appearance.FontSize, 12, 32);
        TerminalHost.Background = new SolidColorBrush(Color.Parse(appearance.BackgroundColor));

        // Renderer palette APIs differ between RoyalTerminal renderers; apply values when present.
        ApplyColor(_terminal, "TextColor", appearance.ForegroundColor);
        ApplyColor(_terminal, "CursorColor", appearance.CursorColor);
        ApplyColor(_terminal.Theme, "BackgroundColor", appearance.BackgroundColor);
        ApplyColor(_terminal.Theme, "ForegroundColor", appearance.ForegroundColor);
        ApplyColor(_terminal.Theme, "CursorColor", appearance.CursorColor);
    }

    private static string ResolveFontFamily(string requested)
    {
        var installed = FontManager.Current.SystemFonts
            .Select(font => font.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(requested) && installed.Contains(requested))
            return requested;

        // Cascadia/Consolas are normally absent on Linux. Always choose a real installed
        // monospace face; passing a missing family to the Skia renderer produces poor metrics.
        foreach (var fallback in new[] { "DejaVu Sans Mono", "Noto Mono", "Liberation Mono", "JetBrains Mono", "Cascadia Mono", "Consolas" })
            if (installed.Contains(fallback))
                return fallback;

        return FontManager.Current.DefaultFontFamily.Name;
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
