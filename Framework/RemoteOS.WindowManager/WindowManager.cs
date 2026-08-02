using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Threading;
using RemoteOS.Core.Primitives;
using RemoteOS.Core.Windows;
using Rect = RemoteOS.Core.Primitives.Rect;
using Point = RemoteOS.Core.Primitives.Point;
using Size = RemoteOS.Core.Primitives.Size;
using WindowState = RemoteOS.Core.Windows.WindowState;

namespace RemoteOS.WindowManager;

/// <summary>
/// Authoritative desktop window manager. Windows are <see cref="RemoteWindow"/> controls parented
/// to a <see cref="Canvas"/> host; their position, size, z-order and state are owned here.
/// </summary>
public sealed class WindowManager : IWindowManager
{
    private readonly ObservableCollection<ManagedWindow> _windows = new();
    private readonly Dictionary<WindowId, WindowState> _preMinimizeState = new();
    private readonly List<IModalDialogView> _dialogs = new();

    private Canvas? _host;
    private Rect _hostBounds;
    private int _zCounter;
    private int _nextId;
    private ManagedWindow? _active;

    public IReadOnlyList<ManagedWindow> Windows => _windows;
    public ManagedWindow? ActiveWindow => _active;
    public Rect HostBounds => _hostBounds;

    public event EventHandler<ManagedWindow>? WindowOpened;
    public event EventHandler<ManagedWindow>? WindowClosed;
    public event EventHandler<ManagedWindow?>? ActiveWindowChanged;

    public void Attach(Canvas host) => _host = host;

    public void SetHostBounds(Rect bounds)
    {
        _hostBounds = bounds;
        // Keep maximized windows filling the new area.
        foreach (var w in _windows)
        {
            if (w.Info.State == WindowState.Maximized)
            {
                w.Info.Bounds = bounds;
                w.View.ApplyBounds(bounds);
            }
        }

        foreach (var dialog in _dialogs)
            dialog.ApplyBounds(dialog.Owner.Info.Bounds);
    }

    public ManagedWindow Create(WindowCreateOptions options)
    {
        if (_host == null)
            throw new InvalidOperationException("WindowManager is not attached to a host canvas.");

        var id = new WindowId(++_nextId);
        var bounds = ResolveInitialBounds(options.Bounds);

        var info = new WindowInfo
        {
            Id = id,
            OwnerAppId = options.OwnerAppId,
            Title = options.Title,
            IconGlyph = options.IconGlyph,
            Bounds = bounds,
            RestoreBounds = bounds,
            MinSize = new Size(240, 160),
            State = WindowState.Normal,
            CanResize = options.CanResize,
            CanMinimize = options.CanMinimize,
            CanMaximize = options.CanMaximize,
        };

        var view = new RemoteWindow { Content = options.Content };
        var managed = new ManagedWindow(info, view);
        view.DataContext = managed;

        managed.FocusRequested += (_, _) => Focus(managed);
        managed.CloseRequested += (_, _) => Close(managed);
        managed.MinimizeRequested += (_, _) => Minimize(managed);
        managed.MaximizeToggleRequested += (_, _) => ToggleMaximize(managed);
        managed.TaskbarToggleRequested += (_, _) => ToggleTaskbar(managed);

        view.DragRequested += (_, e) => OnDrag(managed, e);
        view.ResizeRequested += (_, e) => OnResize(managed, e);
        view.FocusRequested += (_, _) => Focus(managed);

        _host.Children.Add(view);
        view.ApplyBounds(info.Bounds);
        view.ApplyState(WindowState.Normal);

        _windows.Add(managed);
        WindowOpened?.Invoke(this, managed);

        Focus(managed);

        return managed;
    }

    public Task<TResult?> ShowDialogAsync<TResult>(
        ManagedWindow owner,
        string title,
        Func<ModalDialog<TResult>, Control> contentFactory)
    {
        if (_host == null)
            throw new InvalidOperationException("WindowManager is not attached to a host canvas.");
        if (!_windows.Contains(owner))
            throw new InvalidOperationException("The dialog owner is no longer open.");

        return ShowDialogCoreAsync(owner, parent: null, owner.View.ZIndex + 1, title, contentFactory);
    }

    internal Task<TResult?> ShowChildDialogAsync<TResult>(
        IModalDialogView parent,
        string title,
        Func<ModalDialog<TResult>, Control> contentFactory)
        => ShowDialogCoreAsync(parent.Owner, parent, parent.ZOrder + 1, title, contentFactory);

