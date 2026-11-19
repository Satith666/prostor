namespace WinPulse.Core.Startup;

public enum StartupAdvice
{
    /// <summary>Можно отключить — программу легко запустить вручную.</summary>
    CanDisable,
    /// <summary>На ваше усмотрение — есть польза, но не обязательна.</summary>
    Optional,
    /// <summary>Лучше оставить — без неё что-то перестанет работать.</summary>
    Keep,
}

public sealed record StartupTip(StartupAdvice Advice, string Text);

/// <summary>База знаний о частых программах автозагрузки.</summary>
public static class StartupKnowledge
{
    private sealed record Rule(string[] Exe, string[] Names, StartupAdvice Advice, string Text, string TextEn)
    {
        public StartupTip Tip => new(Advice, Lang.English ? TextEn : Text);
    }

    private static readonly Rule[] Rules =
    {
        // Лучше оставить
        R(new[] { "securityhealthsystray.exe" }, new[] { "SecurityHealth" }, StartupAdvice.Keep,
            "Значок «Безопасность Windows» в трее. Защиту не ускоряет и не замедляет — оставьте.", "The Windows Security tray icon. It neither speeds up nor slows down protection — keep it."),
        R(new[] { "rtkauduservice64.exe", "ravcpl64.exe", "rtkngui64.exe", "ravbg64.exe" }, new[] { "Realtek", "RtkAudUService" }, StartupAdvice.Keep,
            "Служебная программа звуковой карты Realtek: определяет подключение наушников и микрофона.", "Realtek audio utility: detects when headphones and a microphone are plugged in."),
        R(new[] { "syntpenh.exe", "etdctrl.exe", "elanfilter.exe" }, new[] { "Synaptics", "ETD", "ELAN" }, StartupAdvice.Keep,
            "Драйвер тачпада: без него перестанут работать жесты и прокрутка двумя пальцами.", "Touchpad driver: without it gestures and two-finger scrolling stop working."),
        R(new[] { "radeonsoftware.exe", "cncmd.exe", "amdrsserv.exe" }, new[] { "AMD Software", "Radeon" }, StartupAdvice.Keep,
            "Панель драйвера AMD: применяет профили игр, FreeSync и ограничение кадров.", "AMD driver panel: applies game profiles, FreeSync and frame limits."),
        R(new[] { "avp.exe", "avpui.exe", "avastui.exe", "egui.exe", "mbam.exe", "mbamtray.exe", "bdagent.exe", "nortonui.exe", "drweb.exe" },
            new[] { "Kaspersky", "Avast", "ESET", "Malwarebytes", "Bitdefender", "Norton", "Dr.Web" }, StartupAdvice.Keep,
            "Антивирус должен запускаться вместе с Windows.", "An antivirus should start with Windows."),
        R(new[] { "rtss.exe", "msiafterburner.exe" }, new[] { "RivaTuner", "Afterburner" }, StartupAdvice.Optional,
            "Если ограничиваете ими FPS, разгоняете видеокарту или смотрите температуры в играх — оставьте; иначе можно отключить.", "Keep it if you use it to cap FPS, overclock the GPU or watch temperatures in games; otherwise it can be disabled."),

        // На ваше усмотрение
        R(new[] { "onedrive.exe" }, new[] { "OneDrive" }, StartupAdvice.Optional,
            "Синхронизирует файлы с облаком OneDrive. Если не пользуетесь — отключите, файлы останутся на компьютере.", "Syncs files with OneDrive. If you don't use it, disable it — your files stay on the PC."),
        R(new[] { "googledrivefs.exe", "dropbox.exe", "yandexdisk2.exe", "yandexdisk.exe" }, new[] { "Google Drive", "Dropbox", "Yandex.Disk", "Яндекс Диск" }, StartupAdvice.Optional,
            "Синхронизация облачного диска. Если отключить — файлы перестанут синхронизироваться, пока не запустите программу.", "Cloud drive sync. If disabled, files won't sync until you start the program."),
        R(new[] { "telegram.exe" }, new[] { "Telegram" }, StartupAdvice.Optional, "Мессенджер: оставьте, если нужны уведомления сразу после включения.", "Messenger: keep it if you want notifications right after startup."),
        R(new[] { "whatsapp.exe" }, new[] { "WhatsApp" }, StartupAdvice.Optional, "Мессенджер: оставьте, если нужны уведомления сразу после включения.", "Messenger: keep it if you want notifications right after startup."),
        R(new[] { "viber.exe" }, new[] { "Viber" }, StartupAdvice.Optional, "Мессенджер: оставьте, если нужны уведомления сразу после включения.", "Messenger: keep it if you want notifications right after startup."),
        R(new[] { "ms-teams.exe", "teams.exe" }, new[] { "Teams" }, StartupAdvice.Optional,
            "Рабочий мессенджер. Если пользуетесь им каждый день — оставьте; иначе запускайте вручную (он тяжёлый: 300–800 МБ памяти).", "Work messenger. Keep it if you use it every day; otherwise start it by hand (it's heavy: 300–800 MB of memory)."),
        R(new[] { "slack.exe" }, new[] { "Slack" }, StartupAdvice.Optional, "Рабочий мессенджер: оставьте, если пользуетесь им каждый день.", "Work messenger: keep it if you use it every day."),
        R(new[] { "qbittorrent.exe", "utorrent.exe", "bittorrent.exe" }, new[] { "qBittorrent", "uTorrent", "BitTorrent" }, StartupAdvice.Optional,
            "Торрент-клиент. Если отключить — загрузки и раздачи продолжатся только после ручного запуска.", "Torrent client. If disabled, downloads and seeding resume only after you start it."),
        R(new[] { "lghub.exe", "lghub_agent.exe", "razercentral.exe", "razer synapse 3.exe", "icue.exe", "steelseriesgg.exe", "armourycrate.exe", "msi center.exe" },
            new[] { "G HUB", "Razer", "iCUE", "Corsair", "SteelSeries", "Armoury", "MSI Center" }, StartupAdvice.Optional,
            "Программа мыши, клавиатуры или подсветки. Без неё могут сброситься подсветка, макросы и чувствительность.", "Mouse, keyboard or RGB software. Without it lighting, macros and sensitivity may reset."),
        R(new[] { "wallpaper32.exe", "wallpaper64.exe", "lively.exe" }, new[] { "Wallpaper Engine", "Lively" }, StartupAdvice.Optional,
            "Живые обои. Нагружают видеокарту — если оставляете, настройте паузу во время игр.", "Live wallpapers. They load the GPU — if you keep them, set them to pause during games."),
        R(new[] { "punto.exe", "ps64ldr.exe" }, new[] { "Punto" }, StartupAdvice.Optional, "Автопереключение раскладки. Оставьте, если пользуетесь.", "Automatic keyboard layout switching. Keep it if you use it."),
        R(new[] { "lightshot.exe" }, new[] { "Lightshot" }, StartupAdvice.Optional, "Программа скриншотов. В Windows есть встроенная: Win+Shift+S.", "Screenshot tool. Windows has one built in: Win+Shift+S."),
        R(new[] { "googleupdate.exe" }, new[] { "GoogleUpdate", "Google Updater" }, StartupAdvice.Optional,
            "Обновляет программы Google. Если отключить, браузер будет обновляться при своём запуске.", "Updates Google software. If disabled, the browser updates itself when it starts."),
        R(new[] { "adobearm.exe", "acrotray.exe" }, new[] { "Adobe Acrobat", "AdobeARM", "Acrobat" }, StartupAdvice.Optional, "Обновление Adobe Acrobat Reader. Можно отключить.", "Adobe Acrobat Reader updater. Can be disabled."),
        R(new[] { "nvidia app.exe", "nvidia share.exe", "nvtray.exe", "nvbackend.exe" }, new[] { "NVIDIA" }, StartupAdvice.Optional,
            "Приложение NVIDIA: оверлей, запись, проверка драйверов. Сам драйвер работает и без него.", "NVIDIA App: overlay, recording, driver checks. The driver itself works without it."),
        R(new[] { "igfxtray.exe", "igfxem.exe", "igfxhk.exe" }, new[] { "Intel Graphics", "igfx" }, StartupAdvice.Optional,
            "Значок и горячие клавиши графики Intel. Можно отключить — драйвер работает без этого.", "Intel graphics tray icon and hotkeys. Can be disabled — the driver works without it."),
        R(new[] { "figma_agent.exe" }, new[] { "Figma Agent" }, StartupAdvice.Optional, "Нужен Figma в браузере для локальных шрифтов.", "Needed by Figma in the browser for local fonts."),
        R(Array.Empty<string>(), new[] { "VPN", "WireGuard", "OpenVPN" }, StartupAdvice.Optional, "VPN-клиент. Оставьте, если подключаетесь сразу после включения.", "VPN client. Keep it if you connect right after startup."),

        // Можно отключить
        R(new[] { "discord.exe" }, new[] { "Discord" }, StartupAdvice.CanDisable, "Запускайте Discord, когда он нужен. Отключение ускоряет загрузку и экономит 200–400 МБ памяти.", "Start Discord when you need it. Disabling speeds up boot and saves 200–400 MB of memory."),
        R(new[] { "steam.exe" }, new[] { "Steam" }, StartupAdvice.CanDisable, "Steam можно запускать перед игрой. Загрузки обновлений начнутся при его запуске.", "Steam can be started before playing. Update downloads begin when it starts."),
        R(new[] { "epicgameslauncher.exe" }, new[] { "EpicGamesLauncher", "Epic Games" }, StartupAdvice.CanDisable, "Лаунчер Epic Games можно запускать вручную.", "The Epic Games launcher can be started by hand."),
        R(new[] { "eadesktop.exe", "origin.exe" }, new[] { "EA Desktop", "EADM", "Origin" }, StartupAdvice.CanDisable, "Лаунчер EA можно запускать вручную.", "The EA launcher can be started by hand."),
        R(new[] { "battle.net.exe", "battle.net launcher.exe" }, new[] { "Battle.net" }, StartupAdvice.CanDisable, "Лаунчер Battle.net можно запускать вручную.", "The Battle.net launcher can be started by hand."),
        R(new[] { "upc.exe", "ubisoftconnect.exe", "ubisoftgamelauncher.exe" }, new[] { "Ubisoft" }, StartupAdvice.CanDisable, "Лаунчер Ubisoft можно запускать вручную.", "The Ubisoft launcher can be started by hand."),
        R(new[] { "galaxyclient.exe" }, new[] { "GOG Galaxy", "GalaxyClient" }, StartupAdvice.CanDisable, "Лаунчер GOG можно запускать вручную.", "The GOG launcher can be started by hand."),
        R(new[] { "spotify.exe" }, new[] { "Spotify" }, StartupAdvice.CanDisable, "Spotify можно запускать, когда хочется музыки.", "Start Spotify when you want music."),
        R(new[] { "skype.exe" }, new[] { "Skype" }, StartupAdvice.CanDisable, "Skype можно запускать вручную.", "Skype can be started by hand."),
        R(new[] { "zoom.exe" }, new[] { "Zoom" }, StartupAdvice.CanDisable, "Zoom достаточно запускать перед встречей.", "It's enough to start Zoom before a meeting."),
        R(new[] { "msedge.exe" }, new[] { "MicrosoftEdgeAutoLaunch", "Microsoft Edge" }, StartupAdvice.CanDisable,
            "«Ускорение запуска» Edge держит браузер в памяти с самого включения. Если вы не пользуетесь Edge постоянно — отключите.", "Edge Startup Boost keeps the browser in memory from boot. If you don't use Edge all the time, disable it."),
        R(new[] { "chrome.exe" }, new[] { "GoogleChromeAutoLaunch", "Chrome" }, StartupAdvice.CanDisable, "Фоновый запуск Chrome держит браузер в памяти. Можно отключить.", "Chrome background mode keeps the browser in memory. Can be disabled."),
        R(new[] { "browser.exe" }, new[] { "Yandex", "Яндекс" }, StartupAdvice.CanDisable, "Автозапуск Яндекс Браузера. Можно отключить.", "Yandex Browser autostart. Can be disabled."),
        R(new[] { "creative cloud.exe", "ccxprocess.exe", "adobegcclient.exe", "coresync.exe" }, new[] { "Creative Cloud", "CCXProcess", "AdobeGC" }, StartupAdvice.CanDisable,
            "Фоновые процессы Adobe Creative Cloud. Запустятся сами, когда откроете Photoshop или другую программу Adobe.", "Adobe Creative Cloud background processes. They start on their own when you open Photoshop or another Adobe app."),
        R(new[] { "ccleaner.exe", "ccleaner64.exe" }, new[] { "CCleaner" }, StartupAdvice.CanDisable, "Мониторинг CCleaner в фоне пользы не приносит. Можно отключить.", "CCleaner background monitoring brings no benefit. Can be disabled."),
        R(new[] { "ituneshelper.exe" }, new[] { "iTunesHelper" }, StartupAdvice.CanDisable, "Помощник iTunes. Можно отключить — iTunes запустит его сам.", "iTunes helper. Can be disabled — iTunes starts it when needed."),
        R(new[] { "jusched.exe" }, new[] { "SunJavaUpdateSched", "Java Update" }, StartupAdvice.CanDisable, "Проверка обновлений Java. Можно отключить.", "Java update check. Can be disabled."),
        R(new[] { "onenotem.exe" }, new[] { "OneNote" }, StartupAdvice.CanDisable, "Быстрые заметки OneNote (Win+N). Можно отключить.", "OneNote quick notes (Win+N). Can be disabled."),
        R(new[] { "docker desktop.exe" }, new[] { "Docker Desktop" }, StartupAdvice.CanDisable, "Docker Desktop тяжёлый: запускайте его, когда нужен.", "Docker Desktop is heavy: start it when you need it."),
        R(new[] { "cortana.exe" }, new[] { "Cortana" }, StartupAdvice.CanDisable, "Cortana. Можно отключить.", "Cortana. Can be disabled."),
        R(Array.Empty<string>(), new[] { "Update Task", "Updater", "UpdateTask", "AutoUpdate" }, StartupAdvice.Optional,
            "Проверка обновлений программы. Если отключить, обновления придётся ставить самому.", "Program update check. If disabled, you'll have to install updates yourself."),
    };

