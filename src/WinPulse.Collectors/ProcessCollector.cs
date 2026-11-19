using System.ComponentModel;
using System.Runtime.InteropServices;
using WinPulse.Collectors.Interop;
using WinPulse.Core.Model;
using WinPulse.Core;

namespace WinPulse.Collectors;

/// <summary>
/// Все процессы одним системным вызовом NtQuerySystemInformation(SystemProcessInformation).
/// Это в десятки раз быстрее, чем System.Diagnostics.Process, и не требует прав администратора.
/// Смещения полей — для 64-битной Windows.
/// </summary>
internal sealed unsafe class ProcessCollector : IDisposable
{
    private IntPtr _buffer;
    private int _size = 1 << 20;

    public List<ProcessSample> Capture()
    {
        if (IntPtr.Size != 8) throw new PlatformNotSupportedException(Lang.T("Нужна 64-битная Windows.", "64-bit Windows is required."));

        for (int attempt = 0; ; attempt++)
        {
            if (_buffer == IntPtr.Zero) _buffer = Marshal.AllocHGlobal(_size);
            int status = Native.NtQuerySystemInformation(5, _buffer, _size, out int needed);
            if (status == 0) break;
            if (status == Native.STATUS_INFO_LENGTH_MISMATCH && attempt < 6)
            {
                Marshal.FreeHGlobal(_buffer);
                _buffer = IntPtr.Zero;
                _size = Math.Max(_size * 2, needed + 256 * 1024);
                continue;
            }
            throw new Win32Exception($"NtQuerySystemInformation: 0x{status:X8}");
        }

        var list = new List<ProcessSample>(512);
        byte* p = (byte*)_buffer;
        byte* end = p + _size;
        while (p < end)
        {
            int next = *(int*)p;
            int pid = (int)*(long*)(p + 80);
            if (pid != 0)
            {
                ushort nameBytes = *(ushort*)(p + 56);
                char* nameBuffer = *(char**)(p + 64);
                string name = nameBuffer != null && nameBytes > 0
                    ? new string(nameBuffer, 0, nameBytes / 2)
                    : pid == 4 ? "System" : $"PID {pid}";

                list.Add(new ProcessSample(
                    Pid: pid,
                    ParentPid: (int)*(long*)(p + 88),
                    Name: name,
                    CreateTime: *(long*)(p + 32),
                    CpuTime100ns: *(long*)(p + 40) + *(long*)(p + 48),
                    PrivateWorkingSet: *(long*)(p + 8),
                    WorkingSet: *(long*)(p + 144),
                    PrivateBytes: *(long*)(p + 184),
                    ReadBytes: *(long*)(p + 232),
                    WriteBytes: *(long*)(p + 240),
                    OtherBytes: *(long*)(p + 248),
                    Handles: *(int*)(p + 96),
                    Threads: *(int*)(p + 4),
                    SessionId: *(int*)(p + 100)));
            }
            if (next <= 0) break;
            p += next;
        }
        return list;
    }

    public void Dispose()
    {
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _buffer = IntPtr.Zero;
    }
}
