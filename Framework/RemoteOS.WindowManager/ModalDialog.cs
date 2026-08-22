using Avalonia.Controls;
using Avalonia.Media;
using RemoteOS.Core.Primitives;
using Rect = RemoteOS.Core.Primitives.Rect;

namespace RemoteOS.WindowManager;

/// <summary>
/// Handle for a modal desktop window. A dialog is a real managed window: it can be moved and
/// resized like any other program window, while only its direct owner is blocked.
/// </summary>
public sealed class ModalDialog<TResult>
{
    private readonly WindowManager _manager;
    private readonly TaskCompletionSource<TResult?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ManagedWindow? _window;

    internal ModalDialog(WindowManager manager, ManagedWindow owner)
    {
        _manager = manager;
        Owner = owner;
    }

    internal ManagedWindow Owner { get; }
    public ManagedWindow? Window => _window;
    public Task<TResult?> Result => _completion.Task;

    public void Close(TResult result) => _completion.TrySetResult(result);
    public void Cancel() => _completion.TrySetResult(default);

    /// <summary>Opens a child modal window whose owner is this dialog window.</summary>
    public Task<TChild?> ShowDialogAsync<TChild>(
        string title,
        Func<ModalDialog<TChild>, Control> contentFactory)
    {
        if (_window is null)
            throw new InvalidOperationException("The dialog has not been shown yet.");
        return _manager.ShowDialogAsync(_window, title, contentFactory);
    }

    internal void Attach(ManagedWindow window) => _window = window;
}

internal interface IModalSession
{
    ManagedWindow Owner { get; }
    ManagedWindow DialogWindow { get; }
    ModalBlocker Blocker { get; }
    Canvas Host { get; }
    void Cancel();
}

internal sealed class ModalSession<TResult>(
    ManagedWindow owner,
    ManagedWindow dialogWindow,
    ModalBlocker blocker,
    Canvas host,
    ModalDialog<TResult> dialog) : IModalSession
{
    public ManagedWindow Owner { get; } = owner;
    public ManagedWindow DialogWindow { get; } = dialogWindow;
    public ModalBlocker Blocker { get; } = blocker;
    public Canvas Host { get; } = host;
    public void Cancel() => dialog.Cancel();
}

/// <summary>A transparent input shield that follows only the blocked owner window.</summary>
internal sealed class ModalBlocker : Border
{
    public ModalBlocker(ManagedWindow owner)
    {
        Owner = owner;
        Background = new SolidColorBrush(Color.Parse("#3D000000"));
    }

    public ManagedWindow Owner { get; }

    public void ApplyBounds(Rect bounds)
    {
        Canvas.SetLeft(this, bounds.X);
        Canvas.SetTop(this, bounds.Y);
        Width = bounds.Width;
        Height = bounds.Height;
    }
}
