namespace WinPulse.Core.Analysis;

public enum AppKind { Application, Service, System }

public sealed class ProcessRow
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string? Detail { get; init; }
    public double CpuPercent { get; init; }
    public long PrivateMemory { get; init; }
    public double ReadBps { get; init; }
    public double WriteBps { get; init; }
    public double GpuPercent { get; init; }
    public long GpuMemory { get; init; }
    public int Handles { get; init; }
    public int Threads { get; init; }
    public double IoBps => ReadBps + WriteBps;
}

/// <summary>Приложение = группа процессов (все процессы Chrome, одна служба svchost и т. п.).</summary>
public sealed class AppGroup
{
    public string Key { get; init; } = "";
    public string DisplayName { get; set; } = "";
    public string? Publisher { get; set; }
    public string? Path { get; set; }
    public AppKind Kind { get; set; }
    public string? Hint { get; set; }
    public bool IsCritical { get; set; }
    public List<ProcessRow> Processes { get; } = new();

    public double CpuPercent { get; set; }
    public long PrivateMemory { get; set; }
    public double ReadBps { get; set; }
    public double WriteBps { get; set; }
    public double GpuPercent { get; set; }
    public long GpuMemory { get; set; }
    public int Handles { get; set; }
    public int Threads { get; set; }
    public double IoBps => ReadBps + WriteBps;

    // Заполняется историей
    public double CpuAvg1m { get; set; }
    public double CpuAvg5m { get; set; }
    public double IoAvg1m { get; set; }
    public double IoAvg5m { get; set; }
    /// <summary>Изменение памяти, байт в час (линейная регрессия).</summary>
    public double MemorySlopePerHour { get; set; }
    public double MemoryTrendR2 { get; set; }
    public double[] MemoryHistory { get; set; } = [];

    /// <summary>Похоже на утечку памяти: растёт стабильно и заметно.</summary>
    public bool LooksLikeLeak =>
        MemorySlopePerHour > 200.0 * 1024 * 1024 && MemoryTrendR2 > 0.8 && PrivateMemory > 300L * 1024 * 1024;
}
