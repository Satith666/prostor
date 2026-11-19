using System.Runtime.InteropServices;
using WinPulse.Collectors.Interop;

namespace WinPulse.Collectors;

/// <summary>Состояние защит Windows, влияющих на производительность.</summary>
public static unsafe class SecurityInfo
{
    private const int SystemCodeIntegrityInformation = 103;
    private const uint CODEINTEGRITY_OPTION_HVCI_KMCI_ENABLED = 0x400;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_CODEINTEGRITY_INFORMATION
    {
        public uint Length;
        public uint CodeIntegrityOptions;
    }

    /// <summary>Работает ли сейчас «Целостность памяти» (HVCI). null — не удалось узнать.</summary>
    public static bool? HvciRunning()
    {
        try
        {
            var info = new SYSTEM_CODEINTEGRITY_INFORMATION { Length = 8 };
            int status = Native.NtQuerySystemInformation(SystemCodeIntegrityInformation, (IntPtr)(&info), 8, out _);
            if (status != 0) return null;
            return (info.CodeIntegrityOptions & CODEINTEGRITY_OPTION_HVCI_KMCI_ENABLED) != 0;
        }
        catch
        {
            return null;
        }
    }
}
