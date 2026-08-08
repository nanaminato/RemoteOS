namespace RemoteOS.Examples.ServerMonitor;

/// <summary>Fixed-size chronological buffer used by the chart controls.</summary>
public sealed class MetricHistory
{
    private readonly List<double> _values = [];

    public IReadOnlyList<double> Values => _values;

    public void Add(double value, int capacity)
    {
        capacity = Math.Max(capacity, 2);
        _values.Add(double.IsFinite(value) ? value : 0);
        if (_values.Count > capacity)
            _values.RemoveRange(0, _values.Count - capacity);
    }

    public void Clear() => _values.Clear();
}
