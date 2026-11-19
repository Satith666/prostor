using WinPulse.Core.Model;

namespace WinPulse.Core.Analysis;

/// <summary>
/// Из чего состоит оперативная память — так, чтобы сумма сходилась с объёмом:
/// программы + система + сжатая + изменённая + кэш + свободно (+ зарезервировано оборудованием).
/// </summary>
public sealed class MemoryBreakdown
{
    public long Programs { get; init; }
    public long System { get; init; }
    public long Compressed { get; init; }
    public long Modified { get; init; }
    public long Standby { get; init; }
    public long Free { get; init; }
    public long HardwareReserved { get; init; }
    public long Total { get; init; }
    /// <summary>Занято в понимании пользователя: программы + система + сжатая + изменённая.</summary>
    public long InUse => Programs + System + Compressed + Modified;
}

public static class MemoryMath
{
    private static readonly HashSet<string> NotPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "Memory Compression", "System", "Registry", "Secure System",
    };

    public static MemoryBreakdown Compute(MemoryState m, IEnumerable<ProcessSample> processes)
    {
        long programs = 0;
        long compressed = 0;
        foreach (var p in processes)
        {
            if (p.Name.Equals("Memory Compression", StringComparison.OrdinalIgnoreCase))
            {
                compressed += p.WorkingSet;
                continue;
            }
            if (NotPrograms.Contains(p.Name) || p.Pid == 0) continue;
            programs += p.PrivateWorkingSet;
        }

        long modified = Math.Max(0, m.Modified ?? 0);
        long active = Math.Max(0, m.TotalPhysical - m.Available - modified);
        compressed = Math.Min(compressed, active);
        programs = Math.Min(programs, active - compressed);
        long system = Math.Max(0, active - programs - compressed);

        long free = Math.Max(0, m.Free ?? 0);
        long standby = m.Standby is long sb ? Math.Max(0, sb) : Math.Max(0, m.Available - free);
        // Счётчики берутся в разные моменты; подгоняем кэш, чтобы сумма не превысила объём.
        long sum = programs + system + compressed + modified + standby + free;
        if (sum > m.TotalPhysical && m.TotalPhysical > 0)
            standby = Math.Max(0, standby - (sum - m.TotalPhysical));

        return new MemoryBreakdown
        {
            Programs = programs,
            System = system,
            Compressed = compressed,
            Modified = modified,
            Standby = standby,
            Free = free,
            HardwareReserved = m.HardwareReserved,
            Total = m.InstalledPhysical > 0 ? m.InstalledPhysical : m.TotalPhysical,
        };
    }
}
