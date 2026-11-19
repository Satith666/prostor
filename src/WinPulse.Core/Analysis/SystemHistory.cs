using WinPulse.Core.Model;

namespace WinPulse.Core.Analysis;

public sealed class DiskSeries
{
    public RingBuffer Active { get; } = new(SystemHistory.Capacity);
    public RingBuffer Latency { get; } = new(SystemHistory.Capacity);
    public RingBuffer Read { get; } = new(SystemHistory.Capacity);
    public RingBuffer Write { get; } = new(SystemHistory.Capacity);
    internal DateTime LastSeen;
}

public sealed class GpuSeries
{
    public RingBuffer Util { get; } = new(SystemHistory.Capacity);
    public RingBuffer MemUsed { get; } = new(SystemHistory.Capacity);
    internal DateTime LastSeen;
}

public sealed class NetSeries
{
    public RingBuffer Rx { get; } = new(SystemHistory.Capacity);
    public RingBuffer Tx { get; } = new(SystemHistory.Capacity);
    internal DateTime LastSeen;
}

/// <summary>История системы за 10 минут (600 точек раз в секунду).</summary>
public sealed class SystemHistory
{
    public const int Capacity = 600;

    public RingBuffer Cpu { get; } = new(Capacity);
    public RingBuffer MemPercent { get; } = new(Capacity);
    public RingBuffer MemAvailablePercent { get; } = new(Capacity);
    public RingBuffer HardFaults { get; } = new(Capacity);
    public RingBuffer NetRx { get; } = new(Capacity);
    public RingBuffer NetTx { get; } = new(Capacity);
    public RingBuffer GpuUtil { get; } = new(Capacity);
    public Dictionary<int, DiskSeries> Disks { get; } = new();
    public Dictionary<string, GpuSeries> Gpus { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, NetSeries> Adapters { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void Add(RawSnapshot s)
    {
        var now = s.Timestamp;
        Cpu.Add(Math.Clamp(s.Cpu.UtilityPercent, 0, 100));
        MemPercent.Add(s.Memory.UsedPercent);
        MemAvailablePercent.Add(s.Memory.TotalPhysical > 0 ? s.Memory.Available * 100.0 / s.Memory.TotalPhysical : 0);
        HardFaults.Add(s.Memory.HardFaultsPerSec ?? 0);

        double rx = 0, tx = 0;
        foreach (var a in s.Network)
        {
            rx += a.ReceiveBps;
            tx += a.SendBps;
            if (!Adapters.TryGetValue(a.Id, out var ns)) Adapters[a.Id] = ns = new NetSeries();
            ns.Rx.Add(a.ReceiveBps);
            ns.Tx.Add(a.SendBps);
            ns.LastSeen = now;
        }
        NetRx.Add(rx);
        NetTx.Add(tx);

        double gpuMax = 0;
        foreach (var g in s.Gpus)
        {
            gpuMax = Math.Max(gpuMax, g.UtilPercent);
            if (!Gpus.TryGetValue(g.Luid, out var gs)) Gpus[g.Luid] = gs = new GpuSeries();
            gs.Util.Add(g.UtilPercent);
            gs.MemUsed.Add(g.DedicatedUsed);
            gs.LastSeen = now;
        }
        GpuUtil.Add(gpuMax);

        foreach (var d in s.Disks)
        {
            if (!Disks.TryGetValue(d.Index, out var ds)) Disks[d.Index] = ds = new DiskSeries();
            ds.Active.Add(d.ActivePercent);
            ds.Latency.Add(d.LatencyMs);
            ds.Read.Add(d.ReadBps);
            ds.Write.Add(d.WriteBps);
            ds.LastSeen = now;
        }

        Purge(Disks, now, x => x.LastSeen);
        Purge(Gpus, now, x => x.LastSeen);
        Purge(Adapters, now, x => x.LastSeen);
    }

    private static void Purge<TKey, TValue>(Dictionary<TKey, TValue> dict, DateTime now, Func<TValue, DateTime> lastSeen)
        where TKey : notnull
    {
        List<TKey>? stale = null;
        foreach (var kv in dict)
            if ((now - lastSeen(kv.Value)).TotalSeconds > 60) (stale ??= new()).Add(kv.Key);
        if (stale is null) return;
        foreach (var k in stale) dict.Remove(k);
    }

    public HistorySnapshot Snapshot() => new()
    {
        Cpu = Cpu.ToArray(),
        MemPercent = MemPercent.ToArray(),
        NetRx = NetRx.ToArray(),
        NetTx = NetTx.ToArray(),
        GpuUtil = GpuUtil.ToArray(),
        Disks = Disks.ToDictionary(kv => kv.Key, kv => new DiskHistory(
            kv.Value.Active.ToArray(), kv.Value.Latency.ToArray(), kv.Value.Read.ToArray(), kv.Value.Write.ToArray())),
        Gpus = Gpus.ToDictionary(kv => kv.Key, kv => new GpuHistory(kv.Value.Util.ToArray(), kv.Value.MemUsed.ToArray()),
            StringComparer.OrdinalIgnoreCase),
        Adapters = Adapters.ToDictionary(kv => kv.Key, kv => new NetHistory(kv.Value.Rx.ToArray(), kv.Value.Tx.ToArray()),
            StringComparer.OrdinalIgnoreCase),
    };
}

public sealed record DiskHistory(double[] Active, double[] Latency, double[] Read, double[] Write);
public sealed record GpuHistory(double[] Util, double[] MemUsed);
public sealed record NetHistory(double[] Rx, double[] Tx);

/// <summary>Неизменяемая копия истории для интерфейса.</summary>
public sealed class HistorySnapshot
{
    public double[] Cpu { get; init; } = [];
    public double[] MemPercent { get; init; } = [];
    public double[] NetRx { get; init; } = [];
    public double[] NetTx { get; init; } = [];
    public double[] GpuUtil { get; init; } = [];
    public IReadOnlyDictionary<int, DiskHistory> Disks { get; init; } = new Dictionary<int, DiskHistory>();
    public IReadOnlyDictionary<string, GpuHistory> Gpus { get; init; } = new Dictionary<string, GpuHistory>();
    public IReadOnlyDictionary<string, NetHistory> Adapters { get; init; } = new Dictionary<string, NetHistory>();

    /// <summary>Последние n точек массива (для мини-графиков).</summary>
    public static double[] Tail(double[] values, int n) =>
        values.Length <= n ? values : values[^n..];
}
