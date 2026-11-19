using WinPulse.Core;
using WinPulse.Optimizer.Changes;

namespace WinPulse.Optimizer.WindowedGames;

public enum FixStatus
{
    /// <summary>В порядке.</summary>
    Ok,
    /// <summary>Можно улучшить.</summary>
    Improvable,
    /// <summary>Недоступно на этой системе (с причиной).</summary>
    NotAvailable,
}

/// <summary>Дополнительная кнопка пункта.</summary>
public sealed record FixAction(string Id, string Label, bool Enabled = true, bool Danger = false);

/// <summary>Подпункт: найденная программа, монитор и т. п.</summary>
public sealed record FixEntry(string Title, string Detail, FixAction? Action = null, bool Warning = false);

/// <summary>Результат проверки пункта.</summary>
public sealed class FixState
{
    public FixStatus Status { get; init; }
    /// <summary>Коротко: «Включено», «Флаг установлен», «165 и 60 Гц».</summary>
    public string StatusText { get; init; } = "";
    /// <summary>Почему недоступно или что именно не так.</summary>
    public string? Reason { get; init; }
    /// <summary>Отдельное предупреждение (например, про DLSS Frame Generation).</summary>
    public string? Warning { get; init; }
    public bool CanFix { get; init; }
    public string? FixLabel { get; init; }
    /// <summary>Войдёт в «Исправить всё рекомендуемое».</summary>
    public bool Recommended { get; init; }
    public bool NeedsAdmin { get; init; }
    public bool NeedsRestart { get; init; }
    /// <summary>Для профи: где и что лежит сейчас.</summary>
    public string? Technical { get; init; }
    public IReadOnlyList<string> Steps { get; init; } = [];
    public IReadOnlyList<FixEntry> Entries { get; init; } = [];
    public IReadOnlyList<FixAction> Actions { get; init; } = [];

    public static FixState Unavailable(string reason) => new()
    {
        Status = FixStatus.NotAvailable,
        StatusText = Lang.T("Недоступно", "Not available"),
        Reason = reason,
    };

    public static FixState NeedGame() => Unavailable(Lang.T(
        "Сначала выберите игру вверху страницы — этот пункт настраивается для конкретного exe.",
        "Pick a game at the top of the page first — this item is set per game executable."));
}

/// <summary>Что вернуть после действия: обновить пункт и/или предложить план изменений (пройдёт через подтверждение).</summary>
public sealed class ActionOutcome
{
    public bool Refresh { get; init; }
    public List<Change>? Plan { get; init; }
    public string? Message { get; init; }

    public static readonly ActionOutcome None = new();
    public static readonly ActionOutcome Refreshed = new() { Refresh = true };
}

/// <summary>То, что пунктам нужно от интерфейса: подтверждения, обратный отсчёт, отметки «я сделал».</summary>
public interface IFixHost
{
    Task<bool> ConfirmAsync(string title, string message, string confirmText, bool danger = false);
    /// <summary>Окно «Оставить изменения?» с обратным отсчётом. true — оставить.</summary>
    Task<bool> KeepChangesAsync(string title, string message, int seconds);
    void Notify(string title, string message);
    bool GetMark(string key);
    void SetMark(string key, bool value);
}

public abstract class FixItem
{
    public abstract string Id { get; }
    public abstract int Number { get; }
    public abstract string Title { get; }
    /// <summary>Что это и зачем — простыми словами.</summary>
    public abstract string Explanation { get; }
    /// <summary>Раздел на странице (null — без заголовка). Пункты одного раздела должны идти подряд.</summary>
    public virtual string? Section => null;

    public abstract FixState Evaluate(FixContext ctx);

    /// <summary>План изменений для кнопки «Исправить». Ничего не меняет.</summary>
    public virtual List<Change> PlanFix(FixContext ctx) => new();

    public virtual Task<ActionOutcome> RunActionAsync(string actionId, FixContext ctx, IFixHost host) =>
        Task.FromResult(ActionOutcome.None);

    protected Change RegChange(RegHive hive, string key, string name, string kind, string? data, string before, string after, string why,
        bool restart = false, string? subKey = null) => new()
    {
        ItemId = Id,
        Kind = data is null ? ChangeKind.RegistryDelete : ChangeKind.RegistrySet,
        Hive = hive,
        Key = key,
        ValueName = name,
        ValueKind = kind,
        Data = data,
        SubKey = subKey,
        Title = Title,
        Before = before,
        After = after,
        Why = why,
        NeedsRestart = restart,
    };

    protected static string Missing => Lang.T("параметра нет", "not set");
}
