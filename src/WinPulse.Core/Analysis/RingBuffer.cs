namespace WinPulse.Core.Analysis;

/// <summary>Кольцевой буфер фиксированной ёмкости. Индекс 0 — самое старое значение.</summary>
public sealed class RingBuffer
{
    private readonly double[] _data;
    private int _start;
    private int _count;

    public RingBuffer(int capacity) => _data = new double[Math.Max(1, capacity)];

    public int Capacity => _data.Length;
    public int Count => _count;

    public void Add(double value)
    {
        if (_count < _data.Length)
        {
            _data[(_start + _count) % _data.Length] = value;
            _count++;
        }
        else
        {
            _data[_start] = value;
            _start = (_start + 1) % _data.Length;
        }
    }

    public double this[int index] => _data[(_start + index) % _data.Length];

    public double Last => _count == 0 ? 0 : this[_count - 1];

    public double[] ToArray()
    {
        var result = new double[_count];
        for (int i = 0; i < _count; i++) result[i] = this[i];
        return result;
    }

    public double AverageOfLast(int n)
    {
        n = Math.Min(n, _count);
        if (n <= 0) return 0;
        double sum = 0;
        for (int i = _count - n; i < _count; i++) sum += this[i];
        return sum / n;
    }
}

public static class Trend
{
    /// <summary>Линейная регрессия по точкам с шагом stepSeconds. Возвращает наклон в единицах за час и R².</summary>
    public static (double SlopePerHour, double R2) Linear(IReadOnlyList<double> ys, double stepSeconds)
    {
        int n = ys.Count;
        if (n < 3 || stepSeconds <= 0) return (0, 0);
        double meanX = (n - 1) * stepSeconds / 2.0;
        double meanY = 0;
        for (int i = 0; i < n; i++) meanY += ys[i];
        meanY /= n;
        double sxx = 0, sxy = 0, syy = 0;
        for (int i = 0; i < n; i++)
        {
            double dx = i * stepSeconds - meanX;
            double dy = ys[i] - meanY;
            sxx += dx * dx;
            sxy += dx * dy;
            syy += dy * dy;
        }
        if (sxx <= 0) return (0, 0);
        double slopePerSecond = sxy / sxx;
        double r2 = syy > 0 ? sxy * sxy / (sxx * syy) : 0;
        return (slopePerSecond * 3600.0, r2);
    }
}
