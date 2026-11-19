namespace WinPulse.Core.Startup;

public enum StartupSource
{
    /// <summary>HKCU\...\Run</summary>
    UserRun,
    /// <summary>HKLM\...\Run (64-бит)</summary>
    MachineRun,
    /// <summary>HKLM\SOFTWARE\WOW6432Node\...\Run (32-бит)</summary>
    MachineRun32,
    /// <summary>Папка «Автозагрузка» пользователя</summary>
    UserFolder,
    /// <summary>Общая папка «Автозагрузка»</summary>
    CommonFolder,
    /// <summary>Задание Планировщика с запуском при входе или загрузке</summary>
    Task,
}

/// <summary>Одна программа, которая запускается вместе с Windows.</summary>
public sealed class StartupEntry
{
    public string Id { get; init; } = "";
    public StartupSource Source { get; init; }
    /// <summary>Имя значения в реестре, имя файла в папке или имя задания.</summary>
    public string Name { get; init; } = "";
    public string Command { get; init; } = "";
    public string? ExePath { get; init; }
    public string? Description { get; init; }
    public string? Publisher { get; init; }
    public bool Enabled { get; init; }
    /// <summary>Для заданий — полный путь в Планировщике.</summary>
    public string? TaskPath { get; init; }
    /// <summary>Для задания — при входе или при загрузке.</summary>
    public string? TriggerText { get; init; }

    public bool NeedsAdmin => Source is StartupSource.MachineRun or StartupSource.MachineRun32 or StartupSource.CommonFolder or StartupSource.Task;

    public string DisplayName => !string.IsNullOrWhiteSpace(Description) ? Description! : Name;

    public string SourceText => Source switch
    {
        StartupSource.UserRun => Lang.T("Реестр (текущий пользователь)", "Registry (current user)"),
        StartupSource.MachineRun => Lang.T("Реестр (все пользователи)", "Registry (all users)"),
        StartupSource.MachineRun32 => Lang.T("Реестр (все пользователи, 32-бит)", "Registry (all users, 32-bit)"),
        StartupSource.UserFolder => Lang.T("Папка «Автозагрузка»", "Startup folder"),
        StartupSource.CommonFolder => Lang.T("Общая папка «Автозагрузка»", "Common Startup folder"),
        StartupSource.Task => Lang.T("Планировщик заданий", "Task Scheduler"),
        _ => "",
    };
}

/// <summary>
/// Флаг «включено/отключено» в разделе StartupApproved — тот же, что ставит Диспетчер задач.
/// Первый байт: чётный — включено (02, 06), нечётный — отключено (03, 07). Затем 8 байт FILETIME момента отключения.
/// Если значения нет — программа включена.
/// </summary>
public static class StartupApproved
{
    public const string UserRun = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string MachineRun = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    public const string MachineRun32 = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run32";
    public const string UserFolder = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";
    public const string MachineFolder = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder";

    public static bool IsEnabled(byte[]? data) => data is null || data.Length == 0 || (data[0] & 1) == 0;

    public static byte[] Disabled(DateTime utcNow)
    {
        var bytes = new byte[12];
        bytes[0] = 0x03;
        BitConverter.GetBytes(utcNow.ToFileTimeUtc()).CopyTo(bytes, 4);
        return bytes;
    }

    public static byte[] Enabled() => new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };

    /// <summary>Раздел StartupApproved для источника (null — для заданий).</summary>
    public static (bool Machine, string Key)? KeyFor(StartupSource source) => source switch
    {
        StartupSource.UserRun => (false, UserRun),
        StartupSource.MachineRun => (true, MachineRun),
        StartupSource.MachineRun32 => (true, MachineRun32),
        StartupSource.UserFolder => (false, UserFolder),
        StartupSource.CommonFolder => (true, MachineFolder),
        _ => null,
    };
}

public static class CommandLine
{
    /// <summary>
    /// Путь к исполняемому файлу из командной строки автозагрузки:
    /// «"C:\Program Files\X\x.exe" --min» → C:\Program Files\X\x.exe; «C:\X\x.exe /bg» → C:\X\x.exe;
    /// «rundll32.exe C:\X\y.dll,Entry» → C:\X\y.dll.
    /// </summary>
    public static string? ExtractExe(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string c = Environment.ExpandEnvironmentVariables(command.Trim());

        string first;
        string rest;
        if (c.StartsWith('"'))
        {
            int end = c.IndexOf('"', 1);
            first = end > 0 ? c[1..end] : c[1..];
            rest = end > 0 && end + 1 < c.Length ? c[(end + 1)..].Trim() : "";
        }
        else
        {
            int exe = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            if (exe > 0 && (exe + 4 == c.Length || c[exe + 4] is ' ' or '\t' or '"'))
            {
                first = c[..(exe + 4)];
                rest = c[(exe + 4)..].Trim();
            }
            else
            {
                int space = c.IndexOf(' ');
                first = space > 0 ? c[..space] : c;
                rest = space > 0 ? c[(space + 1)..].Trim() : "";
            }
        }

        // rundll32 — интересна библиотека, которую он запускает.
        string fileName = first.Replace('/', '\\');
        int slash = fileName.LastIndexOf('\\');
        string shortName = slash >= 0 ? fileName[(slash + 1)..] : fileName;
        if (shortName.Equals("rundll32.exe", StringComparison.OrdinalIgnoreCase) || shortName.Equals("rundll32", StringComparison.OrdinalIgnoreCase))
        {
            string arg = rest.Trim().Trim('"');
            int comma = arg.IndexOf(',');
            if (comma > 0) arg = arg[..comma];
            arg = arg.Trim().Trim('"');
            return arg.Length > 0 ? arg : first;
        }
        return first.Trim();
    }
}
