using System.Runtime.InteropServices;
using Microsoft.Win32;
using WinPulse.Collectors;
using WinPulse.Core;
using WinPulse.Core.Startup;
using WinPulse.Optimizer.Changes;
using WinPulse.Optimizer.Platform;
using WinPulse.Optimizer.WindowedGames;

namespace WinPulse.Optimizer.Startup;

/// <summary>
/// Автозагрузка: реестр (Run), папки «Автозагрузка», задания Планировщика с запуском при входе/загрузке.
/// Отключение — как в Диспетчере задач (флаг в StartupApproved) или выключение задания. Записи не удаляются.
/// </summary>
public sealed class StartupManager
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Run32Key = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";

    public StartupManager(string? backupPath = null)
    {
        Module = new FixModule("startup", Array.Empty<FixItem>(), backupPath);
    }

    /// <summary>Резервная копия и применение изменений (у модуля нет пунктов — только откат).</summary>
    public FixModule Module { get; }

    public List<StartupEntry> Scan()
    {
        var list = new List<StartupEntry>();
        Safe(() => ReadRun(list, RegHive.CurrentUser, RunKey, StartupSource.UserRun));
        Safe(() => ReadRun(list, RegHive.LocalMachine, RunKey, StartupSource.MachineRun));
        Safe(() => ReadRun(list, RegHive.LocalMachine, Run32Key, StartupSource.MachineRun32));
        Safe(() => ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), StartupSource.UserFolder));
        Safe(() => ReadFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), StartupSource.CommonFolder));
        Safe(() => ReadTasks(list));
        return list;
    }

    private static void Safe(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            Diag.Report("startup-scan", ex);
        }
    }

    private static byte[]? Approved(StartupSource source, string name)
    {
        var key = StartupApproved.KeyFor(source);
        if (key is null) return null;
        using var root = Reg.Base(key.Value.Machine ? RegHive.LocalMachine : RegHive.CurrentUser);
        using var k = root.OpenSubKey(key.Value.Key);
        return k?.GetValue(name) as byte[];
    }

    private static void ReadRun(List<StartupEntry> list, RegHive hive, string keyPath, StartupSource source)
    {
        using var root = Reg.Base(hive);
        using var key = root.OpenSubKey(keyPath);
        if (key is null) return;
        foreach (var name in key.GetValueNames())
        {
            if (string.IsNullOrEmpty(name)) continue;
            string command = key.GetValue(name, "", RegistryValueOptions.DoNotExpandEnvironmentNames)?.ToString() ?? "";
            if (command.Length == 0) continue;
            list.Add(Make(source, name, command, CommandLine.ExtractExe(command), StartupApproved.IsEnabled(Approved(source, name))));
        }
    }

    private static void ReadFolder(List<StartupEntry> list, string folder, StartupSource source)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            string name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;
            string? exe = file;
            string command = file;
            if (name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var (target, args) = ResolveShortcut(file);
                if (!string.IsNullOrWhiteSpace(target))
                {
                    exe = target;
                    command = string.IsNullOrWhiteSpace(args) ? target : $"\"{target}\" {args}";
                }
            }
            list.Add(Make(source, name, command, exe, StartupApproved.IsEnabled(Approved(source, name))));
        }
    }

    private static void ReadTasks(List<StartupEntry> list)
    {
        foreach (var t in ScheduledTasks.StartupTasks())
        {
            string command = t.ExePath is null ? "" : string.IsNullOrWhiteSpace(t.Arguments) ? t.ExePath : $"\"{t.ExePath}\" {t.Arguments}";
            var entry = Make(StartupSource.Task, t.Name, command, t.ExePath, t.Enabled, t.Path,
                t.AtLogon && t.AtBoot ? Lang.T("при загрузке и входе", "at boot and logon")
                : t.AtBoot ? Lang.T("при загрузке Windows", "at boot") : Lang.T("при входе в систему", "at logon"),
                t.Description);
            list.Add(entry);
        }
    }

    private static StartupEntry Make(StartupSource source, string name, string command, string? exe, bool enabled,
        string? taskPath = null, string? trigger = null, string? fallbackDescription = null)
    {
        string? description = null, publisher = null;
        if (exe is not null)
        {
            exe = Environment.ExpandEnvironmentVariables(exe);
            if (File.Exists(exe))
            {
                var vi = ProcessPaths.VersionInfo(exe);
                description = vi.Description;
                publisher = vi.Company;
            }
        }
        return new StartupEntry
        {
            Id = source + "|" + (taskPath ?? name),
            Source = source,
            Name = name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name,
            Command = command,
            ExePath = exe,
            Description = description ?? fallbackDescription,
            Publisher = publisher,
            Enabled = enabled,
            TaskPath = taskPath,
            TriggerText = trigger,
        };
    }

    /// <summary>Цель и ссылка ярлыка .lnk через WScript.Shell.</summary>
    private static (string? Target, string? Arguments) ResolveShortcut(string path)
    {
        object? shell = null, link = null;
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return (null, null);
            shell = Activator.CreateInstance(type);
            dynamic d = shell!;
            link = d.CreateShortcut(path);
            dynamic l = link!;
            return ((string?)l.TargetPath, (string?)l.Arguments);
        }
        catch
        {
            return (null, null);
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    /// <summary>План включения/отключения записи. Значение в реестре автозагрузки не удаляется.</summary>
    public static Change PlanToggle(StartupEntry e, bool enable)
    {
        string state = enable ? Lang.T("включено", "enabled") : Lang.T("отключено", "disabled");
        string was = e.Enabled ? Lang.T("включено", "enabled") : Lang.T("отключено", "disabled");
        string why = enable
            ? Lang.T("Программа снова будет запускаться вместе с Windows.", "The program will start with Windows again.")
            : Lang.T("Как в Диспетчере задач: запись остаётся на месте, Windows просто не запускает программу. Вернуть можно в любой момент.",
                "Like Task Manager: the entry stays, Windows just doesn't start it. You can turn it back on any time.");

        if (e.Source == StartupSource.Task)
        {
            return new Change
            {
                ItemId = "startup",
                Kind = ChangeKind.TaskEnabled,
                TaskPath = e.TaskPath,
                Data = enable ? "1" : "0",
                Title = e.DisplayName,
                Before = was,
                After = state,
                Why = why,
            };
        }

        var key = StartupApproved.KeyFor(e.Source)!.Value;
        // Для папок значение называется по имени файла с расширением.
        string valueName = e.Source is StartupSource.UserFolder or StartupSource.CommonFolder
            ? FolderFileName(e)
            : e.Name;
        byte[] data = enable ? StartupApproved.Enabled() : StartupApproved.Disabled(DateTime.UtcNow);
        return new Change
        {
            ItemId = "startup",
            Kind = ChangeKind.RegistrySet,
            Hive = key.Machine ? RegHive.LocalMachine : RegHive.CurrentUser,
            Key = key.Key,
            ValueName = valueName,
            ValueKind = "Binary",
            Data = Convert.ToBase64String(data),
            Title = e.DisplayName,
            Before = was,
            After = state,
            Why = why,
        };
    }

    private static string FolderFileName(StartupEntry e)
    {
        string folder = Environment.GetFolderPath(e.Source == StartupSource.UserFolder
            ? Environment.SpecialFolder.Startup
            : Environment.SpecialFolder.CommonStartup);
        foreach (var ext in new[] { ".lnk", "" })
        {
            string candidate = e.Name + ext;
            if (File.Exists(Path.Combine(folder, candidate))) return candidate;
        }
        return e.Name + ".lnk";
    }
}
