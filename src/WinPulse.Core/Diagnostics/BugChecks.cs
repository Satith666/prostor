using System.Globalization;

namespace WinPulse.Core.Diagnostics;

/// <summary>Вероятная причина синего экрана — от неё зависят советы.</summary>
public enum CrashCause { Driver, Graphics, Memory, Storage, Hardware, System, Unknown }

public sealed record BugCheckInfo(uint Code, string Name, string MeaningRu, CrashCause Cause, string MeaningEn = "")
{
    public string Meaning => Lang.English && MeaningEn.Length > 0 ? MeaningEn : MeaningRu;
    public string Hex => BugChecks.Hex(Code);
}

/// <summary>Коды синих экранов: название, смысл простыми словами и вероятная причина.</summary>
public static class BugChecks
{
    private static readonly Dictionary<uint, BugCheckInfo> Map = new[]
    {
        new BugCheckInfo(0x0A, "IRQL_NOT_LESS_OR_EQUAL", "драйвер обратился к памяти в неподходящий момент", CrashCause.Driver, "a driver accessed memory at the wrong moment"),
        new BugCheckInfo(0xD1, "DRIVER_IRQL_NOT_LESS_OR_EQUAL", "драйвер обратился к недопустимой памяти (часто сетевой, Wi-Fi или USB)", CrashCause.Driver, "a driver accessed invalid memory (often network, Wi-Fi or USB)"),
        new BugCheckInfo(0x1E, "KMODE_EXCEPTION_NOT_HANDLED", "необработанная ошибка в драйвере или ядре", CrashCause.Driver, "an unhandled error in a driver or the kernel"),
        new BugCheckInfo(0x3B, "SYSTEM_SERVICE_EXCEPTION", "ошибка в драйвере или системном компоненте (часто видеодрайвер или антивирус)", CrashCause.Driver, "an error in a driver or system component (often the graphics driver or antivirus)"),
        new BugCheckInfo(0x7E, "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "необработанная ошибка в системном потоке драйвера", CrashCause.Driver, "an unhandled error in a driver's system thread"),
        new BugCheckInfo(0x8E, "KERNEL_MODE_EXCEPTION_NOT_HANDLED", "необработанная ошибка в режиме ядра", CrashCause.Driver, "an unhandled kernel-mode error"),
        new BugCheckInfo(0x9F, "DRIVER_POWER_STATE_FAILURE", "драйвер не справился со сном или выходом из сна", CrashCause.Driver, "a driver failed going to sleep or waking up"),
        new BugCheckInfo(0x19, "BAD_POOL_HEADER", "драйвер повредил служебную память ядра", CrashCause.Driver, "a driver corrupted kernel pool memory"),
        new BugCheckInfo(0xC2, "BAD_POOL_CALLER", "драйвер неправильно работает с памятью ядра", CrashCause.Driver, "a driver misuses kernel memory"),
        new BugCheckInfo(0x13A, "KERNEL_MODE_HEAP_CORRUPTION", "драйвер повредил память ядра", CrashCause.Driver, "a driver corrupted kernel memory"),
        new BugCheckInfo(0xFC, "ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY", "драйвер попытался выполнить данные как код", CrashCause.Driver, "a driver tried to execute data as code"),
        new BugCheckInfo(0xC4, "DRIVER_VERIFIER_DETECTED_VIOLATION", "включена проверка драйверов (Driver Verifier), и она поймала нарушение", CrashCause.Driver, "Driver Verifier is on and caught a violation"),
        new BugCheckInfo(0xD5, "DRIVER_PAGE_FAULT_IN_FREED_SPECIAL_POOL", "драйвер обратился к уже освобождённой памяти", CrashCause.Driver, "a driver accessed memory that was already freed"),
        new BugCheckInfo(0x3D, "INTERRUPT_EXCEPTION_NOT_HANDLED", "ошибка при обработке прерывания", CrashCause.Driver, "an error while handling an interrupt"),
        new BugCheckInfo(0x116, "VIDEO_TDR_FAILURE", "видеодрайвер завис и не смог перезапуститься", CrashCause.Graphics, "the graphics driver hung and failed to recover"),
        new BugCheckInfo(0x117, "VIDEO_TDR_TIMEOUT_DETECTED", "видеодрайвер перестал отвечать", CrashCause.Graphics, "the graphics driver stopped responding"),
        new BugCheckInfo(0x119, "VIDEO_SCHEDULER_INTERNAL_ERROR", "сбой планировщика видеокарты", CrashCause.Graphics, "a GPU scheduler failure"),
        new BugCheckInfo(0x10E, "VIDEO_MEMORY_MANAGEMENT_INTERNAL", "сбой управления видеопамятью", CrashCause.Graphics, "a video memory management failure"),
        new BugCheckInfo(0x113, "VIDEO_DXGKRNL_FATAL_ERROR", "сбой графической подсистемы Windows", CrashCause.Graphics, "a Windows graphics subsystem failure"),
        new BugCheckInfo(0x1A, "MEMORY_MANAGEMENT", "серьёзная ошибка управления памятью — часто неисправная или нестабильная ОЗУ", CrashCause.Memory, "a serious memory management error — often faulty or unstable RAM"),
        new BugCheckInfo(0x4E, "PFN_LIST_CORRUPT", "повреждены служебные таблицы памяти — ОЗУ или драйвер", CrashCause.Memory, "memory tables are corrupted — RAM or a driver"),
        new BugCheckInfo(0x50, "PAGE_FAULT_IN_NONPAGED_AREA", "обращение к несуществующей памяти — драйвер, ОЗУ или повреждённые файлы", CrashCause.Memory, "access to non-existent memory — a driver, RAM or corrupted files"),
        new BugCheckInfo(0x139, "KERNEL_SECURITY_CHECK_FAILURE", "ядро обнаружило повреждение своих данных — драйвер или ОЗУ", CrashCause.Memory, "the kernel detected corruption of its data — a driver or RAM"),
        new BugCheckInfo(0x109, "CRITICAL_STRUCTURE_CORRUPTION", "повреждены важные данные ядра — драйвер, ОЗУ или программы виртуализации", CrashCause.Memory, "critical kernel data is corrupted — a driver, RAM or virtualization software"),
        new BugCheckInfo(0x7A, "KERNEL_DATA_INPAGE_ERROR", "не удалось прочитать данные с диска в память", CrashCause.Storage, "data couldn't be read from disk into memory"),
        new BugCheckInfo(0x77, "KERNEL_STACK_INPAGE_ERROR", "не удалось прочитать данные из файла подкачки", CrashCause.Storage, "data couldn't be read from the page file"),
        new BugCheckInfo(0x154, "UNEXPECTED_STORE_EXCEPTION", "сбой хранилища памяти — часто диск или его прошивка", CrashCause.Storage, "a memory store failure — often the disk or its firmware"),
        new BugCheckInfo(0x133, "DPC_WATCHDOG_VIOLATION", "драйвер слишком долго занимал процессор — часто контроллер диска или прошивка SSD", CrashCause.Storage, "a driver held the CPU too long — often the storage controller or SSD firmware"),
        new BugCheckInfo(0x24, "NTFS_FILE_SYSTEM", "ошибка файловой системы NTFS", CrashCause.Storage, "an NTFS file system error"),
        new BugCheckInfo(0x7B, "INACCESSIBLE_BOOT_DEVICE", "Windows не смогла обратиться к системному диску", CrashCause.Storage, "Windows couldn't access the system disk"),
        new BugCheckInfo(0xEF, "CRITICAL_PROCESS_DIED", "неожиданно завершился важный системный процесс", CrashCause.System, "a critical system process ended unexpectedly"),
        new BugCheckInfo(0xF4, "CRITICAL_OBJECT_TERMINATION", "завершился критически важный процесс — часто из-за сбоя диска", CrashCause.Storage, "a critical process terminated — often due to a disk failure"),
        new BugCheckInfo(0xC000021A, "STATUS_SYSTEM_PROCESS_TERMINATED", "отказал системный процесс — повреждены файлы Windows", CrashCause.System, "a system process failed — Windows files are corrupted"),
        new BugCheckInfo(0x124, "WHEA_UNCORRECTABLE_ERROR", "оборудование сообщило о неисправимой ошибке", CrashCause.Hardware, "the hardware reported an uncorrectable error"),
        new BugCheckInfo(0x101, "CLOCK_WATCHDOG_TIMEOUT", "одно из ядер процессора перестало отвечать", CrashCause.Hardware, "one of the CPU cores stopped responding"),
        new BugCheckInfo(0x7F, "UNEXPECTED_KERNEL_MODE_TRAP", "процессор столкнулся с ошибкой, которую ядро не ожидало — часто нестабильный разгон", CrashCause.Hardware, "the CPU hit an error the kernel didn't expect — often an unstable overclock"),
        new BugCheckInfo(0x9C, "MACHINE_CHECK_EXCEPTION", "процессор сообщил об аппаратной ошибке", CrashCause.Hardware, "the CPU reported a hardware error"),
    }.ToDictionary(b => b.Code);

    public static string Hex(uint code) => "0x" + code.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>Убирает старший флаг 0x10000000 (0x1000007E → 0x7E).</summary>
    public static uint Normalize(uint code) => (code & 0xF0000000) == 0x10000000 ? code & 0x0FFFFFFF : code;

    public static BugCheckInfo Describe(uint code)
    {
        code = Normalize(code);
        return Map.TryGetValue(code, out var info)
            ? info
            : new BugCheckInfo(code, "BUGCHECK " + Hex(code), "редкий код ошибки — нужен разбор файла дампа", CrashCause.Unknown, "a rare error code — the dump file needs analysis");
    }

    /// <summary>«0x0000009f (0x0000000000000003, …)» → 0x9F.</summary>
    public static uint? ParseParam1(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var s = text.Trim();
        int end = s.IndexOfAny(new[] { ' ', '(' });
        if (end > 0) s = s[..end];
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v) ? v : null;
    }

