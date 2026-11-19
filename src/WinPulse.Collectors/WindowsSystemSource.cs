using WinPulse.Collectors.Gpu;
using WinPulse.Collectors.Interop;
using WinPulse.Core;
using WinPulse.Core.Model;

namespace WinPulse.Collectors;

/// <summary>Настоящая Windows: процессы, процессор, память, диски, сеть, видеокарты.</summary>
public sealed class WindowsSystemSource : ISystemSource
{
    private readonly ProcessCollector _processes = new();
    private readonly IdentityCache _identities = new();
    private readonly ServiceMap _services = new();
    private readonly PdhQuery _query = new();
    private readonly PdhCounter? _utility;
    private readonly PdhCounter? _performance;
    private readonly PdhCounter? _frequency;
    private readonly PdhCounter? _standbyReserve;
    private readonly PdhCounter? _standbyNormal;
    private readonly PdhCounter? _standbyCore;
    private readonly PdhCounter? _modified;
    private readonly PdhCounter? _freeZero;
    private readonly PdhCounter? _pagesInput;
    private readonly DiskCollector _disks;
    private readonly NetworkCollector _network = new();
    private readonly GpuCollector _gpu;
    private long _prevIdle, _prevKernel, _prevUser;

    public WindowsSystemSource()
    {
        Info = OsInfo.ReadSystemInfo();
        _utility = _query.Add(@"\Processor Information(_Total)\% Processor Utility");
        _performance = _query.Add(@"\Processor Information(_Total)\% Processor Performance");
        _frequency = _query.Add(@"\Processor Information(_Total)\Processor Frequency");
        _standbyReserve = _query.Add(@"\Memory\Standby Cache Reserve Bytes");
        _standbyNormal = _query.Add(@"\Memory\Standby Cache Normal Priority Bytes");
        _standbyCore = _query.Add(@"\Memory\Standby Cache Core Bytes");
        _modified = _query.Add(@"\Memory\Modified Page List Bytes");
        _freeZero = _query.Add(@"\Memory\Free & Zero Page List Bytes");
        _pagesInput = _query.Add(@"\Memory\Pages Input/sec");
        _disks = new DiskCollector(_query);
        _query.Collect();
        _gpu = new GpuCollector();
        Native.GetSystemTimes(out _prevIdle, out _prevKernel, out _prevUser);
    }

    public SystemInfo Info { get; }
    public bool IsDemo => false;
    public IReadOnlyDictionary<int, IReadOnlyList<ServiceEntry>> ServicesByPid => _services.Current;

    public RawSnapshot Capture()
    {
        var now = DateTime.Now;

        List<ProcessSample> processes;
        try
        {
            processes = _processes.Capture();
        }
        catch (Exception ex)
        {
            Diag.Report("processes", ex);
            processes = new List<ProcessSample>();
        }
        _identities.Retain(processes);
        _services.RefreshIfDue();

        bool pdh = _query.Collect();
        var cpu = ReadCpu(processes, pdh);
        var memory = ReadMemory(pdh);
        var disks = Safe("disks", _disks.Read, new List<DiskState>());
        var network = Safe("network", _network.Read, new List<NetworkAdapterState>());

        List<GpuAdapterState> gpus;
        Dictionary<int, GpuProcessState> gpuByPid;
        try
        {
            _gpu.Read(out gpus, out gpuByPid);
        }
        catch (Exception ex)
        {
            Diag.Report("gpu", ex);
            gpus = new List<GpuAdapterState>();
            gpuByPid = new Dictionary<int, GpuProcessState>();
        }

        return new RawSnapshot
        {
            Timestamp = now,
            Processes = processes,
            Cpu = cpu,
            Memory = memory,
            Disks = disks,
            Network = network,
            Gpus = gpus,
            GpuByPid = gpuByPid,
        };
    }

    public AppIdentity Identify(ProcessSample process) => _identities.Get(process);

    private CpuState ReadCpu(List<ProcessSample> processes, bool pdh)
    {
        // Запасной расчёт по GetSystemTimes — если счётчики «Processor Information» недоступны.
        double fallback = 0;
        if (Native.GetSystemTimes(out long idle, out long kernel, out long user))
        {
            long dIdle = idle - _prevIdle;
            long dTotal = (kernel - _prevKernel) + (user - _prevUser);
            if (dTotal > 0) fallback = Math.Clamp((dTotal - dIdle) * 100.0 / dTotal, 0, 100);
            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;
        }

        double? utility = pdh ? _utility?.Value() : null;
        int threads = 0, handles = 0;
        foreach (var p in processes)
        {
            threads += p.Threads;
            handles += p.Handles;
        }

        return new CpuState
        {
            UtilityPercent = Math.Clamp(utility ?? fallback, 0, 100),
            PerformancePercent = pdh ? _performance?.Value() : null,
            NominalMHz = pdh ? _frequency?.Value() : null,
            ProcessCount = processes.Count,
            ThreadCount = threads,
            HandleCount = handles,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
        };
    }

    private MemoryState ReadMemory(bool pdh)
    {
        var status = new MEMORYSTATUSEX { dwLength = 64 };
        Native.GlobalMemoryStatusEx(ref status);

        long kernelPaged = 0, kernelNonPaged = 0;
        if (Native.K32GetPerformanceInfo(out var perf, System.Runtime.InteropServices.Marshal.SizeOf<PERFORMANCE_INFORMATION>()))
        {
            long page = (long)perf.PageSize;
            kernelPaged = (long)perf.KernelPaged * page;
            kernelNonPaged = (long)perf.KernelNonpaged * page;
        }

        long installed = Info.InstalledMemory > 0 ? Info.InstalledMemory : (long)status.ullTotalPhys;
        long? standby = null;
        long? modified = null;
        long? free = null;
        double? pagesInput = null;
        if (pdh)
        {
            var parts = new[] { _standbyReserve?.Value(), _standbyNormal?.Value(), _standbyCore?.Value() };
            if (parts.Any(v => v.HasValue)) standby = (long)parts.Sum(v => v ?? 0);
            double? mod = _modified?.Value();
            if (mod.HasValue) modified = (long)mod.Value;
            double? fz = _freeZero?.Value();
            if (fz.HasValue) free = (long)fz.Value;
            pagesInput = _pagesInput?.Value();
        }

        return new MemoryState
        {
            TotalPhysical = (long)status.ullTotalPhys,
            InstalledPhysical = installed,
            Available = (long)status.ullAvailPhys,
            Standby = standby,
            Modified = modified,
            Free = free,
            CommitTotal = (long)(status.ullTotalPageFile - status.ullAvailPageFile),
            CommitLimit = (long)status.ullTotalPageFile,
            KernelPaged = kernelPaged,
            KernelNonPaged = kernelNonPaged,
            HardFaultsPerSec = pagesInput,
        };
    }

    private static T Safe<T>(string where, Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception ex)
        {
            Diag.Report(where, ex);
            return fallback;
        }
    }

    public void Dispose()
    {
        _processes.Dispose();
        _query.Dispose();
        _gpu.Dispose();
    }
}
