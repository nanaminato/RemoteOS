using Avalonia.Controls;
using RemoteOS.Core.Applications;
using RemoteOS.Core.Primitives;
using RemoteOS.WindowManager;

namespace RemoteOS.AppSDK;

/// <summary>
/// The runtime context handed to an application when it is launched. This is the surface
/// through which a RemoteOS application creates windows and reaches system services.
/// </summary>
public sealed class AppContext
{
    public AppContext(AppId appId, IWindowManager windowManager, IServiceProvider services)
    {
        AppId = appId;
        WindowManager = windowManager;
        Services = services;
    }

    /// <summary>The id of the application this context belongs to.</summary>
    public AppId AppId { get; }

    /// <summary>The desktop window manager — used to create application windows.</summary>
    public IWindowManager WindowManager { get; }

    /// <summary>
    /// DI container for first-party applications compiled with the host. This is not exposed to
    /// package applications; they receive <see cref="IExternalAppContext"/> instead.
    /// </summary>
    public IServiceProvider Services { get; }

    /// <summary>Convenience: create and show a window owned by this application.</summary>
    public ManagedWindow ShowWindow(
        string title,
        Control content,
        Rect? bounds = null,
        string? iconGlyph = null,
        bool canResize = true,
        bool canMinimize = true,
        bool canMaximize = true)
    {
        return WindowManager.Create(new WindowCreateOptions(
            OwnerAppId: AppId,
            Title: title,
            Content: content,
            Bounds: bounds,
            IconGlyph: iconGlyph,
            CanResize: canResize,
            CanMinimize: canMinimize,
            CanMaximize: canMaximize));
    }

    /// <summary>Shows a dialog that blocks its owner until it is confirmed or cancelled.</summary>
    public Task<TResult?> ShowDialogAsync<TResult>(
        ManagedWindow owner,
        string title,
        Func<ModalDialog<TResult>, Control> contentFactory,
        Rect? bounds = null)
        => WindowManager.ShowDialogAsync(owner, title, contentFactory, bounds);

    /// <summary>Shows a dialog centered over its owner at the requested size.</summary>
    public Task<TResult?> ShowDialogAsync<TResult>(
        ManagedWindow owner,
        string title,
        Func<ModalDialog<TResult>, Control> contentFactory,
        Size preferredSize)
        => WindowManager.ShowDialogAsync(owner, title, contentFactory, preferredSize);
}