    public static string CauseText(CrashCause cause) => cause switch
    {
        CrashCause.Driver => Lang.T("драйвер", "a driver"),
        CrashCause.Graphics => Lang.T("видеокарта или видеодрайвер", "the GPU or graphics driver"),
        CrashCause.Memory => Lang.T("оперативная память или драйвер", "RAM or a driver"),
        CrashCause.Storage => Lang.T("диск или его контроллер", "the disk or its controller"),
        CrashCause.Hardware => Lang.T("оборудование: процессор, разгон, питание", "hardware: CPU, overclock, power"),
        CrashCause.System => Lang.T("повреждённые файлы Windows", "corrupted Windows files"),
        _ => Lang.T("не определена", "undetermined"),
    };

    public static IReadOnlyList<string> Advice(CrashCause cause) => cause switch
    {
        CrashCause.Graphics => new[]
        {
            Lang.T("Установите последний стабильный драйвер видеокарты с сайта NVIDIA, AMD или Intel (при установке выберите «Чистая установка»).", "Install the latest stable graphics driver from NVIDIA, AMD or Intel (choose Clean install during setup)."),
            Lang.T("Если видеокарта разогнана (MSI Afterburner и т. п.) — верните заводские частоты.", "If the GPU is overclocked (MSI Afterburner etc.), return it to stock clocks."),
            Lang.T("Проверьте температуру видеокарты под нагрузкой и подключение дополнительного питания.", "Check GPU temperature under load and the auxiliary power connection."),
        },
        CrashCause.Memory => new[]
        {
            Lang.T("Проверьте оперативную память: «Средство проверки памяти Windows» (кнопка ниже) или MemTest86.", "Test the RAM: Windows Memory Diagnostic (button below) or MemTest86."),
            Lang.T("Если в BIOS включён профиль XMP/EXPO или память разогнана — временно отключите и проверьте стабильность.", "If an XMP/EXPO profile is on in BIOS or the RAM is overclocked, turn it off for now and check stability."),
            Lang.T("Если ошибки начались после установки драйвера или программы — откатите их.", "If the errors began after installing a driver or program, roll it back."),
        },
        CrashCause.Storage => new[]
        {
            Lang.T("Проверьте здоровье диска (пункт «Здоровье дисков» на этой странице) или программой производителя SSD.", "Check disk health (the Disk health item on this page) or with the SSD maker's tool."),
            Lang.T("Обновите прошивку SSD и драйвер чипсета/контроллера хранения с сайта производителя.", "Update the SSD firmware and the chipset/storage controller driver from the vendor's site."),
            Lang.T("Для SATA-дисков проверьте кабель и разъём; запустите «chkdsk C: /scan» в командной строке от администратора.", "For SATA drives, check the cable and port; run “chkdsk C: /scan” in an administrator command prompt."),
        },
        CrashCause.Hardware => new[]
        {
            Lang.T("Снимите разгон и андервольт процессора и памяти (в BIOS — «Load Optimized Defaults»).", "Remove CPU and memory overclocks and undervolts (in BIOS: Load Optimized Defaults)."),
            Lang.T("Обновите BIOS материнской платы.", "Update the motherboard BIOS."),
            Lang.T("Проверьте температуры под нагрузкой и блок питания — нестабильное питание тоже вызывает такие ошибки.", "Check temperatures under load and the power supply — unstable power causes these errors too."),
        },
        CrashCause.System => new[]
        {
            Lang.T("Проверьте системные файлы: в командной строке от администратора выполните «sfc /scannow», затем «DISM /Online /Cleanup-Image /RestoreHealth».", "Check system files: in an administrator command prompt run “sfc /scannow”, then “DISM /Online /Cleanup-Image /RestoreHealth”."),
            Lang.T("Проверьте диск на ошибки — повреждённые файлы часто следствие сбоев диска.", "Check the disk for errors — corrupted files are often a result of disk failures."),
        },
        CrashCause.Driver => new[]
        {
            Lang.T("Обновите драйверы чипсета, сети (Wi-Fi/Ethernet), звука и USB с сайта производителя материнской платы или ноутбука.", "Update chipset, network (Wi-Fi/Ethernet), audio and USB drivers from the motherboard or laptop maker's site."),
            Lang.T("Вспомните, что устанавливали перед первым сбоем: антивирусы, VPN, программы подсветки и мониторинга часто ставят свои драйверы.", "Recall what you installed before the first crash: antivirus, VPN, RGB and monitoring software often install their own drivers."),
            Lang.T("Точное имя драйвера-виновника можно узнать, открыв файл дампа в WinDbg или BlueScreenView.", "You can find the exact culprit driver by opening the dump file in WinDbg or BlueScreenView."),
        },
        _ => new[]
        {
            Lang.T("Откройте файл дампа в WinDbg или BlueScreenView, чтобы узнать драйвер-виновник.", "Open the dump file in WinDbg or BlueScreenView to find the culprit driver."),
            Lang.T("Обновите драйверы чипсета и видеокарты, снимите разгон.", "Update chipset and graphics drivers, remove overclocks."),
        },
    };
}

