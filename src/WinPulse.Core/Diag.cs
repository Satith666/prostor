namespace WinPulse.Core;

/// <summary>
/// Сообщения об ошибках сборщиков и модулей. Приложение подписывается и пишет их в журнал.
/// Одинаковые ошибки из одного места пишутся не чаще раза в минуту, чтобы не забить журнал.
/// </summary>
public static class Diag
{
    private static readonly Dictionary<string, DateTime> LastByKey = new();
    private static readonly object Gate = new();

    public static event Action<string>? Message;

    public static void Report(string where, Exception ex) => Write(where, $"{where}: {ex.GetType().Name}: {ex.Message}");

    public static void Info(string text) => Message?.Invoke(text);

    private static void Write(string key, string text)
    {
        lock (Gate)
        {
            var now = DateTime.UtcNow;
            if (LastByKey.TryGetValue(key, out var last) && (now - last).TotalSeconds < 60) return;
            LastByKey[key] = now;
        }
        Message?.Invoke(text);
    }
}
