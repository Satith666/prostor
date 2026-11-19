using System.Diagnostics;
using WinPulse.Core;
using WinPulse.Optimizer.Platform;

namespace WinPulse.Optimizer.Gaming;

/// <summary>Что делает игровой режим. Всё обратимо и возвращается после игры.</summary>
public sealed class BoostOptions
{
    /// <summary>Схема питания «Высокая» или «Максимальная производительность» на время игры.</summary>
    public bool PowerPlan { get; set; } = true;
    /// <summary>Пауза Wallpaper Engine штатной командой.</summary>
    public bool PauseWallpaper { get; set; } = true;
    /// <summary>Высокий приоритет процессу игры (не «реального времени»).</summary>
    public bool GamePriority { get; set; } = true;
    /// <summary>Пониженный приоритет браузерам, мессенджерам и лаунчерам в фоне.</summary>
    public bool BackgroundPriority { get; set; } = true;
}

/// <summary>Что было изменено — чтобы вернуть ровно это.</summary>
public sealed class BoostState
{
    public string Game { get; init; } = "";
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
    public string? PreviousScheme { get; set; }
    public string? WallpaperExe { get; set; }
    public List<(int Pid, DateTime StartTime, ProcessPriorityClass Old)> Priorities { get; } = new();
    public List<string> Done { get; } = new();
    public List<string> Skipped { get; } = new();
}