    private Task<TResult?> ShowDialogCoreAsync<TResult>(
        ManagedWindow owner,
        IModalDialogView? parent,
        int zOrder,
        string title,
        Func<ModalDialog<TResult>, Control> contentFactory)
    {
        if (_host == null)
            throw new InvalidOperationException("WindowManager is not attached to a host canvas.");

        var dialog = new ModalDialog<TResult>(this, owner);
        var view = new ModalDialogView<TResult>(title, contentFactory(dialog), dialog, parent);
        dialog.Attach(view);
        view.ApplyBounds(owner.Info.Bounds);
        view.ZIndex = zOrder;
        _host.Children.Add(view);
        _dialogs.Add(view);
        view.FocusDialog();

        _ = dialog.Result.ContinueWith(_ => Dispatcher.UIThread.Post(() => CloseDialog(view)),
            TaskScheduler.Default);
        return dialog.Result;
    }

    public void Close(ManagedWindow window)
    {
        if (!_windows.Remove(window))
            return;

        if (_host != null)
            _host.Children.Remove(window.View);

        _preMinimizeState.Remove(window.Info.Id);
        foreach (var dialog in _dialogs.Where(d => ReferenceEquals(d.Owner, window)).ToList())
            dialog.Cancel();

        if (ReferenceEquals(_active, window))
        {
            _active = null;
            // Focus the topmost remaining visible window.
            var next = _windows.LastOrDefault(w => w.IsOnScreen) ?? _windows.LastOrDefault();
            if (next != null)
                Focus(next);
            else
                ActiveWindowChanged?.Invoke(this, null);
        }

        WindowClosed?.Invoke(this, window);
    }

    private void CloseDialog(IModalDialogView dialog)
    {
        if (!_dialogs.Remove(dialog))
            return;

        if (_host != null && dialog is Control control)
            _host.Children.Remove(control);

        foreach (var child in _dialogs.Where(d => ReferenceEquals(d.ParentDialog, dialog)).ToList())
            child.Cancel();
    }

    public void Focus(ManagedWindow window)
    {
        if (!_windows.Contains(window))
            return;

        _zCounter++;
        window.View.ZIndex = _zCounter;

        foreach (var w in _windows)
        {
            var active = ReferenceEquals(w, window);
            w.Info.IsFocused = active;
            w.IsActive = active;
            w.View.SetActive(active);
        }

        if (!ReferenceEquals(_active, window))
        {
            _active = window;
            ActiveWindowChanged?.Invoke(this, window);
        }

        window.View.Focus();
    }

    public void Minimize(ManagedWindow window)
    {
        if (window.Info.State == WindowState.Minimized)
            return;

        _preMinimizeState[window.Info.Id] = window.Info.State;
        foreach (var dialog in _dialogs.Where(d => ReferenceEquals(d.Owner, window)).ToList())
            dialog.Cancel();
        SetState(window, WindowState.Minimized);

        if (ReferenceEquals(_active, window))
        {
            _active = null;
            var next = _windows.LastOrDefault(w => w.IsOnScreen && !ReferenceEquals(w, window));
            if (next != null)
                Focus(next);
            else
                ActiveWindowChanged?.Invoke(this, null);
        }
    }

    public void Restore(ManagedWindow window)
    {
        if (window.Info.State != WindowState.Minimized)
            return;

        _preMinimizeState.TryGetValue(window.Info.Id, out var prev);
        SetState(window, prev == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal);
        if (window.Info.State == WindowState.Maximized)
        {
            window.Info.Bounds = _hostBounds;
            window.View.ApplyBounds(_hostBounds);
        }
        else
        {
            window.View.ApplyBounds(window.Info.RestoreBounds);
        }
        Focus(window);
    }

    /// <summary>Taskbar click: restore minimized windows, minimize the active one, otherwise focus.</summary>
    public void ToggleTaskbar(ManagedWindow window)
    {
        if (!_windows.Contains(window))
            return;
        if (window.Info.State == WindowState.Minimized)
            Restore(window);
        else if (ReferenceEquals(_active, window))
            Minimize(window);
        else
            Focus(window);
    }

