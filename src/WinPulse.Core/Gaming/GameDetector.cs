using WinPulse.Core.Analysis;

namespace WinPulse.Core.Gaming;

/// <summary>Похоже ли приложение на игру. Без списков игр: по папке установки и по работе с видеокартой.</summary>
public static class GameDetector
{
    // Папки, куда ставят игры лаунчеры. Сравнение без учёта регистра.
    private static readonly string[] GameFolders =
    [
        @"\steamapps\common\",
        @"\epic games\",
        @"\gog games\",
        @"\gog galaxy\games\",
        @"\riot games\",
        @"\xboxgames\",
        @"\windowsapps\", // игры из Microsoft Store / Game Pass лежат здесь, но вместе с обычными приложениями — только вместе с нагрузкой на видеокарту
        @"\ubisoft game launcher\games\",
        @"\ea games\",
        @"\origin games\",
        @"\battle.net\games\",
        @"\games\",
    ];

    // Точно не игры, даже если активно рисуют: лаунчеры, браузеры, запись, оболочка Windows.
    private static readonly HashSet<string> NotGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam.exe", "steamwebhelper.exe", "epicgameslauncher.exe", "epicwebhelper.exe", "battle.net.exe", "galaxyclient.exe",
        "eadesktop.exe", "origin.exe", "upc.exe", "ubisoftconnect.exe", "riotclientservices.exe", "riotclientux.exe",
        "explorer.exe", "dwm.exe", "applicationframehost.exe", "searchhost.exe", "startmenuexperiencehost.exe", "shellexperiencehost.exe",
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "yandex.exe", "browser.exe",
        "discord.exe", "telegram.exe", "obs64.exe", "obs32.exe", "wallpaper32.exe", "wallpaper64.exe", "webwallpaper32.exe",
        "vlc.exe", "mpc-hc64.exe", "potplayermini64.exe", "spotify.exe", "nvidia app.exe", "nvcontainer.exe",
        "winpulse.exe", "presentmon.exe", "taskmgr.exe", "msiafterburner.exe", "rtss.exe", "code.exe", "devenv.exe",
        "blender.exe", "photoshop.exe", "premiere pro.exe", "davinci resolve.exe", "resolve.exe",
    };

    public static bool LooksLikeGameFolder(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        string p = path.ToLowerInvariant();
        return GameFolders.Any(f => f != @"\windowsapps\" && p.Contains(f, StringComparison.Ordinal));
    }

    public static bool IsExcluded(string? path, string name)
    {
        string exe = string.IsNullOrEmpty(path) ? name : System.IO.Path.GetFileName(path);
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
        return NotGames.Contains(exe);
    }

    /// <summary>
    /// Кандидат в игры: приложение на переднем плане, которое лежит в папке игр или заметно грузит видеокарту.
    /// <paramref name="pinned"/> — exe, который пользователь сам указал как игру.
    /// </summary>
    public static bool IsCandidate(AppGroup app, bool foreground, bool pinned)
    {
        if (app.Kind != AppKind.Application) return false;
        if (IsExcluded(app.Path, app.DisplayName)) return false;
        if (pinned) return true;
        if (!foreground) return false;
        if (LooksLikeGameFolder(app.Path)) return true;
        return app.GpuPercent >= 30;
    }
}
