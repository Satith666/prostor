using System.Diagnostics;
using WinPulse.Collectors.Interop;
using WinPulse.Core.Model;

namespace WinPulse.Collectors;

public static unsafe class ProcessPaths
{
    /// <summary>Полный путь к exe процесса. null — нет доступа (защищённый процесс) или процесс уже завершился.</summary>
    public static string? TryGet(int pid)
    {
        IntPtr h = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            const int capacity = 1024;
            char* buffer = stackalloc char[capacity];
            int size = capacity;
            return Native.QueryFullProcessImageName(h, 0, buffer, ref size) && size > 0
                ? new string(buffer, 0, size)
                : null;
        }
        finally
        {
            Native.CloseHandle(h);
        }
    }

    /// <summary>Описание и издатель из ресурсов exe.</summary>
    public static (string? Description, string? Company) VersionInfo(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            return (Clean(info.FileDescription), Clean(info.CompanyName));
        }
        catch
        {
            return (null, null);
        }
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>Кэш «процесс → путь, описание, издатель». Ключ — (PID, время создания).</summary>
internal sealed class IdentityCache
{
    private readonly Dictionary<(int Pid, long Create), AppIdentity> _byProcess = new();
    private readonly Dictionary<string, (string? Description, string? Company)> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public AppIdentity Get(ProcessSample p)
    {
        if (_byProcess.TryGetValue((p.Pid, p.CreateTime), out var cached)) return cached;
        string? path = ProcessPaths.TryGet(p.Pid);
        string? description = null;
        string? company = null;
        if (path is not null)
        {
            if (!_byPath.TryGetValue(path, out var vi))
            {
                vi = ProcessPaths.VersionInfo(path);
                _byPath[path] = vi;
            }
            (description, company) = vi;
        }
        var id = new AppIdentity(path, description, company);
        _byProcess[(p.Pid, p.CreateTime)] = id;
        return id;
    }

    /// <summary>Убирает из кэша завершившиеся процессы.</summary>
    public void Retain(IReadOnlyList<ProcessSample> alive)
    {
        if (_byProcess.Count < alive.Count * 2 + 64) return;
        var keep = new HashSet<(int, long)>(alive.Select(a => (a.Pid, a.CreateTime)));
        foreach (var key in _byProcess.Keys.Where(k => !keep.Contains(k)).ToList()) _byProcess.Remove(key);
        if (_byPath.Count > 2000) _byPath.Clear();
    }
}
