using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using RemoteOS.Core.Input;
using RemoteOS.Core.Primitives;
using RemoteOS.Core.Windows;
using Rect = RemoteOS.Core.Primitives.Rect;
using Point = RemoteOS.Core.Primitives.Point;
using WindowState = RemoteOS.Core.Windows.WindowState;

namespace RemoteOS.WindowManager;

/// <summary>
/// A desktop window rendered inside the shell's window host canvas. Handles its own
/// pointer-driven move / resize / focus interactions and delegates state authority to
/// the <see cref="WindowManager"/> via events.
/// </summary>
public class RemoteWindow : TemplatedControl
{
    public static readonly StyledProperty<object?> ContentProperty =
        AvaloniaProperty.Register<RemoteWindow, object?>(nameof(Content));

    public object? Content
    {
        get => GetValue(ContentProperty);
        set => SetValue(ContentProperty, value);
    }

    private Border? _titleDrag;
    private Grid? _resizeLayer;

    private readonly Dictionary<ResizeEdge, Border> _resizeBorders = new();

    private bool _dragging;
    private Point _dragStart;
    private Rect _dragStartBounds;
    private Point _dragDelta;

    private bool _resizing;
    private ResizeEdge _resizeEdge;
    private Point _resizeStart;
    private Rect _resizeStartBounds;
    private Point _resizeDelta;
    private readonly HashSet<RemoteKey> _pressedKeys = [];

    /// <summary>Raised while the user drags the title bar. Carries the press-time bounds and current delta.</summary>
    public event EventHandler<DragBoundsEventArgs>? DragRequested;

    /// <summary>Raised once when a title-bar drag ends, so the host can commit its final bounds.</summary>
    public event EventHandler<DragBoundsEventArgs>? DragCompleted;

    /// <summary>Raised while the user resizes from an edge. Carries the edge, press-time bounds and current delta.</summary>
    public event EventHandler<ResizeBoundsEventArgs>? ResizeRequested;

    /// <summary>Raised once when a resize ends, so the host can flush its final bounds.</summary>
    public event EventHandler<ResizeBoundsEventArgs>? ResizeCompleted;

    /// <summary>Raised when the window should be brought to the front.</summary>
    public event EventHandler? FocusRequested;

    /// <summary>
    /// Raised when a pointer-driven move or resize begins. Hosts containing native child
    /// windows (for example WebView) can temporarily suspend them while the managed window
    /// is changing bounds.
    /// </summary>
    public event EventHandler? BoundsInteractionStarted;

    /// <summary>Raised when the current pointer-driven move or resize has ended.</summary>
    public event EventHandler? BoundsInteractionCompleted;

    public RemoteWindow()
    {
        Focusable = true;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        PseudoClasses.Set(":linux", OperatingSystem.IsLinux());
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;
    }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        base.OnApplyTemplate(e);

        _titleDrag = e.NameScope.Find<Border>("PART_TitleDrag");
        _resizeLayer = e.NameScope.Find<Grid>("PART_ResizeLayer");

        if (_titleDrag != null)
        {
            _titleDrag.PointerPressed += OnTitleDragPressed;
            _titleDrag.PointerMoved += OnTitleDragMoved;
            _titleDrag.PointerReleased += OnTitleDragReleased;
            _titleDrag.PointerCaptureLost += OnPointerCaptureLost;
            _titleDrag.DoubleTapped += OnTitleDragDoubleTapped;
        }

        foreach (var (edge, name) in new[]
        {
            (ResizeEdge.NorthWest, "PART_ResizeNW"),
            (ResizeEdge.North, "PART_ResizeN"),
            (ResizeEdge.NorthEast, "PART_ResizeNE"),
            (ResizeEdge.West, "PART_ResizeW"),
            (ResizeEdge.East, "PART_ResizeE"),
            (ResizeEdge.SouthWest, "PART_ResizeSW"),
            (ResizeEdge.South, "PART_ResizeS"),
            (ResizeEdge.SouthEast, "PART_ResizeSE"),
        })
        {
            var border = e.NameScope.Find<Border>(name);
            if (border != null)
            {
                _resizeBorders[edge] = border;
                var capturedEdge = edge;
                border.PointerPressed += (s, ev) => OnResizePressed(capturedEdge, ev);
                border.PointerMoved += (s, ev) => OnResizeMoved(ev);
                border.PointerReleased += (s, ev) => OnResizeReleased(ev);
                border.PointerCaptureLost += OnPointerCaptureLost;
            }
        }

