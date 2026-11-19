using System.Globalization;
using Microsoft.Win32;
using WinPulse.Collectors.Diagnostics;
using WinPulse.Core;
using WinPulse.Core.Drivers;

namespace WinPulse.Collectors.Drivers;

/// <summary>
/// Список драйверов подключённых устройств: имя, класс, поставщик, версия и дата драйвера.
/// Данные — из Диспетчера устройств (cfgmgr32) и раздела HKLM\SYSTEM\CurrentControlSet\Control\Class.
/// </summary>
public static class DriverInventory
{
    private const int CR_SUCCESS = 0;
    private const int CM_GETIDLIST_FILTER_PRESENT = 0x100;
    private const int CM_DRP_DEVICEDESC = 0x01;
    private const int CM_DRP_CLASS = 0x08;
    private const int CM_DRP_DRIVER = 0x0A;
    private const int CM_DRP_MFG = 0x0C;
    private const int CM_DRP_FRIENDLYNAME = 0x0D;

    private static readonly HashSet<string> Classes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Display", "Net", "MEDIA", "HDC", "SCSIAdapter", "Bluetooth", "Camera", "Image", "System",
    };

    public static List<DriverInfo> Read()
    {
        var result = new List<DriverInfo>();
        try
        {
            if (DeviceProblemsReader.CM_Get_Device_ID_List_Size(out int length, null, CM_GETIDLIST_FILTER_PRESENT) != CR_SUCCESS || length <= 0)
                return result;
            var buffer = new char[length];
            if (DeviceProblemsReader.CM_Get_Device_ID_List(null, buffer, length, CM_GETIDLIST_FILTER_PRESENT) != CR_SUCCESS)
                return result;

            using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            foreach (var id in new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    if (DeviceProblemsReader.CM_Locate_DevNode(out int inst, id, 0) != CR_SUCCESS) continue;
                    string? cls = DeviceProblemsReader.Property(inst, CM_DRP_CLASS);
                    if (cls is null || !Classes.Contains(cls)) continue;

                    DeviceProblemsReader.CM_Get_DevNode_Status(out _, out int problem, inst, 0);
                    string name = DeviceProblemsReader.Property(inst, CM_DRP_FRIENDLYNAME)
                                  ?? DeviceProblemsReader.Property(inst, CM_DRP_DEVICEDESC) ?? id;
                    string? manufacturer = DeviceProblemsReader.Property(inst, CM_DRP_MFG);
                    string? driverKey = DeviceProblemsReader.Property(inst, CM_DRP_DRIVER);

                    string? provider = null, version = null, inf = null;
                    DateTime? date = null;
                    if (driverKey is not null)
                    {
                        using var k = lm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\" + driverKey);
                        if (k is not null)
                        {
                            provider = k.GetValue("ProviderName") as string;
                            version = k.GetValue("DriverVersion") as string;
                            inf = k.GetValue("InfPath") as string;
                            date = ReadDate(k);
                        }
                    }
                    result.Add(new DriverInfo(name, cls, manufacturer, provider, version, date, inf, id, problem));
                }
                catch (Exception ex)
                {
                    Diag.Report("driver-read", ex);
                }
            }
        }
        catch (Exception ex)
        {
            Diag.Report("drivers", ex);
        }
        return result;
    }

    private static DateTime? ReadDate(RegistryKey k)
    {
        if (k.GetValue("DriverDateData") is byte[] data && data.Length >= 8)
        {
            long ft = BitConverter.ToInt64(data, 0);
            if (ft > 0)
            {
                try
                {
                    return DateTime.FromFileTimeUtc(ft).Date;
                }
                catch
                {
                }
            }
        }
        if (k.GetValue("DriverDate") is string s &&
            DateTime.TryParseExact(s.Trim(), new[] { "M-d-yyyy", "MM-dd-yyyy" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        return null;
    }
}
