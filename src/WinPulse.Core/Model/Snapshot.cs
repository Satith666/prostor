namespace WinPulse.Core.Model;

/// <summary>Сырые данные процесса за один замер (абсолютные счётчики, не скорости).</summary>
public sealed record ProcessSample(
    int Pid,
    int ParentPid,
    string Name,
    long CreateTime,
    long CpuTime100ns,
    long PrivateWorkingSet,
    long WorkingSet,
    long PrivateBytes,
    long ReadBytes,
    long WriteBytes,
    long OtherBytes,
    int Handles,
    int Threads,
    int SessionId);

public sealed class CpuState
{
    public double UtilityPercent { get; init; }
    public double? PerformancePercent { get; init; }
    public double? NominalMHz { get; init; }
    public double? EffectiveMHz =>
        PerformancePercent is double p && NominalMHz is double n && n > 0 ? n * p / 100.0 : null;
    public int ProcessCount { get; init; }
    public int ThreadCount { get; init; }
    public int HandleCount { get; init; }
    public TimeSpan Uptime { get; init; }
}

public sealed class MemoryState
{
    public long TotalPhysical { get; init; }
    public long InstalledPhysical { get; init; }
    public long Available { get; init; }
    public long? Standby { get; init; }
    public long? Modified { get; init; }
    public long? Free { get; init; }
    public long CommitTotal { get; init; }
    public long CommitLimit { get; init; }
    public long KernelPaged { get; init; }
    public long KernelNonPaged { get; init; }
    public double? HardFaultsPerSec { get; init; }
    public long Used => Math.Max(0, TotalPhysical - Available);
    public long HardwareReserved => InstalledPhysical > TotalPhysical ? InstalledPhysical - TotalPhysical : 0;
    public double UsedPercent => TotalPhysical > 0 ? Used * 100.0 / TotalPhysical : 0;
}

public enum DiskMediaType { Unknown, Hdd, Ssd, Nvme }

public sealed record VolumeState(string Letter, string? Label, long TotalBytes, long FreeBytes);

public sealed class DiskState
{
    public int Index { get; init; }
    public string Model { get; init; } = "";
    public DiskMediaType MediaType { get; init; }
    public string Bus { get; init; } = "";
    public IReadOnlyList<VolumeState> Volumes { get; init; } = [];
    public double ActivePercent { get; init; }
    public double LatencyMs { get; init; }
    public double ReadBps { get; init; }
    public double WriteBps { get; init; }
    public double QueueLength { get; init; }
    public bool IsSystemDisk => Volumes.Any(v => v.Letter.StartsWith("C", StringComparison.OrdinalIgnoreCase));
}

public sealed class NetworkAdapterState
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public string Kind { get; init; } = "";
    public long SpeedBitsPerSec { get; init; }
    public double ReceiveBps { get; init; }
    public double SendBps { get; init; }
}

public sealed class GpuAdapterState
{
    public string Luid { get; init; } = "";
    public string Name { get; init; } = "";
    public int VendorId { get; init; }
    public long DedicatedTotal { get; init; }
    public long DedicatedUsed { get; init; }
    public double UtilPercent { get; init; }
    public IReadOnlyDictionary<string, double> EngineUtil { get; init; } = new Dictionary<string, double>();
}

public readonly record struct GpuProcessState(double UtilPercent, long DedicatedBytes);

public sealed class SystemInfo
{
    public string MachineName { get; init; } = "";
    public string OsName { get; init; } = "";
    public int OsBuild { get; init; }
    public string CpuName { get; init; } = "";
    public int LogicalCores { get; init; }
    public long InstalledMemory { get; init; }
}

public sealed class RawSnapshot
{
    public DateTime Timestamp { get; init; }
    public IReadOnlyList<ProcessSample> Processes { get; init; } = [];
    public CpuState Cpu { get; init; } = new();
    public MemoryState Memory { get; init; } = new();
    public IReadOnlyList<DiskState> Disks { get; init; } = [];
    public IReadOnlyList<NetworkAdapterState> Network { get; init; } = [];
    public IReadOnlyList<GpuAdapterState> Gpus { get; init; } = [];
    public IReadOnlyDictionary<int, GpuProcessState> GpuByPid { get; init; } = new Dictionary<int, GpuProcessState>();
}

public sealed record AppIdentity(string? Path, string? Description, string? Company);

public sealed record ServiceEntry(string Name, string DisplayName);

/// <summary>Источник данных о системе: настоящая Windows или демо-режим.</summary>
public interface ISystemSource : IDisposable
{
    SystemInfo Info { get; }
    bool IsDemo { get; }
    /// <summary>Вызывается раз в секунду из фонового потока.</summary>
    RawSnapshot Capture();
    /// <summary>Путь, описание и издатель процесса (с кэшем). Вызывается из того же потока, что Capture.</summary>
    AppIdentity Identify(ProcessSample process);
    IReadOnlyDictionary<int, IReadOnlyList<ServiceEntry>> ServicesByPid { get; }
}
