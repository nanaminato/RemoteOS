using Avalonia.Controls;
using RemoteOS.Core.Primitives;

namespace RemoteOS.WindowManager;

/// <summary>
/// The desktop window manager. Owns the lifecycle, z-ordering and interactive state of all
/// managed windows that live inside the shell's window host.
/// </summary>
public interface IWindowManager
{
    /// <summary>Read-only view of every window (including minimized ones), bottom-to-top.</summary>
    IReadOnlyList<ManagedWindow> Windows { get; }

    /// <summary>The currently focused window, or null.</summary>
    ManagedWindow? ActiveWindow { get; }

    /// <summary>Desktop area available to windows (excludes taskbar etc.).</summary>
    Rect HostBounds { get; }

    /// <summary>Binds the manager to the canvas that physically hosts window visuals.</summary>
    void Attach(Canvas host);

    /// <summary>Updates the available desktop area (e.g. on shell resize).</summary>
    void SetHostBounds(Rect bounds);

    /// <summary>Creates, lays out and shows a new window for the given application content.</summary>
    ManagedWindow Create(WindowCreateOptions options);

    /// <summary>Shows a modal dialog over an application window and asynchronously returns its result.</summary>
    Task<TResult?> ShowDialogAsync<TResult>(
        ManagedWindow owner,
        string title,
        Func<ModalDialog<TResult>, Control> contentFactory);

    void Close(ManagedWindow window);
    void Focus(ManagedWindow window);
    void Minimize(ManagedWindow window);
    void Restore(ManagedWindow window);
    void ToggleMaximize(ManagedWindow window);

    event EventHandler<ManagedWindow>? WindowOpened;
    event EventHandler<ManagedWindow>? WindowClosed;
    event EventHandler<ManagedWindow?>? ActiveWindowChanged;
}