    private static Rule R(string[] exe, string[] names, StartupAdvice advice, string text, string en) => new(exe, names, advice, text, en);

    /// <summary>Совет по программе: сначала по имени exe, потом по ключевым словам в имени записи.</summary>
    public static StartupTip Find(string name, string? exePath)
    {
        string exe = Path.GetFileName(exePath ?? "").ToLowerInvariant();
        if (exe.Length > 0)
        {
            foreach (var r in Rules)
                if (r.Exe.Contains(exe)) return r.Tip;
        }
        foreach (var r in Rules)
            foreach (var n in r.Names)
                if (name.Contains(n, StringComparison.OrdinalIgnoreCase)) return r.Tip;

        return new StartupTip(StartupAdvice.Optional, Lang.T(
            "Неизвестная программа. Посмотрите издателя и путь к файлу. Отключение обратимо — его можно вернуть в один клик.",
            "Unknown program. Check the publisher and file path. Disabling is reversible — one click brings it back."));
    }

    public static string AdviceText(StartupAdvice a) => a switch
    {
        StartupAdvice.CanDisable => Lang.T("Можно отключить", "Can be disabled"),
        StartupAdvice.Keep => Lang.T("Лучше оставить", "Better keep"),
        _ => Lang.T("На ваше усмотрение", "Your call"),
    };
}
