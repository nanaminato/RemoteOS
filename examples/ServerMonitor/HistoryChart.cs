using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace RemoteOS.Examples.ServerMonitor;

/// <summary>Small dependency-free line chart for a percentage or rate history.</summary>
public sealed class HistoryChart : Control
{
    private readonly IBrush _lineBrush;
    private readonly double _maximum;
    private IReadOnlyList<double> _values = Array.Empty<double>();

    public HistoryChart(Color color, double maximum = 100)
    {
        _lineBrush = new SolidColorBrush(color);
        _maximum = Math.Max(maximum, 1);
        Height = 124;
        MinWidth = 280;
    }

    public void Update(IReadOnlyList<double> values)
    {
        _values = values;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var rect = Bounds.Deflate(1);
        context.DrawRectangle(new SolidColorBrush(Color.Parse("#151D2B")), new Pen(new SolidColorBrush(Color.Parse("#2C3850"))), rect);
        if (rect.Width <= 1 || rect.Height <= 1)
            return;

        var gridPen = new Pen(new SolidColorBrush(Color.Parse("#263247")), 1);
        for (var row = 1; row < 4; row++)
        {
            var y = rect.Top + rect.Height * row / 4;
            context.DrawLine(gridPen, new Point(rect.Left, y), new Point(rect.Right, y));
        }

        if (_values.Count < 2)
            return;

        var linePen = new Pen(_lineBrush, 2);
        for (var index = 1; index < _values.Count; index++)
        {
            var previous = ToPoint(index - 1, rect);
            var current = ToPoint(index, rect);
            context.DrawLine(linePen, previous, current);
        }
    }

    private Point ToPoint(int index, Rect rect)
    {
        var x = rect.Left + rect.Width * index / Math.Max(_values.Count - 1, 1);
        var ratio = Math.Clamp(_values[index] / _maximum, 0, 1);
        return new Point(x, rect.Bottom - rect.Height * ratio);
    }
}
