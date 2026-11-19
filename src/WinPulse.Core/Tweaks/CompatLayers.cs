namespace WinPulse.Core.Tweaks;

/// <summary>
/// Флаги совместимости из ...\AppCompatFlags\Layers: «~ DISABLEDXMAXIMIZEDWINDOWEDMODE HIGHDPIAWARE».
/// Первый маркер «~» или «$» служебный; остальные флаги сохраняем как есть.
/// </summary>
public static class CompatLayers
{
    public const string DisableFullscreenOptimizations = "DISABLEDXMAXIMIZEDWINDOWEDMODE";

    public static bool Has(string? value, string flag) =>
        Tokens(value).Any(t => t.Equals(flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>Убирает флаг. Возвращает null, если после удаления не осталось значимых флагов (значение нужно удалить).</summary>
    public static string? Remove(string? value, string flag)
    {
        var tokens = Tokens(value).Where(t => !t.Equals(flag, StringComparison.OrdinalIgnoreCase)).ToList();
        bool meaningful = tokens.Any(t => t is not ("~" or "$"));
        return meaningful ? string.Join(' ', tokens) : null;
    }

    private static string[] Tokens(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
}