/// <summary>
/// Игровой режим: включается, когда WinPulse замечает игру, и всё возвращает после неё.
/// Сознательно не делает: приоритет «реального времени», отключение служб, «очистку памяти».
/// </summary>
public static class GameBoost
{
    // Фоновые программы, которым можно без вреда понизить приоритет на время игры.
    // OBS и программы записи сюда не входят: с низким приоритетом у них пропадают кадры записи.
    private static readonly HashSet<string> Background = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "opera", "brave", "vivaldi", "browser", "yandex",
        "discord", "telegram", "whatsapp", "slack", "teams", "ms-teams", "skype", "viber", "zoom",
        "steamwebhelper", "epicwebhelper", "epicgameslauncher", "battle.net", "galaxyclient", "eadesktop", "upc",
        "onedrive", "dropbox", "googledrivefs", "yandexdisk2", "spotify", "itunes", "qbittorrent", "utorrent", "code",
    };

    public static BoostState Apply(string game, string? gameExePath, BoostOptions options)
    {
        var state = new BoostState { Game = game };

        if (options.PowerPlan)
        {
            try
            {
                var schemes = PowerSchemes.List();
                string? active = schemes.FirstOrDefault(s => s.Active)?.Guid ?? PowerSchemes.ActiveGuid();
                var target = schemes.FirstOrDefault(s => s.Guid.Equals(PowerSchemes.Ultimate, StringComparison.OrdinalIgnoreCase))
                             ?? schemes.FirstOrDefault(s => s.Guid.Equals(PowerSchemes.HighPerformance, StringComparison.OrdinalIgnoreCase));
                if (target is null)
                {
                    state.Skipped.Add(Lang.T("Схема «Высокая производительность» скрыта в этой Windows — её можно включить в «Играх в окне».",
                        "The High performance plan is hidden in this Windows — enable it in Windowed games."));
                }
                else if (!string.Equals(active, target.Guid, StringComparison.OrdinalIgnoreCase))
                {
                    string? error = PowerSchemes.SetActive(target.Guid);
                    if (error is null)
                    {
                        state.PreviousScheme = active;
                        state.Done.Add(Lang.T($"Схема питания: «{target.Name}»", $"Power plan: “{target.Name}”"));
                    }
                    else
                    {
                        state.Skipped.Add(Lang.T("Схему питания сменить не удалось: ", "Couldn't switch the power plan: ") + error);
                    }
                }
            }
            catch (Exception ex)
            {
                Diag.Report("boost-power", ex);
            }
        }

        if (options.PauseWallpaper)
        {
            string? exe = WallpaperEngine();
            if (exe is not null && Run(exe, "-control pause"))
            {
                state.WallpaperExe = exe;
                state.Done.Add(Lang.T("Wallpaper Engine на паузе", "Wallpaper Engine paused"));
            }
        }

        if (options.GamePriority && gameExePath is not null)
        {
            foreach (var p in ProcessesAt(gameExePath))
            {
                if (SetPriority(p, ProcessPriorityClass.High, state))
                    state.Done.Add(Lang.T($"Высокий приоритет: {game}", $"High priority: {game}"));
                else
                    state.Skipped.Add(Lang.T($"Приоритет игры не изменён (защита античита): {game}", $"Game priority unchanged (anti-cheat protection): {game}"));
                p.Dispose();
            }
        }

        if (options.BackgroundPriority)
        {
            int lowered = 0;
            foreach (var p in SafeProcesses())
            {
                try
                {
                    if (Background.Contains(p.ProcessName) && p.PriorityClass == ProcessPriorityClass.Normal &&
                        SetPriority(p, ProcessPriorityClass.BelowNormal, state))
                        lowered++;
                }
                catch
                {
                    // Чужие или защищённые процессы пропускаем.
                }
                finally
                {
                    p.Dispose();
                }
            }
            if (lowered > 0)
                state.Done.Add(Lang.T($"Пониженный приоритет фоновым программам: {lowered}", $"Lower priority for background apps: {lowered}"));
        }

        return state;
    }

    /// <summary>Вернуть всё, что было изменено. Возвращает список того, что вернуть не удалось.</summary>
    public static List<string> Restore(BoostState state)
    {
        var problems = new List<string>();
        if (state.PreviousScheme is not null)
        {
            try
            {
                string? error = PowerSchemes.SetActive(state.PreviousScheme);
                if (error is not null) problems.Add(Lang.T("Схема питания: ", "Power plan: ") + error);
            }
            catch (Exception ex)
            {
                problems.Add(ex.Message);
            }
        }

        if (state.WallpaperExe is not null) Run(state.WallpaperExe, "-control play");

        foreach (var (pid, start, old) in state.Priorities)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.StartTime != start) continue; // номер процесса уже занят другим — не трогаем
                p.PriorityClass = old;
            }
            catch
            {
                // Процесс уже закрыт — возвращать нечего.
            }
        }
        return problems;
    }

    private static bool SetPriority(Process p, ProcessPriorityClass value, BoostState state)
    {
        try
        {
            var old = p.PriorityClass;
            if (old == value) return true;
            var start = p.StartTime;
            p.PriorityClass = value;
            state.Priorities.Add((p.Id, start, old));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? WallpaperEngine()
    {
        foreach (var name in new[] { "wallpaper64", "wallpaper32" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    string? path = p.MainModule?.FileName;
                    if (path is not null) return path;
                }
                catch
                {
                    // Нет доступа к пути — попробуем следующий.
                }
                finally
                {
                    p.Dispose();
                }
            }
        }
        return null;
    }

    private static IEnumerable<Process> ProcessesAt(string exePath)
    {
        string name = Path.GetFileNameWithoutExtension(exePath);
        foreach (var p in Process.GetProcessesByName(name))
        {
            string? path = null;
            try
            {
                path = p.MainModule?.FileName;
            }
            catch
            {
                // Защищённый процесс: путь не прочитать, но имя совпало — пробуем.
            }
            if (path is null || path.Equals(exePath, StringComparison.OrdinalIgnoreCase)) yield return p;
            else p.Dispose();
        }
    }

    private static Process[] SafeProcesses()
    {
        try
        {
            return Process.GetProcesses();
        }
        catch
        {
            return [];
        }
    }

    private static bool Run(string exe, string args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true });
            p?.WaitForExit(5000);
            return p is not null;
        }
        catch (Exception ex)
        {
            Diag.Report("boost-run " + Path.GetFileName(exe), ex);
            return false;
        }
    }
}
