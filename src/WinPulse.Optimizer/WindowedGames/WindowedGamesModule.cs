using WinPulse.Core;
using WinPulse.Optimizer.Changes;
using WinPulse.Optimizer.Platform;

namespace WinPulse.Optimizer.WindowedGames;

/// <summary>
/// Модуль из пунктов с проверкой, исправлением и откатом. У каждого модуля своя резервная копия:
/// %LOCALAPPDATA%\WinPulse\backups\&lt;id&gt;.json.
/// </summary>
public class FixModule
{
    public FixModule(string id, IReadOnlyList<FixItem> items, string? backupPath = null)
    {
        Id = id;
        Items = items;
        Backup = new BackupStore(backupPath ?? DefaultPath(id), id);
        Executor = new ChangeExecutor(Backup);
    }

    public string Id { get; }
    public IReadOnlyList<FixItem> Items { get; }
    /// <summary>Пунктам нужны сведения о железе (память, PCIe, диски игр) — их чтение занимает пару секунд.</summary>
    public virtual bool NeedsHardware => false;
    /// <summary>Модуль только находит и объясняет, ничего не меняет (нет кнопок «Исправить всё» и резервной копии).</summary>
    public virtual bool IsDiagnosticOnly => false;
    public BackupStore Backup { get; }
    public ChangeExecutor Executor { get; }

    public static string DefaultPath(string id) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPulse", "backups", id + ".json");

    /// <summary>План «Вернуть как было» для пункта (или для всего модуля, если itemId == null).</summary>
    public List<Change> PlanRevert(string? itemId)
    {
        var entries = itemId is null ? Backup.Entries.ToList() : Backup.ForItem(itemId);
        return RevertPlanner.Plan(entries, id => Items.FirstOrDefault(i => i.Id == id)?.Title);
    }
}

/// <summary>Модуль «Игры в окне и без рамки»: 13 пунктов ТЗ.</summary>
public sealed class WindowedGamesModule : FixModule
{
    public WindowedGamesModule(string? backupPath = null)
        : base("windowed-games", new FixItem[]
        {
            new SwapEffectItem(),
            new VrrOptimizeItem(),
            new GsyncItem(),
            new FsoFlagItem(),
            new GpuPreferenceItem(),
            new HagsItem(),
            new GameModeItem(),
            new GameDvrItem(),
            new GpuAppsItem(),
            new MultiMonitorItem(),
            new FpsCapItem(),
            new PowerPlanItem(),
            new MpoItem(),
        }, backupPath)
    {
    }

    public static string DefaultBackupPath => DefaultPath("windowed-games");
}

