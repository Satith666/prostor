using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using WinPulse.Core;
using WinPulse.Core.Diagnostics;

namespace WinPulse.Collectors.Diagnostics;

/// <summary>Устройства с ошибками — то же, что жёлтые значки в Диспетчере устройств (cfgmgr32).</summary>
public static class DeviceProblemsReader
{
    private const int CR_SUCCESS = 0;
    private const int CM_GETIDLIST_FILTER_PRESENT = 0x100;
    private const int DN_HAS_PROBLEM = 0x400;
    private const int CM_DRP_DEVICEDESC = 0x01;
    private const int CM_DRP_CLASS = 0x08;
    private const int CM_DRP_FRIENDLYNAME = 0x0D;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_ID_List_SizeW")]
    internal static extern int CM_Get_Device_ID_List_Size(out int length, string? filter, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_Device_ID_ListW")]
    internal static extern int CM_Get_Device_ID_List(string? filter, [Out] char[] buffer, int length, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Locate_DevNodeW")]
    internal static extern int CM_Locate_DevNode(out int devInst, string deviceId, int flags);

    [DllImport("cfgmgr32.dll")]
    internal static extern int CM_Get_DevNode_Status(out int status, out int problem, int devInst, int flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, EntryPoint = "CM_Get_DevNode_Registry_PropertyW")]
    internal static extern int CM_Get_DevNode_Registry_Property(int devInst, int property, out int regType, [Out] byte[]? buffer, ref int length, int flags);

    /// <summary>null — не удалось прочитать список устройств.</summary>
    public static List<DeviceProblem>? Read()
    {
        try
        {
            if (CM_Get_Device_ID_List_Size(out int length, null, CM_GETIDLIST_FILTER_PRESENT) != CR_SUCCESS || length <= 0) return null;
            var buffer = new char[length];
            if (CM_Get_Device_ID_List(null, buffer, length, CM_GETIDLIST_FILTER_PRESENT) != CR_SUCCESS) return null;
            var ids = new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries);

            var result = new List<DeviceProblem>();
            foreach (var id in ids)
            {
                if (CM_Locate_DevNode(out int inst, id, 0) != CR_SUCCESS) continue;
                if (CM_Get_DevNode_Status(out int status, out int problem, inst, 0) != CR_SUCCESS) continue;
                if ((status & DN_HAS_PROBLEM) == 0 || problem == 0) continue;
                string name = Property(inst, CM_DRP_FRIENDLYNAME) ?? Property(inst, CM_DRP_DEVICEDESC) ?? id;
                result.Add(new DeviceProblem(name, id, Property(inst, CM_DRP_CLASS), problem));
            }
            return result;
        }
        catch (Exception ex)
        {
            Diag.Report("devices", ex);
            return null;
        }
    }

    internal static string? Property(int inst, int property)
    {
        int length = 0;
        CM_Get_DevNode_Registry_Property(inst, property, out _, null, ref length, 0);
        if (length <= 0) return null;
        var buffer = new byte[length];
        if (CM_Get_DevNode_Registry_Property(inst, property, out _, buffer, ref length, 0) != CR_SUCCESS) return null;
        string s = Encoding.Unicode.GetString(buffer, 0, Math.Min(length, buffer.Length)).TrimEnd('\0').Trim();
        return s.Length > 0 ? s : null;
    }
}

/// <summary>Здоровье дисков через PowerShell (Get-PhysicalDisk; износ и температура — Get-StorageReliabilityCounter, нужны права администратора).</summary>
public static class DiskHealthReader
{
    private const string Script = """
        $ErrorActionPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
        $r = foreach ($d in Get-PhysicalDisk) {
            $c = $null
            try { $c = $d | Get-StorageReliabilityCounter -ErrorAction Stop } catch { }
            [pscustomobject]@{
                Name = [string]$d.FriendlyName
                Media = [string]$d.MediaType
                Bus = [string]$d.BusType
                Health = [string]$d.HealthStatus
                Size = [int64]$d.Size
                Wear = $c.Wear
                Temp = $c.Temperature
                TempMax = $c.TemperatureMax
                ReadErr = $c.ReadErrorsUncorrected
                Hours = $c.PowerOnHours
            }
        }
        ConvertTo-Json -InputObject @($r) -Compress
        """;