    public void ToggleMaximize(ManagedWindow window)
    {
        switch (window.Info.State)
        {
            case WindowState.Maximized:
                SetState(window, WindowState.Normal);
                window.Info.Bounds = window.Info.RestoreBounds;
                window.View.ApplyBounds(window.Info.RestoreBounds);
                UpdateDialogs(window);
                break;
            case WindowState.Minimized:
                // Restore straight to maximized.
                SetState(window, WindowState.Maximized);
                window.Info.Bounds = _hostBounds;
                window.View.ApplyBounds(_hostBounds);
                UpdateDialogs(window);
                Focus(window);
                break;
            default:
                window.Info.RestoreBounds = window.Info.Bounds;
                SetState(window, WindowState.Maximized);
                window.Info.Bounds = _hostBounds;
                window.View.ApplyBounds(_hostBounds);
                UpdateDialogs(window);
                break;
        }
    }

    private void SetState(ManagedWindow window, WindowState state)
    {
        window.Info.State = state;
        window.Sync();
        window.View.ApplyState(state);
    }

    private void OnDrag(ManagedWindow window, DragBoundsEventArgs e)
    {
        if (window.Info.State == WindowState.Maximized)
            return;

        var moved = e.StartBounds.WithPosition(e.StartBounds.Position + e.Delta);
        moved = ClampDrag(moved);
        window.Info.Bounds = moved;
        window.View.ApplyBounds(moved);
        UpdateDialogs(window);
    }

    private void OnResize(ManagedWindow window, ResizeBoundsEventArgs e)
    {
        if (window.Info.State == WindowState.Maximized)
            return;

        var resized = ComputeResize(e.StartBounds, e.Edge, e.Delta, window.Info.MinSize, _hostBounds);
        window.Info.Bounds = resized;
        window.Info.RestoreBounds = resized;
        window.View.ApplyBounds(resized);
        UpdateDialogs(window);
    }

    private void UpdateDialogs(ManagedWindow owner)
    {
        foreach (var dialog in _dialogs.Where(d => ReferenceEquals(d.Owner, owner)))
            dialog.ApplyBounds(owner.Info.Bounds);
    }

    private Rect ResolveInitialBounds(Rect? requested)
    {
        var host = _hostBounds;
        if (host.IsEmpty)
            host = new Rect(0, 0, 1280, 720);

        double w = requested?.Width ?? Math.Min(900, host.Width - 80);
        double h = requested?.Height ?? Math.Min(560, host.Height - 80);
        var cascade = (_windows.Count % 6) * 28;

        double x = requested is { } r
            ? r.X
            : host.X + Math.Max(0, (host.Width - w) / 2) + cascade;
        double y = requested is { } rr
            ? rr.Y
            : host.Y + Math.Max(0, (host.Height - h) / 2) - 40 + cascade;

        x = Math.Clamp(x, host.X, Math.Max(host.X, host.Right - w));
        y = Math.Clamp(y, host.Y, Math.Max(host.Y, host.Bottom - h));
        return new Rect(x, y, w, h);
    }

    private Rect ClampDrag(Rect b)
    {
        var host = _hostBounds;
        var maxLeft = host.Right - 120;
        var minLeft = host.X - b.Width + 120;
        var x = Math.Clamp(b.X, minLeft, maxLeft);
        var y = Math.Clamp(b.Y, host.Y, Math.Max(host.Y, host.Bottom - 36));
        return new Rect(x, y, b.Width, b.Height);
    }

    private static Rect ComputeResize(Rect s, ResizeEdge edge, Point d, Size min, Rect host)
    {
        double x = s.X, y = s.Y, w = s.Width, h = s.Height;

        if (edge.HasFlag(ResizeEdge.Left))
        {
            var nx = s.X + d.X;
            if (s.Right - nx < min.Width)
                nx = s.Right - min.Width;
            nx = Math.Max(nx, host.X);
            x = nx;
            w = s.Right - nx;
        }

        if (edge.HasFlag(ResizeEdge.Right))
        {
            var nw = Math.Min(s.Width + d.X, host.Right - s.X);
            w = Math.Max(nw, min.Width);
        }

        if (edge.HasFlag(ResizeEdge.Top))
        {
            var ny = s.Y + d.Y;
            if (s.Bottom - ny < min.Height)
                ny = s.Bottom - min.Height;
            ny = Math.Max(ny, host.Y);
            y = ny;
            h = s.Bottom - ny;
        }

        if (edge.HasFlag(ResizeEdge.Bottom))
        {
            var nh = Math.Min(s.Height + d.Y, host.Bottom - s.Y);
            h = Math.Max(nh, min.Height);
        }

        return new Rect(x, y, w, h);
    }
}
