using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
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

    private bool _resizing;
    private ResizeEdge _resizeEdge;
    private Point _resizeStart;
    private Rect _resizeStartBounds;

    /// <summary>Raised while the user drags the title bar. Carries the press-time bounds and current delta.</summary>
    public event EventHandler<DragBoundsEventArgs>? DragRequested;

    /// <summary>Raised while the user resizes from an edge. Carries the edge, press-time bounds and current delta.</summary>
    public event EventHandler<ResizeBoundsEventArgs>? ResizeRequested;

    /// <summary>Raised when the window should be brought to the front.</summary>
    public event EventHandler? FocusRequested;

    public RemoteWindow()
    {
        Focusable = true;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
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

    private ManagedWindow? ViewModel => DataContext as ManagedWindow;

    private void OnTitleDragPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        FocusRequested?.Invoke(this, EventArgs.Empty);

        // No drag while maximized (restore via caption / double-click).
        if (ViewModel?.State == WindowState.Maximized)
            return;

        _dragging = true;
        _dragStart = ToCore(e.GetPosition(null));
        _dragStartBounds = CurrentBounds();
        e.Pointer.Capture(_titleDrag!);
    }

    private void OnTitleDragMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
            return;

        var delta = ToCore(e.GetPosition(null)) - _dragStart;
        DragRequested?.Invoke(this, new DragBoundsEventArgs(_dragStartBounds, delta));
    }

    private void OnTitleDragReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
            return;

        _dragging = false;
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
        if (vm is not { CanResize: true } || vm.State == WindowState.Maximized)
            return;

        FocusRequested?.Invoke(this, EventArgs.Empty);

        _resizing = true;
        _resizeEdge = edge;
        _resizeStart = ToCore(e.GetPosition(null));
        _resizeStartBounds = CurrentBounds();

        if (_resizeBorders.TryGetValue(edge, out var border))
            e.Pointer.Capture(border);
    }

    private void OnResizeMoved(PointerEventArgs e)
    {
        if (!_resizing)
            return;

        var delta = ToCore(e.GetPosition(null)) - _resizeStart;
        ResizeRequested?.Invoke(this, new ResizeBoundsEventArgs(_resizeEdge, _resizeStartBounds, delta));
    }

    private void OnResizeReleased(PointerReleasedEventArgs e)
    {
        if (!_resizing)
            return;
        _resizing = false;
        e.Pointer.Capture(null);
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

    internal void ApplyState(WindowState state)
    {
        IsVisible = state != WindowState.Minimized;
        UpdateResizeLayer();
    }

    internal void SetActive(bool active) => PseudoClasses.Set(":active", active);

    private void UpdateResizeLayer()
    {
        if (_resizeLayer == null)
            return;

        var vm = ViewModel;
        var canResize = vm?.CanResize ?? true;
        var normal = vm?.State != WindowState.Maximized;
        _resizeLayer.IsVisible = canResize && normal;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        UpdateResizeLayer();
    }
}
