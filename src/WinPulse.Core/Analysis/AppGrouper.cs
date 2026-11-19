using WinPulse.Core.Knowledge;
using WinPulse.Core.Model;

namespace WinPulse.Core.Analysis;

/// <summary>
/// Собирает процессы в приложения:
/// • обычные программы — по пути к exe (все процессы Chrome = одно приложение);
/// • svchost — по службам, которые в нём работают;
/// • помощники (WebView2, conhost) приписываются программе-родителю.
/// </summary>
public sealed class AppGrouper
{
    private static readonly HashSet<string> Helpers = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedgewebview2.exe", "conhost.exe",
    };

    private static readonly HashSet<string> NoAttachParents = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer.exe", "services.exe", "svchost.exe", "csrss.exe", "wininit.exe", "winlogon.exe",
        "sihost.exe", "RuntimeBroker.exe", "userinit.exe", "dllhost.exe", "taskhostw.exe",
        "System", "Registry", "smss.exe",
    };

    private static readonly HashSet<string> Pseudo = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Registry", "Memory Compression", "Secure System", "Idle",
    };

    private readonly string _windowsDir;

    public AppGrouper(string windowsDir)
    {
        _windowsDir = (windowsDir ?? "").TrimEnd('\\', '/');
    }

    public List<AppGroup> Build(RawSnapshot snap, IReadOnlyDictionary<int, ProcessRate> rates, ISystemSource source)
    {
        var byPid = new Dictionary<int, ProcessSample>(snap.Processes.Count);
        foreach (var p in snap.Processes) byPid.TryAdd(p.Pid, p);

        var services = source.ServicesByPid;
        var groups = new Dictionary<string, AppGroup>(StringComparer.Ordinal);
        var keyByPid = new Dictionary<int, string>(snap.Processes.Count);

        // 1. Обычные процессы.
        foreach (var p in snap.Processes)
        {
            if (p.Pid == 0) continue;
            if (Helpers.Contains(p.Name) && FindOwner(p, byPid) is not null) continue;
            var group = GetOrCreate(p, groups, services, source, out string? detail);
            keyByPid[p.Pid] = group.Key;
            AddProcess(group, p, rates, snap, detail);
        }

        // 2. Помощники — к владельцу.
        foreach (var p in snap.Processes)
        {
            if (p.Pid == 0 || keyByPid.ContainsKey(p.Pid)) continue;
            var owner = FindOwner(p, byPid);
            if (owner is not null && keyByPid.TryGetValue(owner.Pid, out var ownerKey) && groups.TryGetValue(ownerKey, out var ownerGroup))
            {
                AddProcess(ownerGroup, p, rates, snap, "помощник: " + StripExe(p.Name));
                keyByPid[p.Pid] = ownerKey;
            }
            else
            {
                var group = GetOrCreate(p, groups, services, source, out string? detail);
                keyByPid[p.Pid] = group.Key;
                AddProcess(group, p, rates, snap, detail);
            }
        }

        foreach (var g in groups.Values) Aggregate(g);
        return groups.Values.ToList();
    }

    private static ProcessSample? FindOwner(ProcessSample p, Dictionary<int, ProcessSample> byPid)
    {
        var current = p;
        for (int depth = 0; depth < 8; depth++)
        {
            if (current.ParentPid == current.Pid) return null;
            if (!byPid.TryGetValue(current.ParentPid, out var parent)) return null;
            if (parent.CreateTime > current.CreateTime) return null; // PID родителя уже занят другим процессом
            if (NoAttachParents.Contains(parent.Name)) return null;
            if (!Helpers.Contains(parent.Name)) return parent;
            current = parent;
        }
        return null;
    }

    private AppGroup GetOrCreate(
        ProcessSample p,
        Dictionary<string, AppGroup> groups,
        IReadOnlyDictionary<int, IReadOnlyList<ServiceEntry>> services,
        ISystemSource source,
        out string? detail)
    {
        detail = null;
        var known = KnownProcesses.Find(p.Name);

        if (Pseudo.Contains(p.Name))
        {
            string key = "sys:" + p.Name.ToLowerInvariant();
            if (groups.TryGetValue(key, out var existing)) return existing;
            var g = new AppGroup
            {
                Key = key,
                DisplayName = known?.DisplayName ?? p.Name,
                Publisher = "Microsoft",
                Kind = AppKind.System,
                Hint = known?.Hint,
                IsCritical = true,
            };
            groups[key] = g;
            return g;
        }

        if (p.Name.Equals("svchost.exe", StringComparison.OrdinalIgnoreCase))
        {
            string key;
            string display;
            string? hint = null;
            if (services.TryGetValue(p.Pid, out var list) && list.Count > 0)
            {
                var sorted = list.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
                key = "svc:" + sorted[0].Name.ToLowerInvariant();
                display = sorted[0].DisplayName.Length > 0 ? sorted[0].DisplayName : sorted[0].Name;
                if (sorted.Count > 1) display += $" и ещё {sorted.Count - 1}";
                detail = "службы: " + string.Join(", ", sorted.Select(s => s.Name));
                hint = "Служба Windows. Имя службы: " + string.Join(", ", sorted.Select(s => s.Name)) + ".";
            }
            else
            {
                key = "svc:pid" + p.Pid;
                display = "Служба Windows (svchost)";
            }
            if (groups.TryGetValue(key, out var existing)) return existing;
            var g = new AppGroup
            {
                Key = key,
                DisplayName = display,
                Publisher = "Microsoft",
                Kind = AppKind.Service,
                Hint = hint,
                IsCritical = true,
            };
            groups[key] = g;
            return g;
        }

        var id = source.Identify(p);
        string groupKey = id.Path is not null ? "path:" + id.Path.ToLowerInvariant() : "name:" + p.Name.ToLowerInvariant();
        if (groups.TryGetValue(groupKey, out var found)) return found;

        bool inWindows = id.Path is not null && _windowsDir.Length > 0 &&
                         id.Path.StartsWith(_windowsDir + "\\", StringComparison.OrdinalIgnoreCase);
        string name = known?.DisplayName
                      ?? (string.IsNullOrWhiteSpace(id.Description) ? StripExe(p.Name) : id.Description!.Trim());
        var group = new AppGroup
        {
            Key = groupKey,
            DisplayName = name,
            Publisher = string.IsNullOrWhiteSpace(id.Company) ? null : id.Company!.Trim(),
            Path = id.Path,
            Kind = (known?.IsSystem ?? false) || inWindows ? AppKind.System : AppKind.Application,
            Hint = known?.Hint,
            IsCritical = known?.IsSystem ?? false,
        };
        groups[groupKey] = group;
        return group;
    }

    private static void AddProcess(AppGroup g, ProcessSample p, IReadOnlyDictionary<int, ProcessRate> rates, RawSnapshot snap, string? detail)
    {
        rates.TryGetValue(p.Pid, out var rate);
        snap.GpuByPid.TryGetValue(p.Pid, out var gpu);
        g.Processes.Add(new ProcessRow
        {
            Pid = p.Pid,
            Name = p.Name,
            Detail = detail,
            CpuPercent = rate.CpuPercent,
            PrivateMemory = p.PrivateWorkingSet,
            ReadBps = rate.ReadBps,
            WriteBps = rate.WriteBps,
            GpuPercent = gpu.UtilPercent,
            GpuMemory = gpu.DedicatedBytes,
            Handles = p.Handles,
            Threads = p.Threads,
        });
    }

    private static void Aggregate(AppGroup g)
    {
        double cpu = 0, rd = 0, wr = 0, gpu = 0;
        long mem = 0, gmem = 0;
        int handles = 0, threads = 0;
        foreach (var p in g.Processes)
        {
            cpu += p.CpuPercent;
            mem += p.PrivateMemory;
            rd += p.ReadBps;
            wr += p.WriteBps;
            gpu += p.GpuPercent;
            gmem += p.GpuMemory;
            handles += p.Handles;
            threads += p.Threads;
        }
        g.CpuPercent = Math.Min(100, cpu);
        g.PrivateMemory = mem;
        g.ReadBps = rd;
        g.WriteBps = wr;
        g.GpuPercent = Math.Min(100, gpu);
        g.GpuMemory = gmem;
        g.Handles = handles;
        g.Threads = threads;
    }

    public static string StripExe(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
}
