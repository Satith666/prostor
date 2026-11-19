using WinPulse.Core.Analysis;
using WinPulse.Core.Model;

namespace WinPulse.Core.Diagnostics;

/// <summary>Одна запись журнала Windows в удобном виде.</summary>
public sealed class EventItem
{
    private static readonly IReadOnlyDictionary<string, string> NoNamed =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public string Log { get; init; } = "";
    public string Provider { get; init; } = "";
    public int Id { get; init; }
    /// <summary>1 — критическая, 2 — ошибка, 3 — предупреждение, 4 — сведения.</summary>
    public int Level { get; init; } = 4;
    /// <summary>Местное время.</summary>
    public DateTime Time { get; init; }
    /// <summary>Именованные поля EventData (имя без учёта регистра).</summary>
    public IReadOnlyDictionary<string, string> Named { get; init; } = NoNamed;
    /// <summary>Все значения EventData по порядку (и именованные, и безымянные).</summary>
    public IReadOnlyList<string> Data { get; init; } = [];

    public string? Get(string name) => Named.TryGetValue(name, out var v) ? v : null;
    public string? At(int index) => index >= 0 && index < Data.Count ? Data[index] : null;
    /// <summary>Поле по имени (новые версии Windows) или по позиции (старые).</summary>
    public string? Get(string name, int index) => Get(name) ?? At(index);

    public bool Is(string provider, int id) => Id == id && Provider.Equals(provider, StringComparison.OrdinalIgnoreCase);
    public bool From(string provider) => Provider.Equals(provider, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Устройство в Диспетчере устройств с жёлтым значком.</summary>
public sealed record DeviceProblem(string Name, string InstanceId, string? DeviceClass, int ProblemCode);

/// <summary>Состояние физического диска по данным Windows (Get-PhysicalDisk, Get-StorageReliabilityCounter).</summary>
public sealed record DiskHealthInfo(
    string Name,
    string Media,
    string Bus,
    string Health,
    long Size,
    int? WearPercent,
    int? Temperature,
    int? TemperatureMax,
    long? ReadErrorsUncorrected,
    long? PowerOnHours);

/// <summary>Всё, что собрано для диагностики. Собирается в Collectors, анализируется здесь — так детекторы можно тестировать.</summary>
public sealed class DiagnosticInput
{
    public DateTime Now { get; init; } = DateTime.Now;
    public int PeriodDays { get; init; } = 30;
    public IReadOnlyList<EventItem> Events { get; init; } = [];
    /// <summary>Журнал загрузки (Diagnostics-Performance) прочитан.</summary>
    public bool BootLogRead { get; init; }
    /// <summary>Журнал загрузки недоступен без прав администратора.</summary>
    public bool BootLogDenied { get; init; }
    public bool DevicesChecked { get; init; }
    public IReadOnlyList<DeviceProblem> Devices { get; init; } = [];
    public bool DiskHealthChecked { get; init; }
    public IReadOnlyList<DiskHealthInfo> DiskHealth { get; init; } = [];
    public IReadOnlyDictionary<int, string> DiskNames { get; init; } = new Dictionary<int, string>();
    /// <summary>Причины, по которым Windows ждёт перезагрузки.</summary>
    public IReadOnlyList<string> PendingReboot { get; init; } = [];
    public IReadOnlyList<VolumeState> Volumes { get; init; } = [];
    public string SystemDrive { get; init; } = "C:";
    /// <summary>true — файл подкачки отключён в настройках; null — неизвестно.</summary>
    public bool? PageFileDisabled { get; init; }
    public long TotalPhysical { get; init; }
    public long CommitLimit { get; init; }
    public TimeSpan Uptime { get; init; }
    public bool IsElevated { get; init; }
    /// <summary>Что не удалось прочитать (для заметок внизу отчёта).</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
}

/// <summary>Кнопка в находке: открыть инструмент Windows (ничего не меняет сама).</summary>
public sealed record FindingAction(string Label, string Command, string? Arguments = null);

public sealed record Evidence(DateTime? When, string Text);

/// <summary>Находка: что случилось, что это значит, что делать.</summary>
public sealed class Finding
{
    public string Id { get; init; } = "";
    public string Category { get; init; } = "";
    public Severity Severity { get; init; }
    public string Title { get; init; } = "";
    /// <summary>Что это значит — простыми словами.</summary>
    public string Summary { get; init; } = "";
    /// <summary>Что делать — по шагам.</summary>
    public IReadOnlyList<string> Advice { get; init; } = [];
    /// <summary>Записи, на которых основан вывод.</summary>
    public IReadOnlyList<Evidence> Evidence { get; init; } = [];
    public int Count7 { get; init; }
    public int Count30 { get; init; }
    public DateTime? LastSeen { get; init; }
    /// <summary>Для профи: журналы, источники, коды.</summary>
    public string? Technical { get; init; }
    public IReadOnlyList<FindingAction> Actions { get; init; } = [];
}

/// <summary>День хронологии: сколько было критичных событий, ошибок и предупреждений.</summary>
public sealed record DayBucket(DateOnly Day, int Critical, int Errors, int Warnings)
{
    public int Total => Critical + Errors + Warnings;
}

public sealed class DiagnosticReport
{
    public DateTime ScannedAt { get; init; }
    public int PeriodDays { get; init; }
    /// <summary>Найденные проблемы (Info и выше), от самых серьёзных.</summary>
    public IReadOnlyList<Finding> Problems { get; init; } = [];
    /// <summary>Пройденные проверки.</summary>
    public IReadOnlyList<Finding> Passed { get; init; } = [];
    public IReadOnlyList<DayBucket> Days { get; init; } = [];
    public IReadOnlyList<string> Notes { get; init; } = [];

    public int CriticalCount => Problems.Count(p => p.Severity == Severity.Critical);
    public int WarningCount => Problems.Count(p => p.Severity == Severity.Warning);
    public int InfoCount => Problems.Count(p => p.Severity == Severity.Info);
}
