using System.Text.Json.Serialization;

namespace WinPulse.Optimizer.Changes;

public enum ChangeKind
{
    /// <summary>Записать значение реестра (или подзначение «Ключ=Значение;» внутри строки, если задан SubKey).</summary>
    RegistrySet,
    /// <summary>Удалить значение реестра (или только подзначение, если задан SubKey).</summary>
    RegistryDelete,
    /// <summary>Сделать схему электропитания активной (при необходимости создать её копию).</summary>
    PowerActivate,
    /// <summary>Удалить схему электропитания, которую создал WinPulse.</summary>
    PowerDelete,
    /// <summary>Сменить частоту монитора при текущем разрешении.</summary>
    DisplayRefresh,
    /// <summary>Вернуть отключённый монитор.</summary>
    DisplayAttach,
    /// <summary>Включить или отключить задание Планировщика (Data: "1" / "0").</summary>
    TaskEnabled,
}

public enum RegHive { CurrentUser, LocalMachine }

/// <summary>
/// Одно изменение системы: что меняем, было → станет, зачем.
/// Сериализуется в JSON для вспомогательного процесса с правами администратора.
/// </summary>
public sealed class Change
{
    public string ItemId { get; set; } = "";
    public ChangeKind Kind { get; set; }

    // Реестр
    public RegHive Hive { get; set; }
    public string Key { get; set; } = "";
    public string ValueName { get; set; } = "";
    /// <summary>Имя RegistryValueKind: DWord, String, QWord, ExpandString, MultiString, Binary.</summary>
    public string ValueKind { get; set; } = "DWord";
    public string? Data { get; set; }
    /// <summary>Для строк вида «A=1;B=2;» — меняем только этот ключ.</summary>
    public string? SubKey { get; set; }

    // Электропитание
    public string? SchemeGuid { get; set; }
    public bool CreateIfMissing { get; set; }

    // Задание Планировщика
    public string? TaskPath { get; set; }

    // Монитор
    public string? DeviceName { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Frequency { get; set; }
    public int PositionX { get; set; }
    public int PositionY { get; set; }
    public int BitsPerPixel { get; set; }

    // Для человека
    public string Title { get; set; } = "";
    public string Before { get; set; } = "";
    public string After { get; set; } = "";
    public string Why { get; set; } = "";
    public bool NeedsRestart { get; set; }

    /// <summary>Это откат: при успехе убрать запись резервной копии с этим идентификатором.</summary>
    public string? RevertTargetId { get; set; }

    [JsonIgnore]
    public bool IsRevert => RevertTargetId is not null || Kind == ChangeKind.PowerDelete;

    [JsonIgnore]
    public bool NeedsAdmin => Kind switch
    {
        ChangeKind.RegistrySet or ChangeKind.RegistryDelete => Hive == RegHive.LocalMachine,
        ChangeKind.PowerActivate or ChangeKind.PowerDelete or ChangeKind.TaskEnabled => true,
        _ => false,
    };

    /// <summary>Что именно меняется — по этому ключу хранится исходное значение в резервной копии.</summary>
    [JsonIgnore]
    public string TargetId => Kind switch
    {
        ChangeKind.RegistrySet or ChangeKind.RegistryDelete =>
            ("reg:" + Hive + "\\" + Key + "\\" + ValueName + (SubKey is null ? "" : "#" + SubKey)).ToLowerInvariant(),
        ChangeKind.PowerActivate or ChangeKind.PowerDelete => "power:active",
        ChangeKind.DisplayRefresh => ("display:" + DeviceName).ToLowerInvariant(),
        ChangeKind.DisplayAttach => ("detach:" + DeviceName).ToLowerInvariant(),
        ChangeKind.TaskEnabled => ("task:" + TaskPath).ToLowerInvariant(),
        _ => "",
    };

    /// <summary>Где меняется — для подробного режима.</summary>
    [JsonIgnore]
    public string Location => Kind switch
    {
        ChangeKind.RegistrySet or ChangeKind.RegistryDelete =>
            (Hive == RegHive.LocalMachine ? "HKLM\\" : "HKCU\\") + Key + " → " + (ValueName.Length == 0 ? "(по умолчанию)" : ValueName) +
            (SubKey is null ? "" : " → " + SubKey),
        ChangeKind.PowerActivate or ChangeKind.PowerDelete => "powercfg " + (SchemeGuid ?? ""),
        ChangeKind.DisplayRefresh or ChangeKind.DisplayAttach => DeviceName ?? "",
        ChangeKind.TaskEnabled => "Планировщик заданий → " + (TaskPath ?? ""),
        _ => "",
    };
}

public sealed class ChangeResult
{
    public int Index { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
    /// <summary>GUID схемы питания, которую пришлось создать (копия «Высокой производительности»).</summary>
    public string? CreatedSchemeGuid { get; set; }

    [JsonIgnore]
    public Change? Change { get; set; }
}
