using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using RemoteOS.Core.Primitives;
using Rect = RemoteOS.Core.Primitives.Rect;

namespace RemoteOS.WindowManager;

/// <summary>Represents a running modal dialog and provides the result channel to its content.</summary>
public sealed class ModalDialog<TResult>
{
    private readonly TaskCompletionSource<TResult?> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly WindowManager _manager;
    private IModalDialogView? _view;

    internal ModalDialog(WindowManager manager, ManagedWindow owner)
    {
        _manager = manager;
        Owner = owner;
    }

    internal ManagedWindow Owner { get; }
    public Task<TResult?> Result => _completion.Task;

    public void Close(TResult result) => _completion.TrySetResult(result);
    public void Cancel() => _completion.TrySetResult(default);

    /// <summary>Opens a child modal dialog. The current dialog remains blocked until its child closes.</summary>
    public Task<TChild?> ShowDialogAsync<TChild>(
        string title,
        Func<ModalDialog<TChild>, Control> contentFactory)
    {
        if (_view is null)
            throw new InvalidOperationException("The dialog has not been shown yet.");
        return _manager.ShowChildDialogAsync(_view, title, contentFactory);
    }

    internal void Attach(IModalDialogView view) => _view = view;
}

internal interface IModalDialogView
{
    ManagedWindow Owner { get; }
    IModalDialogView? ParentDialog { get; }
    int ZOrder { get; }
    void ApplyBounds(Rect bounds);
    void Cancel();
}

/// <summary>Desktop-wide dimming overlay with a centred, parent-owned dialog surface.</summary>
internal sealed class ModalDialogView<TResult> : Grid, IModalDialogView
{
    private readonly ModalDialog<TResult> _dialog;

    public ModalDialogView(string title, Control content, ModalDialog<TResult> dialog, IModalDialogView? parent)
    {
        _dialog = dialog;
        ParentDialog = parent;
        Focusable = true;
        Background = Brushes.Transparent;

        Children.Add(new Border { Background = new SolidColorBrush(Color.Parse("#42000000")) });

        var closeButton = new Button
        {
            Content = "\uE8BB",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 10,
            Width = 38,
            Height = 34,
            Padding = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        closeButton.Click += (_, _) => _dialog.Cancel();

        var panel = new Border
        {
            Width = 420,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.Parse("#C8C8C8")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            BoxShadow = new BoxShadows(new BoxShadow { OffsetX = 0, OffsetY = 16, Blur = 32, Color = Color.Parse("#99000000") }),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var frame = new Grid { RowDefinitions = new RowDefinitions("34,*") };
        var titleBar = new Border { Background = new SolidColorBrush(Color.Parse("#F3F3F3")), CornerRadius = new CornerRadius(5, 5, 0, 0) };
        var titleGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        titleGrid.Children.Add(new TextBlock
        {
            Text = title,
            Margin = new Thickness(12, 0),
            Foreground = new SolidColorBrush(Color.Parse("#202020")),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 13,
        });
        Grid.SetColumn(closeButton, 1);
        titleGrid.Children.Add(closeButton);
        titleBar.Child = titleGrid;
        frame.Children.Add(titleBar);
        Grid.SetRow(content, 1);
        frame.Children.Add(content);
        panel.Child = frame;
        Children.Add(panel);

        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            _dialog.Cancel();
            e.Handled = true;
        };
    }

    public ManagedWindow Owner => _dialog.Owner;
    public IModalDialogView? ParentDialog { get; }
    public int ZOrder => ZIndex;

    public void ApplyBounds(Rect bounds)
    {
        Canvas.SetLeft(this, bounds.X);
        Canvas.SetTop(this, bounds.Y);
        Width = bounds.Width;
        Height = bounds.Height;
    }

    public void Cancel() => _dialog.Cancel();

    public void FocusDialog() => Dispatcher.UIThread.Post(() => Focus());
}
