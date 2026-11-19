using WinPulse.Core.Analysis;
using WinPulse.Core.Model;

namespace WinPulse.Core;

/// <summary>Всё, что нужно интерфейсу за один такт (раз в секунду).</summary>
public sealed class MonitorFrame
{
    public required RawSnapshot Raw { get; init; }
    public required IReadOnlyList<AppGroup> Apps { get; init; }
    public required MemoryBreakdown Memory { get; init; }
    public required HistorySnapshot History { get; init; }
    public required IReadOnlyList<Verdict> Verdicts { get; init; }
    public required SystemInfo Info { get; init; }
    public bool IsDemo { get; init; }
}

/// <summary>Замер → скорости → приложения → история → выводы.</summary>
public sealed class MonitorEngine : IDisposable
{
    private readonly ISystemSource _source;
    private readonly ProcessRateCalculator _rates = new();
    private readonly AppGrouper _grouper;
    private readonly AppHistory _appHistory = new();
    private readonly SystemHistory _history = new();

    public MonitorEngine(ISystemSource source, string windowsDir)
    {
        _source = source;
        _grouper = new AppGrouper(windowsDir);
    }

    public ISystemSource Source => _source;

    public MonitorFrame Tick()
    {
        var raw = _source.Capture();
        var rates = _rates.Compute(raw.Processes, raw.Timestamp, _source.Info.LogicalCores);
        var apps = _grouper.Build(raw, rates, _source);
        _appHistory.Update(apps, raw.Timestamp);
        _history.Add(raw);
        var memory = MemoryMath.Compute(raw.Memory, raw.Processes);
        var verdicts = VerdictRules.Evaluate(raw, apps, _history, memory);
        return new MonitorFrame
        {
            Raw = raw,
            Apps = apps,
            Memory = memory,
            History = _history.Snapshot(),
            Verdicts = verdicts,
            Info = _source.Info,
            IsDemo = _source.IsDemo,
        };
    }

    public void Dispose() => _source.Dispose();
}
