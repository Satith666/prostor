namespace WinPulse.Core.Analysis;

/// <summary>
/// История по каждому приложению: процессор и диск — 300 точек раз в секунду (5 минут),
/// память — 360 точек раз в 10 секунд (1 час) для поиска утечек.
/// </summary>
public sealed class AppHistory
{
    private const int MemoryStepSeconds = 10;

    private sealed class Entry
    {
        public readonly RingBuffer Cpu = new(300);
        public readonly RingBuffer Io = new(300);
        public readonly RingBuffer Memory = new(360);
        public DateTime LastSeen;
        public DateTime LastMemorySample = DateTime.MinValue;
        public double Slope;
        public double R2;
        public double[] MemoryArray = [];
    }

    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private DateTime _lastPurge = DateTime.MinValue;

    public int Count => _entries.Count;

    public void Update(IEnumerable<AppGroup> groups, DateTime now)
    {
        foreach (var g in groups)
        {
            if (!_entries.TryGetValue(g.Key, out var e))
            {
                e = new Entry();
                _entries[g.Key] = e;
            }

            e.Cpu.Add(g.CpuPercent);
            e.Io.Add(g.IoBps);
            e.LastSeen = now;

            if ((now - e.LastMemorySample).TotalSeconds >= MemoryStepSeconds - 0.5)
            {
                e.Memory.Add(g.PrivateMemory);
                e.LastMemorySample = now;
                e.MemoryArray = e.Memory.ToArray();
                if (e.Memory.Count >= 30)
                {
                    var (slope, r2) = Trend.Linear(e.MemoryArray, MemoryStepSeconds);
                    e.Slope = slope;
                    e.R2 = r2;
                }
            }

            g.CpuAvg1m = e.Cpu.AverageOfLast(60);
            g.CpuAvg5m = e.Cpu.AverageOfLast(300);
            g.IoAvg1m = e.Io.AverageOfLast(60);
            g.IoAvg5m = e.Io.AverageOfLast(300);
            g.MemorySlopePerHour = e.Slope;
            g.MemoryTrendR2 = e.R2;
            g.MemoryHistory = e.MemoryArray;
        }

        if ((now - _lastPurge).TotalMinutes >= 1)
        {
            _lastPurge = now;
            var stale = _entries.Where(kv => (now - kv.Value.LastSeen).TotalMinutes > 15).Select(kv => kv.Key).ToList();
            foreach (var key in stale) _entries.Remove(key);
        }
    }
}
