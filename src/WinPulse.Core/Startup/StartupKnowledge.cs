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
    private sealed record Rule(string[] Exe, string[] Names, StartupAdvice Advice, string Text);

    private static readonly Rule[] Rules =
    {
        // Лучше оставить
        R(new[] { "securityhealthsystray.exe" }, new[] { "SecurityHealth" }, StartupAdvice.Keep,
            "Значок «Безопасность Windows» в трее. Защиту не ускоряет и не замедляет — оставьте."),
        R(new[] { "rtkauduservice64.exe", "ravcpl64.exe", "rtkngui64.exe", "ravbg64.exe" }, new[] { "Realtek", "RtkAudUService" }, StartupAdvice.Keep,
            "Служебная программа звуковой карты Realtek: определяет подключение наушников и микрофона."),
        R(new[] { "syntpenh.exe", "etdctrl.exe", "elanfilter.exe" }, new[] { "Synaptics", "ETD", "ELAN" }, StartupAdvice.Keep,
            "Драйвер тачпада: без него перестанут работать жесты и прокрутка двумя пальцами."),
        R(new[] { "radeonsoftware.exe", "cncmd.exe", "amdrsserv.exe" }, new[] { "AMD Software", "Radeon" }, StartupAdvice.Keep,
            "Панель драйвера AMD: применяет профили игр, FreeSync и ограничение кадров."),
        R(new[] { "avp.exe", "avpui.exe", "avastui.exe", "egui.exe", "mbam.exe", "mbamtray.exe", "bdagent.exe", "nortonui.exe", "drweb.exe" },
            new[] { "Kaspersky", "Avast", "ESET", "Malwarebytes", "Bitdefender", "Norton", "Dr.Web" }, StartupAdvice.Keep,
            "Антивирус должен запускаться вместе с Windows."),
        R(new[] { "rtss.exe", "msiafterburner.exe" }, new[] { "RivaTuner", "Afterburner" }, StartupAdvice.Optional,
            "Если ограничиваете ими FPS, разгоняете видеокарту или смотрите температуры в играх — оставьте; иначе можно отключить."),

        // На ваше усмотрение
        R(new[] { "onedrive.exe" }, new[] { "OneDrive" }, StartupAdvice.Optional,
            "Синхронизирует файлы с облаком OneDrive. Если не пользуетесь — отключите, файлы останутся на компьютере."),
        R(new[] { "googledrivefs.exe", "dropbox.exe", "yandexdisk2.exe", "yandexdisk.exe" }, new[] { "Google Drive", "Dropbox", "Yandex.Disk", "Яндекс Диск" }, StartupAdvice.Optional,
            "Синхронизация облачного диска. Если отключить — файлы перестанут синхронизироваться, пока не запустите программу."),
        R(new[] { "telegram.exe" }, new[] { "Telegram" }, StartupAdvice.Optional, "Мессенджер: оставьте, если нужны уведомления сразу после включения."),
        R(new[] { "whatsapp.exe" }, new[] { "WhatsApp" }, StartupAdvice.Optional, "Мессенджер: оставьте, если нужны уведомления сразу после включения."),
        R(new[] { "viber.exe" }, new[] { "Viber" }, StartupAdvice.Optional, "Мессенджер: оставьте, если нужны уведомления сразу после включения."),
        R(new[] { "ms-teams.exe", "teams.exe" }, new[] { "Teams" }, StartupAdvice.Optional,
            "Рабочий мессенджер. Если пользуетесь им каждый день — оставьте; иначе запускайте вручную (он тяжёлый: 300–800 МБ памяти)."),
        R(new[] { "slack.exe" }, new[] { "Slack" }, StartupAdvice.Optional, "Рабочий мессенджер: оставьте, если пользуетесь им каждый день."),
        R(new[] { "qbittorrent.exe", "utorrent.exe", "bittorrent.exe" }, new[] { "qBittorrent", "uTorrent", "BitTorrent" }, StartupAdvice.Optional,
            "Торрент-клиент. Если отключить — загрузки и раздачи продолжатся только после ручного запуска."),
        R(new[] { "lghub.exe", "lghub_agent.exe", "razercentral.exe", "razer synapse 3.exe", "icue.exe", "steelseriesgg.exe", "armourycrate.exe", "msi center.exe" },
            new[] { "G HUB", "Razer", "iCUE", "Corsair", "SteelSeries", "Armoury", "MSI Center" }, StartupAdvice.Optional,
            "Программа мыши, клавиатуры или подсветки. Без неё могут сброситься подсветка, макросы и чувствительность."),
        R(new[] { "wallpaper32.exe", "wallpaper64.exe", "lively.exe" }, new[] { "Wallpaper Engine", "Lively" }, StartupAdvice.Optional,
            "Живые обои. Нагружают видеокарту — если оставляете, настройте паузу во время игр."),
        R(new[] { "punto.exe", "ps64ldr.exe" }, new[] { "Punto" }, StartupAdvice.Optional, "Автопереключение раскладки. Оставьте, если пользуетесь."),
        R(new[] { "lightshot.exe" }, new[] { "Lightshot" }, StartupAdvice.Optional, "Программа скриншотов. В Windows есть встроенная: Win+Shift+S."),
        R(new[] { "googleupdate.exe" }, new[] { "GoogleUpdate", "Google Updater" }, StartupAdvice.Optional,
            "Обновляет программы Google. Если отключить, браузер будет обновляться при своём запуске."),
        R(new[] { "adobearm.exe", "acrotray.exe" }, new[] { "Adobe Acrobat", "AdobeARM", "Acrobat" }, StartupAdvice.Optional, "Обновление Adobe Acrobat Reader. Можно отключить."),
        R(new[] { "nvidia app.exe", "nvidia share.exe", "nvtray.exe", "nvbackend.exe" }, new[] { "NVIDIA" }, StartupAdvice.Optional,
            "Приложение NVIDIA: оверлей, запись, проверка драйверов. Сам драйвер работает и без него."),
        R(new[] { "igfxtray.exe", "igfxem.exe", "igfxhk.exe" }, new[] { "Intel Graphics", "igfx" }, StartupAdvice.Optional,
            "Значок и горячие клавиши графики Intel. Можно отключить — драйвер работает без этого."),
        R(new[] { "figma_agent.exe" }, new[] { "Figma Agent" }, StartupAdvice.Optional, "Нужен Figma в браузере для локальных шрифтов."),
        R(Array.Empty<string>(), new[] { "VPN", "WireGuard", "OpenVPN" }, StartupAdvice.Optional, "VPN-клиент. Оставьте, если подключаетесь сразу после включения."),

        // Можно отключить
        R(new[] { "discord.exe" }, new[] { "Discord" }, StartupAdvice.CanDisable, "Запускайте Discord, когда он нужен. Отключение ускоряет загрузку и экономит 200–400 МБ памяти."),
        R(new[] { "steam.exe" }, new[] { "Steam" }, StartupAdvice.CanDisable, "Steam можно запускать перед игрой. Загрузки обновлений начнутся при его запуске."),
        R(new[] { "epicgameslauncher.exe" }, new[] { "EpicGamesLauncher", "Epic Games" }, StartupAdvice.CanDisable, "Лаунчер Epic Games можно запускать вручную."),
        R(new[] { "eadesktop.exe", "origin.exe" }, new[] { "EA Desktop", "EADM", "Origin" }, StartupAdvice.CanDisable, "Лаунчер EA можно запускать вручную."),
        R(new[] { "battle.net.exe", "battle.net launcher.exe" }, new[] { "Battle.net" }, StartupAdvice.CanDisable, "Лаунчер Battle.net можно запускать вручную."),
        R(new[] { "upc.exe", "ubisoftconnect.exe", "ubisoftgamelauncher.exe" }, new[] { "Ubisoft" }, StartupAdvice.CanDisable, "Лаунчер Ubisoft можно запускать вручную."),
        R(new[] { "galaxyclient.exe" }, new[] { "GOG Galaxy", "GalaxyClient" }, StartupAdvice.CanDisable, "Лаунчер GOG можно запускать вручную."),
        R(new[] { "spotify.exe" }, new[] { "Spotify" }, StartupAdvice.CanDisable, "Spotify можно запускать, когда хочется музыки."),
        R(new[] { "skype.exe" }, new[] { "Skype" }, StartupAdvice.CanDisable, "Skype можно запускать вручную."),
        R(new[] { "zoom.exe" }, new[] { "Zoom" }, StartupAdvice.CanDisable, "Zoom достаточно запускать перед встречей."),
        R(new[] { "msedge.exe" }, new[] { "MicrosoftEdgeAutoLaunch", "Microsoft Edge" }, StartupAdvice.CanDisable,
            "«Ускорение запуска» Edge держит браузер в памяти с самого включения. Если вы не пользуетесь Edge постоянно — отключите."),
        R(new[] { "chrome.exe" }, new[] { "GoogleChromeAutoLaunch", "Chrome" }, StartupAdvice.CanDisable, "Фоновый запуск Chrome держит браузер в памяти. Можно отключить."),
        R(new[] { "browser.exe" }, new[] { "Yandex", "Яндекс" }, StartupAdvice.CanDisable, "Автозапуск Яндекс Браузера. Можно отключить."),
        R(new[] { "creative cloud.exe", "ccxprocess.exe", "adobegcclient.exe", "coresync.exe" }, new[] { "Creative Cloud", "CCXProcess", "AdobeGC" }, StartupAdvice.CanDisable,
            "Фоновые процессы Adobe Creative Cloud. Запустятся сами, когда откроете Photoshop или другую программу Adobe."),
        R(new[] { "ccleaner.exe", "ccleaner64.exe" }, new[] { "CCleaner" }, StartupAdvice.CanDisable, "Мониторинг CCleaner в фоне пользы не приносит. Можно отключить."),
        R(new[] { "ituneshelper.exe" }, new[] { "iTunesHelper" }, StartupAdvice.CanDisable, "Помощник iTunes. Можно отключить — iTunes запустит его сам."),
        R(new[] { "jusched.exe" }, new[] { "SunJavaUpdateSched", "Java Update" }, StartupAdvice.CanDisable, "Проверка обновлений Java. Можно отключить."),
        R(new[] { "onenotem.exe" }, new[] { "OneNote" }, StartupAdvice.CanDisable, "Быстрые заметки OneNote (Win+N). Можно отключить."),
        R(new[] { "docker desktop.exe" }, new[] { "Docker Desktop" }, StartupAdvice.CanDisable, "Docker Desktop тяжёлый: запускайте его, когда нужен."),
        R(new[] { "cortana.exe" }, new[] { "Cortana" }, StartupAdvice.CanDisable, "Cortana. Можно отключить."),
        R(Array.Empty<string>(), new[] { "Update Task", "Updater", "UpdateTask", "AutoUpdate" }, StartupAdvice.Optional,
            "Проверка обновлений программы. Если отключить, обновления придётся ставить самому."),
    };

    private static Rule R(string[] exe, string[] names, StartupAdvice advice, string text) => new(exe, names, advice, text);

    /// <summary>Совет по программе: сначала по имени exe, потом по ключевым словам в имени записи.</summary>
    public static StartupTip Find(string name, string? exePath)
    {
        string exe = Path.GetFileName(exePath ?? "").ToLowerInvariant();
        if (exe.Length > 0)
        {
            foreach (var r in Rules)
                if (r.Exe.Contains(exe)) return new StartupTip(r.Advice, r.Text);
        }
        foreach (var r in Rules)
            foreach (var n in r.Names)
                if (name.Contains(n, StringComparison.OrdinalIgnoreCase)) return new StartupTip(r.Advice, r.Text);

        return new StartupTip(StartupAdvice.Optional,
            "Неизвестная программа. Посмотрите издателя и путь к файлу. Отключение обратимо — его можно вернуть в один клик.");
    }

    public static string AdviceText(StartupAdvice a) => a switch
    {
        StartupAdvice.CanDisable => Lang.T("Можно отключить", "Can be disabled"),
        StartupAdvice.Keep => Lang.T("Лучше оставить", "Better keep"),
        _ => Lang.T("На ваше усмотрение", "Your call"),
    };
}
