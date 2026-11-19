using Microsoft.Win32;
using WinPulse.Core;
using WinPulse.Core.Diagnostics;

namespace WinPulse.Collectors.Diagnostics;

/// <summary>
/// Режим прерываний PCI-устройств (MSI или линии IRQ) — только чтение, как в MSI mode utility.
/// MSISupported = 1 в «Device Parameters\Interrupt Management\MessageSignaledInterruptProperties».
/// </summary>
public static class InterruptReader
{
    private const int CR_SUCCESS = 0;
    private const int CM_GETIDLIST_FILTER_ENUMERATOR = 0x1;
    private const int CM_GETIDLIST_FILTER_PRESENT = 0x100;
    private const int CM_DRP_DEVICEDESC = 0x01;
    private const int CM_DRP_CLASS = 0x08;
    private const int CM_DRP_FRIENDLYNAME = 0x0D;

    /// <summary>null — список устройств не прочитан.</summary>
    public static List<DeviceInterrupt>? Read()
    {
        try
        {
            int flags = CM_GETIDLIST_FILTER_ENUMERATOR | CM_GETIDLIST_FILTER_PRESENT;
            if (DeviceProblemsReader.CM_Get_Device_ID_List_Size(out int length, "PCI", flags) != CR_SUCCESS || length <= 0) return null;
            var buffer = new char[length];
            if (DeviceProblemsReader.CM_Get_Device_ID_List("PCI", buffer, length, flags) != CR_SUCCESS) return null;

            var result = new List<DeviceInterrupt>();
            foreach (var id in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                if (DeviceProblemsReader.CM_Locate_DevNode(out int inst, id, 0) != CR_SUCCESS) continue;
                string? cls = DeviceProblemsReader.Property(inst, CM_DRP_CLASS);
                if (!DeviceInterrupt.Interesting(cls)) continue;
                string name = DeviceProblemsReader.Property(inst, CM_DRP_FRIENDLYNAME) ?? DeviceProblemsReader.Property(inst, CM_DRP_DEVICEDESC) ?? id;

                bool msi = false;
                int? limit = null;
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(
                        $@"SYSTEM\CurrentControlSet\Enum\{id}\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties");
                    if (key?.GetValue("MSISupported") is int supported) msi = supported == 1;
                    if (key?.GetValue("MessageNumberLimit") is int n) limit = n;
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
                {
                    // Без доступа считаем, что MSI не настроен — подсказка на странице объясняет.
                }
                result.Add(new DeviceInterrupt(name, cls!, id, msi, limit));
            }
            return result.OrderBy(d => d.Order).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        catch (Exception ex)
        {
            Diag.Report("interrupts", ex);
            return null;
        }
    }
}
