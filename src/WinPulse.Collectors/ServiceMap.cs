using System.Runtime.InteropServices;
using WinPulse.Collectors.Interop;
using WinPulse.Core;
using WinPulse.Core.Model;

namespace WinPulse.Collectors;

/// <summary>Какие службы работают в каком процессе (для svchost). Обновляется раз в 10 секунд.</summary>
internal sealed unsafe class ServiceMap
{
    private const int SC_MANAGER_ENUMERATE_SERVICE = 0x0004;
    private const int SC_ENUM_PROCESS_INFO = 0;
    private const int SERVICE_WIN32 = 0x30;
    private const int SERVICE_ACTIVE = 0x1;
    private const int ERROR_MORE_DATA = 234;
    private const int EntrySize = 56;

    private Dictionary<int, IReadOnlyList<ServiceEntry>> _map = new();
    private DateTime _last = DateTime.MinValue;

    public IReadOnlyDictionary<int, IReadOnlyList<ServiceEntry>> Current => _map;

    public void RefreshIfDue()
    {
        var now = DateTime.UtcNow;
        if ((now - _last).TotalSeconds < 10) return;
        _last = now;
        try
        {
            _map = Load();
        }
        catch (Exception ex)
        {
            Diag.Report("services", ex);
        }
    }

    private static Dictionary<int, IReadOnlyList<ServiceEntry>> Load()
    {
        var result = new Dictionary<int, List<ServiceEntry>>();
        IntPtr scm = Native.OpenSCManager(null, null, SC_MANAGER_ENUMERATE_SERVICE);
        if (scm == IntPtr.Zero) return new();
        int size = 128 * 1024;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            int resume = 0;
            for (int guard = 0; guard < 64; guard++)
            {
                bool ok = Native.EnumServicesStatusEx(scm, SC_ENUM_PROCESS_INFO, SERVICE_WIN32, SERVICE_ACTIVE,
                    buffer, size, out int needed, out int count, ref resume, null);
                int error = ok ? 0 : Marshal.GetLastWin32Error();
                if (!ok && error != ERROR_MORE_DATA) break;

                byte* p = (byte*)buffer;
                for (int i = 0; i < count; i++)
                {
                    byte* e = p + i * EntrySize;
                    char* name = *(char**)e;
                    char* display = *(char**)(e + 8);
                    int pid = *(int*)(e + 16 + 28);
                    if (pid == 0 || name == null) continue;
                    if (!result.TryGetValue(pid, out var list)) result[pid] = list = new List<ServiceEntry>();
                    list.Add(new ServiceEntry(new string(name), display == null ? "" : new string(display)));
                }

                if (ok) break;
                if (count == 0 && needed > size)
                {
                    Marshal.FreeHGlobal(buffer);
                    size = needed + 4096;
                    buffer = Marshal.AllocHGlobal(size);
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Native.CloseServiceHandle(scm);
        }
        return result.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<ServiceEntry>)kv.Value);
    }
}
