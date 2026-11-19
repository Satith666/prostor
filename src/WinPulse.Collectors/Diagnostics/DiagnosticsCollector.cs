using WinPulse.Collectors.Interop;
using WinPulse.Core;
using WinPulse.Core.Diagnostics;
using WinPulse.Core.Model;

namespace WinPulse.Collectors.Diagnostics;

/// <summary>Собирает всё для раздела «Проблемы»: журналы, устройства, диски, состояние системы. Работает 2–15 секунд — вызывать в фоне.</summary>
public static class DiagnosticsCollector
{
    private const string BootChannel = "Microsoft-Windows-Diagnostics-Performance/Operational";

    public static DiagnosticInput Collect(int days = 30, Action<string>? progress = null)
    {
        var errors = new List<string>();
        var events = new List<EventItem>();
        bool bootRead = false, bootDenied = false;

        void Read(string channel, string filter, string what, int periodDays, int max)
        {
            string xpath = $"*[System[{filter} and {EventLogReader.LastDays(periodDays)}]]";
            var r = EventLogReader.Query(channel, xpath, max);
            if (r.Error != 0 && r.Error != EventLogReader.ERROR_EVT_CHANNEL_NOT_FOUND)
                errors.Add($"Не удалось прочитать {what} (код {r.Error}).");
            events.AddRange(r.Items);
        }

        progress?.Invoke("Читаю системный журнал…");
        Read("System", "Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41)", "записи о внезапных выключениях", days, 500);
        Read("System", "Provider[@Name='EventLog'] and (EventID=6008)", "записи о неожиданных завершениях", days, 500);
        Read("System", "Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and (EventID=1001)", "записи о синих экранах", days, 500);
        Read("System", "Provider[@Name='Display'] and (EventID=4101)", "записи о сбросах видеодрайвера", days, 500);
        Read("System", "Provider[@Name='Microsoft-Windows-WHEA-Logger']", "аппаратные ошибки WHEA", days, 500);
        Read("System",
            "(Provider[@Name='disk'] or Provider[@Name='Ntfs'] or Provider[@Name='Microsoft-Windows-Ntfs'] or Provider[@Name='stornvme'] " +
            "or Provider[@Name='storahci'] or Provider[@Name='iaStorAC'] or Provider[@Name='iaStorA'] or Provider[@Name='iaStorAVC'] or Provider[@Name='nvme']) " +
            "and (Level=1 or Level=2 or Level=3)",
            "ошибки дисков", days, 500);
        Read("System", "Provider[@Name='Service Control Manager'] and (EventID=7000 or EventID=7009 or EventID=7023 or EventID=7031 or EventID=7034)",
            "сбои служб", days, 500);
        Read("System", "Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and (EventID=19 or EventID=20)", "журнал обновлений", days, 1000);
        Read("System", "Provider[@Name='Microsoft-Windows-Kernel-Processor-Power'] and (EventID=37)", "ограничения частоты процессора", days, 500);
        Read("System", "Provider[@Name='Microsoft-Windows-MemoryDiagnostics-Results'] and (EventID=1101 or EventID=1102)", "результаты проверки памяти", 365, 20);

        progress?.Invoke("Читаю журнал программ…");
        Read("Application", "Provider[@Name='Application Error'] and (EventID=1000)", "падения программ", days, 1000);
        Read("Application", "Provider[@Name='Application Hang'] and (EventID=1002)", "зависания программ", days, 500);
        Read("Application", "Provider[@Name='.NET Runtime'] and (EventID=1026)", "ошибки программ .NET", days, 300);

        progress?.Invoke("Читаю журнал загрузки…");
        var boot = EventLogReader.Query(BootChannel,
            $"*[System[(EventID=100 or EventID=101 or EventID=102 or EventID=103 or EventID=106 or EventID=109) and {EventLogReader.LastDays(60)}]]", 300);
        if (boot.Error == EventLogReader.ERROR_ACCESS_DENIED) bootDenied = true;
        else if (boot.Error == 0)
        {
            bootRead = true;
            events.AddRange(boot.Items);
        }

        progress?.Invoke("Проверяю устройства…");
        var devices = DeviceProblemsReader.Read();

        progress?.Invoke("Проверяю здоровье дисков…");
        var health = DiskHealthReader.Read();

        progress?.Invoke("Проверяю состояние системы…");
        var status = new MEMORYSTATUSEX { dwLength = 64 };
        Native.GlobalMemoryStatusEx(ref status);

        return new DiagnosticInput
        {
            Now = DateTime.Now,
            PeriodDays = days,
            Events = events,
            BootLogRead = bootRead,
            BootLogDenied = bootDenied,
            DevicesChecked = devices is not null,
            Devices = devices ?? new List<DeviceProblem>(),
            DiskHealthChecked = health is not null,
            DiskHealth = health ?? new List<DiskHealthInfo>(),
            DiskNames = DiskNames(),
            PendingReboot = SystemChecks.PendingReboot(),
            Volumes = Volumes(),
            SystemDrive = (Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\").TrimEnd('\\'),
            PageFileDisabled = SystemChecks.PageFileDisabled(),
            TotalPhysical = (long)status.ullTotalPhys,
            CommitLimit = (long)status.ullTotalPageFile,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64),
            IsElevated = OsInfo.IsElevated(),
            Errors = errors,
        };
    }

    private static Dictionary<int, string> DiskNames()
    {
        var names = new Dictionary<int, string>();
        for (int i = 0; i < 16; i++)
        {
            try
            {
                using var h = Native.CreateFile($@"\\.\PhysicalDrive{i}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                if (h.IsInvalid) continue;
            }
            catch
            {
                continue;
            }
            names[i] = DiskCollector.QueryStatic(i).Model;
        }
        return names;
    }

    private static List<VolumeState> Volumes()
    {
        var list = new List<VolumeState>();
        try
        {
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                    string letter = d.Name.TrimEnd('\\');
                    list.Add(new VolumeState(letter, string.IsNullOrWhiteSpace(d.VolumeLabel) ? null : d.VolumeLabel, d.TotalSize, d.TotalFreeSpace));
                }
                catch
                {
                    // диск занят или недоступен
                }
            }
        }
        catch (Exception ex)
        {
            Diag.Report("volumes", ex);
        }
        return list;
    }
}