    /// <summary>null — PowerShell недоступен или не ответил за 40 секунд.</summary>
    public static List<DiskHealthInfo>? Read()
    {
        try
        {
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Script));
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"),
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            var outputTask = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(40_000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            string json = outputTask.GetAwaiter().GetResult().Trim();
            return Parse(json);
        }
        catch (Exception ex)
        {
            Diag.Report("disk-health", ex);
            return null;
        }
    }

    /// <summary>Разбор вывода ConvertTo-Json. Открыт для тестов.</summary>
    public static List<DiskHealthInfo>? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        int start = json.IndexOf('[');
        if (start < 0) return null;
        using var doc = JsonDocument.Parse(json[start..]);
        var list = new List<DiskHealthInfo>();
        foreach (var e in doc.RootElement.EnumerateArray())
        {
            if (e.ValueKind != JsonValueKind.Object) continue;
            list.Add(new DiskHealthInfo(
                Str(e, "Name") ?? Lang.T("Диск", "Disk"),
                MediaName(Str(e, "Media")),
                BusName(Str(e, "Bus")),
                Str(e, "Health") ?? "",
                Long(e, "Size") ?? 0,
                (int?)Long(e, "Wear"),
                (int?)Long(e, "Temp"),
                (int?)Long(e, "TempMax"),
                Long(e, "ReadErr"),
                Long(e, "Hours")));
        }
        return list;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static long? Long(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l)) return l;
        if (v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out l)) return l;
        return null;
    }

    private static string MediaName(string? s) => s switch
    {
        "3" => "HDD",
        "4" => "SSD",
        "5" => "SCM",
        "0" or null or "" => "",
        "Unspecified" => "",
        _ => s,
    };

    private static string BusName(string? s) => s switch
    {
        "17" => "NVMe",
        "11" => "SATA",
        "7" => "USB",
        "3" => "ATA",
        "10" => "SAS",
        "8" => "RAID",
        null => "",
        _ => s,
    };
}

/// <summary>Мелкие проверки состояния системы.</summary>
public static class SystemChecks
{
    /// <summary>Почему Windows ждёт перезагрузки (пусто — не ждёт).</summary>
    public static List<string> PendingReboot()
    {
        var reasons = new List<string>();
        try
        {
            using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using (var k = lm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending"))
                if (k is not null) reasons.Add(Lang.T("Установка компонентов Windows ждёт перезагрузки", "Windows component installation is waiting for a restart"));
            using (var k = lm.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired"))
                if (k is not null) reasons.Add(Lang.T("Обновления Windows ждут перезагрузки", "Windows updates are waiting for a restart"));
            using (var k = lm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager"))
            {
                if (k?.GetValue("PendingFileRenameOperations") is string[] ops && ops.Any(o => o.Length > 0))
                    reasons.Add(Lang.T("Программы ждут перезагрузки, чтобы заменить свои файлы", "Programs are waiting for a restart to replace their files"));
            }
        }
        catch (Exception ex)
        {
            Diag.Report("pending-reboot", ex);
        }
        return reasons;
    }

    /// <summary>true — файл подкачки отключён в настройках; null — не удалось прочитать.</summary>
    public static bool? PageFileDisabled()
    {
        try
        {
            using var lm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var k = lm.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management");
            var value = k?.GetValue("PagingFiles");
            if (value is string[] files) return !files.Any(f => !string.IsNullOrWhiteSpace(f));
            if (value is string single) return string.IsNullOrWhiteSpace(single);
            return value is null ? true : null;
        }
        catch
        {
            return null;
        }
    }
}
