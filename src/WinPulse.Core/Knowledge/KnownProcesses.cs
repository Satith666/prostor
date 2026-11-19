namespace WinPulse.Core.Knowledge;

/// <summary>
/// Понятное имя и подсказка для известного процесса. IsSystem — часть Windows, завершать нельзя.
/// Тексты хранятся на двух языках, язык выбирается при обращении (его можно переключить на лету).
/// </summary>
public sealed record KnownProcess(string NameRu, string NameEn, string HintRu, string HintEn, bool IsSystem)
{
    public string DisplayName => Lang.English ? NameEn : NameRu;
    public string Hint => Lang.English ? HintEn : HintRu;
}

public static class KnownProcesses
{
    private static KnownProcess P(string nameRu, string nameEn, string hintRu, string hintEn, bool system) =>
        new(nameRu, nameEn, hintRu, hintEn, system);

    private static readonly Dictionary<string, KnownProcess> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["System"] = P("Ядро Windows (System)", "Windows kernel (System)",
            "Ядро и драйверы. Если нагрузка высокая постоянно, чаще всего виноват драйвер устройства.",
            "The kernel and drivers. If the load stays high, a device driver is usually to blame.", true),
        ["Registry"] = P("Реестр Windows", "Windows Registry",
            "Хранит реестр в памяти. Нагрузки почти не создаёт.",
            "Keeps the registry in memory. Creates almost no load.", true),
        ["Memory Compression"] = P("Сжатие памяти", "Memory Compression",
            "Windows сжимает редко используемые данные вместо записи на диск. Это нормально и экономит память.",
            "Windows compresses rarely used data instead of writing it to disk. This is normal and saves memory.", true),
        ["Secure System"] = P("Защищённая система (VBS)", "Secure System (VBS)",
            "Работает, когда включены изоляция ядра или Device Guard.",
            "Runs when Core Isolation or Device Guard is on.", true),
        ["smss.exe"] = P("Диспетчер сеансов", "Session Manager", "Системный процесс запуска Windows.", "System process that starts Windows.", true),
        ["csrss.exe"] = P("Среда выполнения клиент-сервер", "Client Server Runtime", "Системный процесс, без него Windows не работает.", "System process; Windows can't run without it.", true),
        ["wininit.exe"] = P("Запуск Windows", "Windows Start-Up", "Системный процесс.", "System process.", true),
        ["winlogon.exe"] = P("Вход в Windows", "Windows Logon", "Отвечает за вход и экран блокировки.", "Handles sign-in and the lock screen.", true),
        ["services.exe"] = P("Диспетчер служб", "Service Control Manager", "Запускает и останавливает службы Windows.", "Starts and stops Windows services.", true),
        ["lsass.exe"] = P("Локальная безопасность (LSASS)", "Local Security Authority (LSASS)", "Проверяет пароли и права. Завершать нельзя.", "Checks passwords and permissions. Never end it.", true),
        ["lsaiso.exe"] = P("Изолированная LSA", "Isolated LSA", "Часть защиты учётных данных.", "Part of Credential Guard.", true),
        ["fontdrvhost.exe"] = P("Драйвер шрифтов", "Font Driver Host", "Отрисовка шрифтов.", "Renders fonts.", true),
        ["dwm.exe"] = P("Диспетчер окон рабочего стола", "Desktop Window Manager",
            "Рисует окна и эффекты. Нагрузка растёт при высокой частоте монитора, видео и прозрачности.",
            "Draws windows and effects. Load grows with high refresh rates, video and transparency.", true),
        ["explorer.exe"] = P("Проводник", "File Explorer", "Рабочий стол, панель задач и окна папок.", "Desktop, taskbar and folder windows.", false),
        ["sihost.exe"] = P("Узел оболочки", "Shell Infrastructure Host", "Часть интерфейса Windows.", "Part of the Windows interface.", true),
        ["ctfmon.exe"] = P("Ввод текста (CTF)", "Text input (CTF)", "Раскладки клавиатуры и рукописный ввод.", "Keyboard layouts and handwriting input.", true),
        ["RuntimeBroker.exe"] = P("Посредник приложений", "Runtime Broker", "Проверяет разрешения приложений из Microsoft Store.", "Checks permissions of Microsoft Store apps.", true),
        ["SearchHost.exe"] = P("Поиск Windows", "Windows Search", "Окно поиска в меню «Пуск».", "The search box in the Start menu.", true),
        ["SearchApp.exe"] = P("Поиск Windows", "Windows Search", "Окно поиска в меню «Пуск».", "The search box in the Start menu.", true),
        ["SearchIndexer.exe"] = P("Индексатор поиска", "Search Indexer",
            "Составляет индекс файлов для быстрого поиска. После установки программ или копирования файлов нагрузка — нормальна.",
            "Indexes files for fast search. Load after installing programs or copying files is normal.", true),
        ["StartMenuExperienceHost.exe"] = P("Меню «Пуск»", "Start menu", "Интерфейс меню «Пуск».", "The Start menu interface.", true),
        ["ShellExperienceHost.exe"] = P("Интерфейс оболочки", "Shell Experience Host", "Центр уведомлений, часы, календарь.", "Notification center, clock, calendar.", true),
        ["TextInputHost.exe"] = P("Сенсорная клавиатура и эмодзи", "Touch keyboard and emoji", "Панель ввода текста.", "The text input panel.", true),
        ["MsMpEng.exe"] = P("Антивирус Microsoft Defender", "Microsoft Defender Antivirus",
            "Проверяет файлы. Пики — при установке программ, обновлении баз и плановой проверке.",
            "Scans files. Spikes happen when installing programs, updating definitions and during scheduled scans.", true),
        ["MpDefenderCoreService.exe"] = P("Служба Microsoft Defender", "Microsoft Defender Core Service", "Часть антивируса.", "Part of the antivirus.", true),
        ["NisSrv.exe"] = P("Защита сети Defender", "Defender Network Inspection", "Часть антивируса.", "Part of the antivirus.", true),
        ["TiWorker.exe"] = P("Установщик модулей Windows", "Windows Modules Installer",
            "Устанавливает обновления. Высокая нагрузка временная — дождитесь окончания.",
            "Installs updates. The high load is temporary — wait for it to finish.", true),
        ["TrustedInstaller.exe"] = P("Установщик Windows", "Windows Installer", "Устанавливает обновления и компоненты.", "Installs updates and components.", true),
        ["MoUsoCoreWorker.exe"] = P("Обновление Windows", "Windows Update", "Проверяет и скачивает обновления.", "Checks for and downloads updates.", true),
        ["CompatTelRunner.exe"] = P("Проверка совместимости", "Compatibility Telemetry",
            "Собирает сведения о совместимости программ. Запускается по расписанию.",
            "Collects program compatibility data. Runs on a schedule.", true),
        ["audiodg.exe"] = P("Аудиодвижок Windows", "Windows Audio Device Graph",
            "Обработка звука. Высокая нагрузка обычно из-за звуковых эффектов или «улучшений» в драйвере.",
            "Audio processing. High load usually comes from sound effects or 'enhancements' in the driver.", true),
        ["WmiPrvSE.exe"] = P("Поставщик WMI", "WMI Provider Host",
            "Отвечает программам на вопросы о системе. Нагрузку создаёт не он, а программа, которая часто спрашивает.",
            "Answers programs' questions about the system. The load comes from the program that asks too often, not from it.", true),
        ["spoolsv.exe"] = P("Диспетчер печати", "Print Spooler", "Очередь печати.", "The print queue.", true),
        ["dllhost.exe"] = P("Суррогат COM", "COM Surrogate", "Выполняет компоненты других программ, например миниатюры.", "Runs components for other programs, such as thumbnails.", true),
        ["conhost.exe"] = P("Окно консоли", "Console Window Host", "Обслуживает окна командной строки.", "Serves command-line windows.", true),
        ["taskhostw.exe"] = P("Узел задач Windows", "Host Process for Windows Tasks", "Выполняет фоновые задачи.", "Runs background tasks.", true),
        ["svchost.exe"] = P("Служба Windows", "Windows service", "Контейнер для служб Windows.", "A container for Windows services.", true),
        ["msedgewebview2.exe"] = P("Microsoft Edge WebView2", "Microsoft Edge WebView2", "Встроенный браузер, на котором сделаны многие программы.", "Embedded browser many programs are built on.", false),
        ["nvcontainer.exe"] = P("NVIDIA Container", "NVIDIA Container", "Фоновые службы драйвера NVIDIA.", "Background services of the NVIDIA driver.", false),
        ["NVDisplay.Container.exe"] = P("NVIDIA Display Container", "NVIDIA Display Container", "Панель управления и службы драйвера NVIDIA.", "NVIDIA Control Panel and driver services.", false),
        ["NVIDIA Overlay.exe"] = P("Оверлей NVIDIA", "NVIDIA Overlay", "Оверлей NVIDIA App/GeForce Experience. Может нагружать видеокарту в играх.", "NVIDIA App/GeForce Experience overlay. Can load the GPU in games.", false),
        ["NVIDIA Share.exe"] = P("Оверлей NVIDIA", "NVIDIA Overlay", "Оверлей GeForce Experience. Может нагружать видеокарту в играх.", "GeForce Experience overlay. Can load the GPU in games.", false),
        ["RadeonSoftware.exe"] = P("AMD Software", "AMD Software", "Панель драйвера AMD.", "AMD driver panel.", false),
        ["wallpaper32.exe"] = P("Wallpaper Engine", "Wallpaper Engine", "Живые обои. Нагружают видеокарту, если не стоят на паузе во время игры.", "Live wallpapers. They load the GPU unless paused while you play.", false),
        ["wallpaper64.exe"] = P("Wallpaper Engine", "Wallpaper Engine", "Живые обои. Нагружают видеокарту, если не стоят на паузе во время игры.", "Live wallpapers. They load the GPU unless paused while you play.", false),
        ["Lively.exe"] = P("Lively Wallpaper", "Lively Wallpaper", "Живые обои. Нагружают видеокарту во время игры.", "Live wallpapers. They load the GPU while you play.", false),
        ["GameBar.exe"] = P("Игровая панель Xbox", "Xbox Game Bar", "Оверлей Win+G.", "The Win+G overlay.", false),
        ["RTSS.exe"] = P("RivaTuner Statistics Server", "RivaTuner Statistics Server", "Оверлей и ограничитель кадров.", "Overlay and frame limiter.", false),
        ["steamwebhelper.exe"] = P("Steam (встроенный браузер)", "Steam (web helper)", "Интерфейс Steam.", "The Steam interface.", false),
    };

    public static KnownProcess? Find(string name) => Map.TryGetValue(name, out var k) ? k : null;
}