/// <summary>Коды проблем Диспетчера устройств (CM_PROB_*).</summary>
public static class DeviceProblemCodes
{
    public static string Describe(int code) => code switch
    {
        1 => Lang.T("устройство неправильно настроено", "the device is not configured correctly"),
        3 => Lang.T("драйвер повреждён или не хватает ресурсов", "the driver is corrupted or resources are short"),
        10 => Lang.T("устройство не может запуститься", "the device cannot start"),
        12 => Lang.T("не хватает свободных ресурсов (конфликт)", "not enough free resources (conflict)"),
        14 => Lang.T("нужна перезагрузка", "a restart is needed"),
        18 => Lang.T("драйверы нужно переустановить", "the drivers need to be reinstalled"),
        19 => Lang.T("повреждены настройки устройства в реестре", "the device's registry settings are corrupted"),
        21 => Lang.T("Windows удаляет устройство", "Windows is removing the device"),
        22 => Lang.T("устройство отключено", "the device is disabled"),
        24 => Lang.T("устройство отсутствует или работает неправильно", "the device is missing or not working properly"),
        28 => Lang.T("драйверы не установлены", "drivers are not installed"),
        29 => Lang.T("устройство отключено в BIOS/прошивке", "the device is disabled in BIOS/firmware"),
        31 => Lang.T("Windows не может загрузить драйвер", "Windows can't load the driver"),
        32 => Lang.T("служба драйвера отключена", "the driver service is disabled"),
        37 => Lang.T("драйвер вернул ошибку при запуске", "the driver returned an error on start"),
        38 => Lang.T("предыдущий экземпляр драйвера ещё в памяти — нужна перезагрузка", "a previous instance of the driver is still in memory — a restart is needed"),
        39 => Lang.T("драйвер повреждён или отсутствует", "the driver is corrupted or missing"),
        40 => Lang.T("повреждены сведения о службе драйвера", "the driver service information is corrupted"),
        41 => Lang.T("драйвер загружен, но устройство не найдено", "the driver is loaded but the device wasn't found"),
        43 => Lang.T("Windows остановила устройство: оно сообщило о проблеме", "Windows stopped the device: it reported a problem"),
        47 => Lang.T("устройство подготовлено к безопасному извлечению", "the device is prepared for safe removal"),
        48 => Lang.T("драйвер заблокирован как несовместимый", "the driver is blocked as incompatible"),
        52 => Lang.T("у драйвера нет подписи или она неверна", "the driver is unsigned or its signature is invalid"),
        _ => Lang.T("код ошибки ", "error code ") + code,
    };
}
