namespace WinPulse.Core;

/// <summary>
/// Язык интерфейса. Тексты пишутся парами прямо в коде: Lang.T("Сохранить", "Save") —
/// так ни один перевод не потеряется.
/// </summary>
public static class Lang
{
    private static bool _english;

    public static bool English
    {
        get => _english;
        set
        {
            if (_english == value) return;
            _english = value;
            Changed?.Invoke();
        }
    }

    public static string Code => _english ? "en" : "ru";

    public static event Action? Changed;

    public static string T(string ru, string en) => _english ? en : ru;
}
