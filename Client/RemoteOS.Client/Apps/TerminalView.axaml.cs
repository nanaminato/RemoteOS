using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using RoyalTerminal.Avalonia.Controls;
using RoyalTerminal.Avalonia.Services;
using RoyalTerminal.Terminal;
using RoyalTerminal.Terminal.Services;
using RoyalTerminal.Terminal.Transport.Ssh;
using RoyalTerminal.Terminal.Transport.Ssh.SshNet;

namespace Client.Apps;

/// <summary>
/// Hosts the RoyalTerminal <c>TerminalControl</c>. The control is created in code-behind (not XAML) because
/// <c>TerminalControl.TerminalTransportFactory</c> is read-only and can only be injected via the 9-parameter
/// constructor — we pass our <see cref="SignalRTransportFactory"/> so SignalR transport (remote PTY) is selected
/// for <see cref="SignalRTransportOptions"/> while local PTY falls through to the inner composite factory.
/// </summary>
/// <remarks>
/// <see cref="RemoteOS.WindowManager.RemoteWindow"/> brings itself to front on every pointer press via
/// <c>WindowManager.Focus</c>→<c>window.View.Focus()</c>. Because <c>TerminalControl.Focusable</c> defaults to
/// <c>false</c> (set on the constructed control) and that focus call would otherwise land on the <c>RemoteWindow</c>,
/// we explicitly (re)focus the terminal: once on load, and deferred (after the bubbling press) on every click.
/// </remarks>
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

    /// <summary>
    /// Creates a <c>TerminalControl</c> with explicit service dependencies, mirroring the parameterless
    /// constructor's defaults but injecting our <see cref="SignalRTransportFactory"/> as the transport factory.
    /// </summary>
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

        if (DataContext is TerminalViewModel vm)
        {
            try { await vm.AttachAsync(_terminal, _transportFactory); }
            catch { /* keep the view alive even if the first session fails */ }
            FocusTerminal();
        }
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        if (DataContext is TerminalViewModel vm)
            vm.Detach();

        base.OnUnloaded(e);
    }

    private void OnTerminalPressed(object? sender, PointerPressedEventArgs e)
    {
        // Defer past the bubbling press so RemoteWindow's focus-bring-to-front does not
        // steal keyboard focus from the terminal control.
        Dispatcher.UIThread.Post(FocusTerminal);
    }

    private void FocusTerminal() => _terminal.Focus();
}