public static class RevertPlanner
{
    public static List<Change> Plan(IEnumerable<BackupEntry> entries, Func<string, string?> itemTitle)
    {
        var result = new List<Change>();
        var order = new Dictionary<string, int> { ["detach"] = 0, ["display"] = 1, ["registry"] = 2, ["task"] = 3, ["nvidia"] = 4, ["power"] = 5 };
        foreach (var e in entries.OrderBy(e => order.GetValueOrDefault(e.Kind, 9)))
        {
            string title = itemTitle(e.ItemId) ?? e.Title;
            string why = Lang.T("Возврат исходного значения из резервной копии.", "Restoring the original value from the backup.");
            switch (e.Kind)
            {
                case "registry":
                {
                    string current = CurrentRegistryText(e);
                    var c = new Change
                    {
                        ItemId = e.ItemId,
                        Kind = e.Existed ? ChangeKind.RegistrySet : ChangeKind.RegistryDelete,
                        Hive = e.Hive,
                        Key = e.Key ?? "",
                        ValueName = e.ValueName ?? "",
                        SubKey = e.SubKey,
                        ValueKind = e.ValueKind ?? "String",
                        Data = e.Existed ? e.Data ?? "" : null,
                        Title = title,
                        Before = current,
                        After = e.Existed
                            ? e.Data ?? ""
                            : Lang.T("параметр будет удалён (до изменения его не было)", "value will be removed (it did not exist before)"),
                        Why = why,
                        RevertTargetId = e.TargetId,
                        NeedsRestart = e.ItemId is "hags" or "mpo",
                    };
                    result.Add(c);
                    break;
                }
                case "power":
                {
                    if (e.SchemeGuid is null) break;
                    result.Add(new Change
                    {
                        ItemId = e.ItemId,
                        Kind = ChangeKind.PowerActivate,
                        SchemeGuid = e.SchemeGuid,
                        Title = title,
                        Before = Lang.T("текущая схема", "current plan"),
                        After = e.SchemeName ?? PowerSchemes.FriendlyName(e.SchemeGuid),
                        Why = why,
                        RevertTargetId = e.TargetId,
                    });
                    if (e.CreatedSchemeGuid is not null)
                    {
                        result.Add(new Change
                        {
                            ItemId = e.ItemId,
                            Kind = ChangeKind.PowerDelete,
                            SchemeGuid = e.CreatedSchemeGuid,
                            Title = title,
                            Before = Lang.T("копия «Высокой производительности», созданная WinPulse", "High performance copy created by WinPulse"),
                            After = Lang.T("удалена", "deleted"),
                            Why = Lang.T("Убираем схему, которую добавил WinPulse.", "Removing the plan WinPulse added."),
                        });
                    }
                    break;
                }
                case "display":
                    result.Add(new Change
                    {
                        ItemId = e.ItemId,
                        Kind = ChangeKind.DisplayRefresh,
                        DeviceName = e.DeviceName,
                        Width = e.Width,
                        Height = e.Height,
                        Frequency = e.Frequency,
                        Title = title,
                        Before = Lang.T("текущая частота", "current refresh rate"),
                        After = $"{e.Width}×{e.Height}, {e.Frequency} " + Lang.T("Гц", "Hz"),
                        Why = why,
                        RevertTargetId = e.TargetId,
                    });
                    break;
                case "nvidia":
                    result.Add(new Change
                    {
                        ItemId = e.ItemId,
                        Kind = ChangeKind.NvidiaSetting,
                        NvApp = e.NvApp,
                        NvAppName = e.NvAppName,
                        NvSettingId = e.NvSettingId,
                        NvValue = e.Existed && uint.TryParse(e.Data, out uint v) ? v : null,
                        Title = title,
                        Before = Lang.T("текущее значение", "current value"),
                        After = e.Existed ? (e.Data ?? "") : Lang.T("по умолчанию драйвера", "driver default"),
                        Why = why,
                        RevertTargetId = e.TargetId,
                    });
                    break;
                case "task":
                    result.Add(new Change
                    {
                        ItemId = e.ItemId,
                        Kind = ChangeKind.TaskEnabled,
                        TaskPath = e.TaskPath,
                        Data = e.Data == "1" ? "1" : "0",
                        Title = title,
                        Before = Lang.T("текущее состояние", "current state"),
                        After = e.Data == "1" ? Lang.T("задание включено", "task enabled") : Lang.T("задание отключено", "task disabled"),
                        Why = why,
                        RevertTargetId = e.TargetId,
                    });
                    break;
                case "detach":
                    result.Add(new Change
                    {
                        ItemId = e.ItemId,
                        Kind = ChangeKind.DisplayAttach,
                        DeviceName = e.DeviceName,
                        Width = e.Width,
                        Height = e.Height,
                        Frequency = e.Frequency,
                        PositionX = e.PositionX,
                        PositionY = e.PositionY,
                        BitsPerPixel = e.BitsPerPixel,
                        Title = title,
                        Before = Lang.T("монитор отключён", "monitor detached"),
                        After = Lang.T("монитор снова включён", "monitor attached again") + $" ({e.Width}×{e.Height}, {e.Frequency} " + Lang.T("Гц)", "Hz)"),
                        Why = why,
                        RevertTargetId = e.TargetId,
                    });
                    break;
            }
        }
        return result;
    }

    private static string CurrentRegistryText(BackupEntry e)
    {
        var state = Reg.Read(e.Hive, e.Key ?? "", e.ValueName ?? "");
        if (!state.Exists) return Lang.T("параметра нет", "not set");
        if (e.SubKey is null) return state.Data ?? "";
        return Core.Tweaks.KeyValueSettings.Get(state.Data, e.SubKey) ?? Lang.T("параметра нет", "not set");
    }
}
