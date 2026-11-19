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
                errors.Add(Lang.T($"Не удалось прочитать {what} (код {r.Error}).", $"Couldn't read {what} (code {r.Error})."));
            events.AddRange(r.Items);
        }

        progress?.Invoke(Lang.T("Читаю системный журнал…", "Reading the System log…"));
        Read("System", "Provider[@Name='Microsoft-Windows-Kernel-Power'] and (EventID=41)", Lang.T("записи о внезапных выключениях", "sudden shutdown records"), days, 500);
        Read("System", "Provider[@Name='EventLog'] and (EventID=6008)", Lang.T("записи о неожиданных завершениях", "unexpected shutdown records"), days, 500);
        Read("System", "Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and (EventID=1001)", Lang.T("записи о синих экранах", "blue screen records"), days, 500);
        Read("System", "Provider[@Name='Display'] and (EventID=4101)", Lang.T("записи о сбросах видеодрайвера", "graphics driver reset records"), days, 500);
        Read("System", "Provider[@Name='Microsoft-Windows-WHEA-Logger']", Lang.T("аппаратные ошибки WHEA", "WHEA hardware errors"), days, 500);
        Read("System",
            "(Provider[@Name='disk'] or Provider[@Name='Ntfs'] or Provider[@Name='Microsoft-Windows-Ntfs'] or Provider[@Name='stornvme'] " +
            "or Provider[@Name='storahci'] or Provider[@Name='iaStorAC'] or Provider[@Name='iaStorA'] or Provider[@Name='iaStorAVC'] or Provider[@Name='nvme']) " +
            "and (Level=1 or Level=2 or Level=3)",
            Lang.T("ошибки дисков", "disk errors"), days, 500);
        Read("System", "Provider[@Name='Service Control Manager'] and (EventID=7000 or EventID=7009 or EventID=7023 or EventID=7031 or EventID=7034)",
            Lang.T("сбои служб", "service failures"), days, 500);
        Read("System", "Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] and (EventID=19 or EventID=20)", Lang.T("журнал обновлений", "the update log"), days, 1000);
        Read("System", "Provider[@Name='Microsoft-Windows-Kernel-Processor-Power'] and (EventID=37)", Lang.T("ограничения частоты процессора", "CPU clock limits"), days, 500);
        Read("System", "Provider[@Name='Microsoft-Windows-MemoryDiagnostics-Results'] and (EventID=1101 or EventID=1102)", Lang.T("результаты проверки памяти", "memory test results"), 365, 20);

        progress?.Invoke(Lang.T("Читаю журнал программ…", "Reading the Application log…"));
        Read("Application", "Provider[@Name='Application Error'] and (EventID=1000)", Lang.T("падения программ", "program crashes"), days, 1000);
        Read("Application", "Provider[@Name='Application Hang'] and (EventID=1002)", Lang.T("зависания программ", "program hangs"), days, 500);
        Read("Application", "Provider[@Name='.NET Runtime'] and (EventID=1026)", Lang.T("ошибки программ .NET", ".NET program errors"), days, 300);

        progress?.Invoke(Lang.T("Читаю журнал загрузки…", "Reading the boot log…"));
        var boot = EventLogReader.Query(BootChannel,
            $"*[System[(EventID=100 or EventID=101 or EventID=102 or EventID=103 or EventID=106 or EventID=109) and {EventLogReader.LastDays(60)}]]", 300);
        if (boot.Error == EventLogReader.ERROR_ACCESS_DENIED) bootDenied = true;
        else if (boot.Error == 0)
        {
            bootRead = true;
            events.AddRange(boot.Items);
        }

        progress?.Invoke(Lang.T("Проверяю устройства…", "Checking devices…"));
        var devices = DeviceProblemsReader.Read();

        progress?.Invoke(Lang.T("Проверяю здоровье дисков…", "Checking disk health…"));
        var health = DiskHealthReader.Read();

        progress?.Invoke(Lang.T("Проверяю состояние системы…", "Checking system state…"));
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
