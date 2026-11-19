using WinPulse.Collectors.Interop;
using WinPulse.Core.Model;

namespace WinPulse.Collectors.Gpu;

/// <summary>
/// Загрузка видеокарт и видеопамять — те же счётчики, что в Диспетчере задач:
/// GPU Engine(*)\Utilization Percentage, GPU Adapter Memory(*)\Dedicated Usage, GPU Process Memory(*)\Dedicated Usage.
/// </summary>
internal sealed class GpuCollector : IDisposable
{
    private readonly PdhQuery _query = new();
    private readonly PdhCounter? _engine;
    private readonly PdhCounter? _adapterMemory;
    private readonly PdhCounter? _processMemory;
    private List<GpuAdapterInfo> _adapters = new();
    private DateTime _adaptersTime = DateTime.MinValue;

    public GpuCollector()
    {
        _engine = _query.Add(@"\GPU Engine(*)\Utilization Percentage");
        _adapterMemory = _query.Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
        _processMemory = _query.Add(@"\GPU Process Memory(*)\Dedicated Usage");
        _query.Collect();
    }

    public IReadOnlyList<GpuAdapterInfo> Adapters => _adapters;

    public void Read(out List<GpuAdapterState> adapters, out Dictionary<int, GpuProcessState> byPid)
    {
        if ((DateTime.UtcNow - _adaptersTime).TotalSeconds > 60)
        {
            _adaptersTime = DateTime.UtcNow;
            _adapters = Dxgi.Enumerate().Where(a => !a.IsSoftware).ToList();
        }

        adapters = new List<GpuAdapterState>();
        byPid = new Dictionary<int, GpuProcessState>();
        if (!_query.Collect()) return;

        // (luid, тип движка) → сумма; (pid, тип движка) → сумма
        var adapterEngines = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
        var processEngines = new Dictionary<int, Dictionary<string, double>>();
        if (_engine is not null)
        {
            foreach (var kv in _engine.Values())
            {
                if (!TryParseEngine(kv.Key, out int pid, out string luid, out string engine)) continue;
                if (!adapterEngines.TryGetValue(luid, out var ae)) adapterEngines[luid] = ae = new(StringComparer.OrdinalIgnoreCase);
                ae[engine] = ae.GetValueOrDefault(engine) + kv.Value;
                if (!processEngines.TryGetValue(pid, out var pe)) processEngines[pid] = pe = new(StringComparer.OrdinalIgnoreCase);
                pe[engine] = pe.GetValueOrDefault(engine) + kv.Value;
            }
        }

        var adapterMem = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (_adapterMemory is not null)
        {
            foreach (var kv in _adapterMemory.Values())
            {
                string? luid = LuidAfter(kv.Key, "luid_");
                if (luid is null) continue;
                adapterMem[luid] = adapterMem.GetValueOrDefault(luid) + kv.Value;
            }
        }

        var processMem = new Dictionary<int, double>();
        if (_processMemory is not null)
        {
            foreach (var kv in _processMemory.Values())
            {
                if (!TryParsePid(kv.Key, out int pid)) continue;
                processMem[pid] = processMem.GetValueOrDefault(pid) + kv.Value;
            }
        }

        foreach (var a in _adapters)
        {
            string luid = a.Luid;
            adapterEngines.TryGetValue(luid, out var engines);
            var engineUtil = engines?.ToDictionary(kv => kv.Key, kv => Math.Min(100, kv.Value), StringComparer.OrdinalIgnoreCase)
                             ?? new Dictionary<string, double>();
            adapters.Add(new GpuAdapterState
            {
                Luid = luid,
                Name = a.Name,
                VendorId = a.VendorId,
                DedicatedTotal = a.DedicatedMemory,
                DedicatedUsed = (long)adapterMem.GetValueOrDefault(luid),
                UtilPercent = engineUtil.Count == 0 ? 0 : engineUtil.Values.Max(),
                EngineUtil = engineUtil,
            });
        }

        foreach (var kv in processEngines)
        {
            double util = kv.Value.Count == 0 ? 0 : Math.Min(100, kv.Value.Values.Max());
            byPid[kv.Key] = new GpuProcessState(util, (long)processMem.GetValueOrDefault(kv.Key));
        }
        foreach (var kv in processMem)
        {
            if (!byPid.ContainsKey(kv.Key)) byPid[kv.Key] = new GpuProcessState(0, (long)kv.Value);
        }
    }

    /// <summary>pid_1234_luid_0x00000000_0x0000D1A2_phys_0_eng_0_engtype_3D</summary>
    internal static bool TryParseEngine(string instance, out int pid, out string luid, out string engine)
    {
        luid = "";
        engine = "";
        if (!TryParsePid(instance, out pid)) return false;
        var l = LuidAfter(instance, "luid_");
        if (l is null) return false;
        luid = l;
        int e = instance.IndexOf("engtype_", StringComparison.Ordinal);
        engine = e < 0 ? "" : instance[(e + 8)..];
        if (engine.Length == 0) engine = "Other";
        return true;
    }

    internal static bool TryParsePid(string instance, out int pid)
    {
        pid = 0;
        if (!instance.StartsWith("pid_", StringComparison.Ordinal)) return false;
        int end = instance.IndexOf('_', 4);
        if (end < 0) return false;
        return int.TryParse(instance.AsSpan(4, end - 4), out pid);
    }

    internal static string? LuidAfter(string instance, string marker)
    {
        int i = instance.IndexOf(marker, StringComparison.Ordinal);
        if (i < 0 || i + marker.Length + 21 > instance.Length) return null;
        return instance.Substring(i + marker.Length, 21).ToLowerInvariant();
    }

    public void Dispose() => _query.Dispose();
}