        this.PointerPressed += OnRootPressed;
        UpdateResizeLayer();
    }

    private void OnRootPressed(object? sender, PointerPressedEventArgs e)
    {
        // Any press inside the window brings it to the front. Does not interfere with
        // child controls because we never mark the event handled.
        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || ViewModel is null)
            return;

        var key = new RemoteKey(e.Key.ToString());
        var input = new RemoteKeyEventArgs(key, ToRemoteModifiers(e.KeyModifiers), !_pressedKeys.Add(key));
        ViewModel.RaiseKeyDown(input);
        if (input.Handled)
            e.Handled = true;
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        if (e.Handled || ViewModel is null)
            return;

        var key = new RemoteKey(e.Key.ToString());
        _pressedKeys.Remove(key);
        var input = new RemoteKeyEventArgs(key, ToRemoteModifiers(e.KeyModifiers));
        ViewModel.RaiseKeyUp(input);
        if (input.Handled)
            e.Handled = true;
    }

    private ManagedWindow? ViewModel => DataContext as ManagedWindow;

    private void OnTitleDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        FocusRequested?.Invoke(this, EventArgs.Empty);

        // No drag while maximized or full-screen (restore via the application command).
        if (ViewModel?.State is WindowState.Maximized or WindowState.FullScreen)
            return;

        _dragging = true;
        _dragStart = ToCore(e.GetPosition(null));
        _dragStartBounds = CurrentBounds();
        _dragDelta = default;
        e.Pointer.Capture(_titleDrag!);
        BoundsInteractionStarted?.Invoke(this, EventArgs.Empty);
    }

    private void OnTitleDragMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
            return;

        _dragDelta = ToCore(e.GetPosition(null)) - _dragStart;
        DragRequested?.Invoke(this, new DragBoundsEventArgs(_dragStartBounds, _dragDelta));
    }

    private void OnTitleDragReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
            return;

        EndDrag();
        e.Pointer.Capture(null);
    }

    private void OnTitleDragDoubleTapped(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ViewModel?.CanMaximize == true)
            ViewModel.ToggleMaximizeCommand.Execute(null);
    }

    private void OnResizePressed(ResizeEdge edge, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        var vm = ViewModel;
        if (vm is not { CanResize: true } || vm.State is WindowState.Maximized or WindowState.FullScreen)
            return;

        FocusRequested?.Invoke(this, EventArgs.Empty);

        _resizing = true;
        _resizeEdge = edge;
        _resizeStart = ToCore(e.GetPosition(null));
        _resizeStartBounds = CurrentBounds();
        _resizeDelta = default;

        if (_resizeBorders.TryGetValue(edge, out var border))
            e.Pointer.Capture(border);

        BoundsInteractionStarted?.Invoke(this, EventArgs.Empty);
    }

    private void OnResizeMoved(PointerEventArgs e)
    {
        if (!_resizing)
            return;

        _resizeDelta = ToCore(e.GetPosition(null)) - _resizeStart;
        ResizeRequested?.Invoke(this, new ResizeBoundsEventArgs(_resizeEdge, _resizeStartBounds, _resizeDelta));
    }

    private void OnResizeReleased(PointerReleasedEventArgs e)
    {
        if (!_resizing)
            return;
        EndResize();
        e.Pointer.Capture(null);
    }

    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_dragging)
            EndDrag();
        else if (_resizing)
            EndResize();
    }

    private void EndDrag()
    {
        if (!_dragging)
            return;

        _dragging = false;
        DragCompleted?.Invoke(this, new DragBoundsEventArgs(_dragStartBounds, _dragDelta));
        BoundsInteractionCompleted?.Invoke(this, EventArgs.Empty);
    }

    private void EndResize()
    {
        if (!_resizing)
            return;

        _resizing = false;
        // Pointer capture can end without a final PointerMoved event, so retain the most
        // recent delta calculated in OnResizeMoved.
        ResizeCompleted?.Invoke(this, new ResizeBoundsEventArgs(_resizeEdge, _resizeStartBounds, _resizeDelta));
        BoundsInteractionCompleted?.Invoke(this, EventArgs.Empty);
    }

    private Rect CurrentBounds()
    {
        var left = Canvas.GetLeft(this);
        var top = Canvas.GetTop(this);
        if (double.IsNaN(left)) left = Bounds.X;
        if (double.IsNaN(top)) top = Bounds.Y;

        var w = Bounds.Width > 0 ? Bounds.Width : Width;
        var h = Bounds.Height > 0 ? Bounds.Height : Height;
        return new Rect(left, top, w, h);
    }

    private static Point ToCore(Avalonia.Point p) => new(p.X, p.Y);

    // ----- Called by WindowManager to apply authoritative state -----

    internal void ApplyBounds(Rect bounds)
    {
        Canvas.SetLeft(this, bounds.X);
        Canvas.SetTop(this, bounds.Y);
        Width = bounds.Width;
        Height = bounds.Height;
    }

    /// <summary>Updates only the Canvas position; size and child layout stay unchanged.</summary>
    internal void ApplyPosition(Point position)
    {
        Canvas.SetLeft(this, position.X);
        Canvas.SetTop(this, position.Y);
    }

    internal void ApplyState(WindowState state)
    {
        IsVisible = state != WindowState.Minimized;
        PseudoClasses.Set(":fullscreen", state == WindowState.FullScreen);
        UpdateResizeLayer();
    }

    internal void SetActive(bool active)
    {
        PseudoClasses.Set(":active", active);
        if (!active)
            _pressedKeys.Clear();
    }

    private void UpdateResizeLayer()
    {
        if (_resizeLayer == null)
            return;

        var vm = ViewModel;
        var canResize = vm?.CanResize ?? true;
        var normal = vm?.State == WindowState.Normal;
        _resizeLayer.IsVisible = canResize && normal;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        UpdateResizeLayer();
    }

    private static RemoteKeyModifiers ToRemoteModifiers(KeyModifiers modifiers)
    {
        var result = RemoteKeyModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Shift)) result |= RemoteKeyModifiers.Shift;
        if (modifiers.HasFlag(KeyModifiers.Control)) result |= RemoteKeyModifiers.Control;
        if (modifiers.HasFlag(KeyModifiers.Alt)) result |= RemoteKeyModifiers.Alt;
        if (modifiers.HasFlag(KeyModifiers.Meta)) result |= RemoteKeyModifiers.Meta;
        return result;
    }
}
