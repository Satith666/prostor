using System.Runtime.InteropServices;
using WinPulse.Core;

namespace WinPulse.Optimizer.Platform;

public sealed record ScheduledTaskInfo(
    string Path,
    string Name,
    bool Enabled,
    string? ExePath,
    string? Arguments,
    string? Author,
    string? Description,
    bool AtLogon,
    bool AtBoot);

/// <summary>Планировщик заданий через COM (Schedule.Service). Системные задания из папки \Microsoft не трогаем.</summary>
public static class ScheduledTasks
{
    private const int TASK_ENUM_HIDDEN = 1;
    private const int TASK_TRIGGER_BOOT = 8;
    private const int TASK_TRIGGER_LOGON = 9;
    private const int TASK_ACTION_EXEC = 0;

    private static dynamic Connect()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service")
                   ?? throw new InvalidOperationException(Lang.T("Планировщик заданий недоступен", "Task Scheduler is unavailable"));
        dynamic service = Activator.CreateInstance(type)!;
        service.Connect();
        return service;
    }

    public static bool IsSystemPath(string path) =>
        path.StartsWith(@"\Microsoft\", StringComparison.OrdinalIgnoreCase) || path.Equals(@"\Microsoft", StringComparison.OrdinalIgnoreCase);

    /// <summary>Сторонние задания, которые запускаются при входе в систему или при загрузке.</summary>
    public static List<ScheduledTaskInfo> StartupTasks()
    {
        var list = new List<ScheduledTaskInfo>();
        dynamic? service = null;
        try
        {
            service = Connect();
            Walk(service.GetFolder("\\"), list, 0);
        }
        catch (Exception ex)
        {
            Diag.Report("tasks", ex);
        }
        finally
        {
            Release(service);
        }
        return list;
    }

    private static void Walk(dynamic folder, List<ScheduledTaskInfo> list, int depth)
    {
        string folderPath = (string)folder.Path;
        if (IsSystemPath(folderPath) || depth > 8) return;

        dynamic tasks = folder.GetTasks(TASK_ENUM_HIDDEN);
        int count = (int)tasks.Count;
        for (int i = 1; i <= count; i++)
        {
            try
            {
                dynamic task = tasks[i];
                string path = (string)task.Path;
                if (IsSystemPath(path)) continue;
                dynamic definition = task.Definition;

                bool atLogon = false, atBoot = false;
                dynamic triggers = definition.Triggers;
                int triggerCount = (int)triggers.Count;
                for (int t = 1; t <= triggerCount; t++)
                {
                    dynamic trigger = triggers[t];
                    int type = (int)trigger.Type;
                    if (!(bool)trigger.Enabled) continue;
                    if (type == TASK_TRIGGER_LOGON) atLogon = true;
                    else if (type == TASK_TRIGGER_BOOT) atBoot = true;
                }
                if (!atLogon && !atBoot) continue;

                string? exe = null, args = null;
                dynamic actions = definition.Actions;
                int actionCount = (int)actions.Count;
                for (int a = 1; a <= actionCount; a++)
                {
                    dynamic action = actions[a];
                    if ((int)action.Type != TASK_ACTION_EXEC) continue;
                    exe = (string?)action.Path;
                    args = (string?)action.Arguments;
                    break;
                }

                dynamic info = definition.RegistrationInfo;
                list.Add(new ScheduledTaskInfo(
                    path,
                    (string)task.Name,
                    (bool)task.Enabled,
                    string.IsNullOrWhiteSpace(exe) ? null : Environment.ExpandEnvironmentVariables(exe.Trim('"')),
                    args,
                    (string?)info.Author,
                    (string?)info.Description,
                    atLogon,
                    atBoot));
            }
            catch (Exception ex)
            {
                Diag.Report("task-read", ex);
            }
        }

        dynamic folders = folder.GetFolders(0);
        int folderCount = (int)folders.Count;
        for (int i = 1; i <= folderCount; i++)
        {
            try
            {
                Walk(folders[i], list, depth + 1);
            }
            catch (Exception ex)
            {
                Diag.Report("task-folder", ex);
            }
        }
    }

    /// <summary>null — задание не найдено или нет доступа.</summary>
    public static bool? IsEnabled(string path)
    {
        dynamic? service = null;
        try
        {
            service = Connect();
            dynamic task = service.GetFolder("\\").GetTask(path);
            return (bool)task.Enabled;
        }
        catch
        {
            return null;
        }
        finally
        {
            Release(service);
        }
    }

    /// <summary>null — успех, иначе текст ошибки.</summary>
    public static string? SetEnabled(string path, bool enabled)
    {
        if (IsSystemPath(path)) return Lang.T("Системные задания Windows не меняем", "Windows system tasks are never changed");
        dynamic? service = null;
        try
        {
            service = Connect();
            dynamic task = service.GetFolder("\\").GetTask(path);
            task.Enabled = enabled;
            return (bool)task.Enabled == enabled ? null : Lang.T("Планировщик не принял изменение", "Task Scheduler did not accept the change");
        }
        catch (UnauthorizedAccessException)
        {
            return Lang.T("Нет доступа — нужны права администратора.", "Access denied — administrator rights required.");
        }
        catch (COMException ex) when ((uint)ex.HResult == 0x80070005)
        {
            return Lang.T("Нет доступа — нужны права администратора.", "Access denied — administrator rights required.");
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
        finally
        {
            Release(service);
        }
    }

    private static void Release(object? com)
    {
        try
        {
            if (com is not null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
        }
        catch
        {
        }
    }
}
