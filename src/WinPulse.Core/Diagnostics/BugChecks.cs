using System.Globalization;

namespace WinPulse.Core.Diagnostics;

/// <summary>Вероятная причина синего экрана — от неё зависят советы.</summary>
public enum CrashCause { Driver, Graphics, Memory, Storage, Hardware, System, Unknown }

public sealed record BugCheckInfo(uint Code, string Name, string Meaning, CrashCause Cause)
{
    public string Hex => BugChecks.Hex(Code);
}

/// <summary>Коды синих экранов: название, смысл простыми словами и вероятная причина.</summary>
public static class BugChecks
{
    private static readonly Dictionary<uint, BugCheckInfo> Map = new[]
    {
        new BugCheckInfo(0x0A, "IRQL_NOT_LESS_OR_EQUAL", "драйвер обратился к памяти в неподходящий момент", CrashCause.Driver),
        new BugCheckInfo(0xD1, "DRIVER_IRQL_NOT_LESS_OR_EQUAL", "драйвер обратился к недопустимой памяти (часто сетевой, Wi-Fi или USB)", CrashCause.Driver),
        new BugCheckInfo(0x1E, "KMODE_EXCEPTION_NOT_HANDLED", "необработанная ошибка в драйвере или ядре", CrashCause.Driver),
        new BugCheckInfo(0x3B, "SYSTEM_SERVICE_EXCEPTION", "ошибка в драйвере или системном компоненте (часто видеодрайвер или антивирус)", CrashCause.Driver),
        new BugCheckInfo(0x7E, "SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "необработанная ошибка в системном потоке драйвера", CrashCause.Driver),
        new BugCheckInfo(0x8E, "KERNEL_MODE_EXCEPTION_NOT_HANDLED", "необработанная ошибка в режиме ядра", CrashCause.Driver),
        new BugCheckInfo(0x9F, "DRIVER_POWER_STATE_FAILURE", "драйвер не справился со сном или выходом из сна", CrashCause.Driver),
        new BugCheckInfo(0x19, "BAD_POOL_HEADER", "драйвер повредил служебную память ядра", CrashCause.Driver),
        new BugCheckInfo(0xC2, "BAD_POOL_CALLER", "драйвер неправильно работает с памятью ядра", CrashCause.Driver),
        new BugCheckInfo(0x13A, "KERNEL_MODE_HEAP_CORRUPTION", "драйвер повредил память ядра", CrashCause.Driver),
        new BugCheckInfo(0xFC, "ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY", "драйвер попытался выполнить данные как код", CrashCause.Driver),
        new BugCheckInfo(0xC4, "DRIVER_VERIFIER_DETECTED_VIOLATION", "включена проверка драйверов (Driver Verifier), и она поймала нарушение", CrashCause.Driver),
        new BugCheckInfo(0xD5, "DRIVER_PAGE_FAULT_IN_FREED_SPECIAL_POOL", "драйвер обратился к уже освобождённой памяти", CrashCause.Driver),
        new BugCheckInfo(0x3D, "INTERRUPT_EXCEPTION_NOT_HANDLED", "ошибка при обработке прерывания", CrashCause.Driver),
        new BugCheckInfo(0x116, "VIDEO_TDR_FAILURE", "видеодрайвер завис и не смог перезапуститься", CrashCause.Graphics),
        new BugCheckInfo(0x117, "VIDEO_TDR_TIMEOUT_DETECTED", "видеодрайвер перестал отвечать", CrashCause.Graphics),
        new BugCheckInfo(0x119, "VIDEO_SCHEDULER_INTERNAL_ERROR", "сбой планировщика видеокарты", CrashCause.Graphics),
        new BugCheckInfo(0x10E, "VIDEO_MEMORY_MANAGEMENT_INTERNAL", "сбой управления видеопамятью", CrashCause.Graphics),
        new BugCheckInfo(0x113, "VIDEO_DXGKRNL_FATAL_ERROR", "сбой графической подсистемы Windows", CrashCause.Graphics),
        new BugCheckInfo(0x1A, "MEMORY_MANAGEMENT", "серьёзная ошибка управления памятью — часто неисправная или нестабильная ОЗУ", CrashCause.Memory),
        new BugCheckInfo(0x4E, "PFN_LIST_CORRUPT", "повреждены служебные таблицы памяти — ОЗУ или драйвер", CrashCause.Memory),
        new BugCheckInfo(0x50, "PAGE_FAULT_IN_NONPAGED_AREA", "обращение к несуществующей памяти — драйвер, ОЗУ или повреждённые файлы", CrashCause.Memory),
        new BugCheckInfo(0x139, "KERNEL_SECURITY_CHECK_FAILURE", "ядро обнаружило повреждение своих данных — драйвер или ОЗУ", CrashCause.Memory),
        new BugCheckInfo(0x109, "CRITICAL_STRUCTURE_CORRUPTION", "повреждены важные данные ядра — драйвер, ОЗУ или программы виртуализации", CrashCause.Memory),
        new BugCheckInfo(0x7A, "KERNEL_DATA_INPAGE_ERROR", "не удалось прочитать данные с диска в память", CrashCause.Storage),
        new BugCheckInfo(0x77, "KERNEL_STACK_INPAGE_ERROR", "не удалось прочитать данные из файла подкачки", CrashCause.Storage),
        new BugCheckInfo(0x154, "UNEXPECTED_STORE_EXCEPTION", "сбой хранилища памяти — часто диск или его прошивка", CrashCause.Storage),
        new BugCheckInfo(0x133, "DPC_WATCHDOG_VIOLATION", "драйвер слишком долго занимал процессор — часто контроллер диска или прошивка SSD", CrashCause.Storage),
        new BugCheckInfo(0x24, "NTFS_FILE_SYSTEM", "ошибка файловой системы NTFS", CrashCause.Storage),
        new BugCheckInfo(0x7B, "INACCESSIBLE_BOOT_DEVICE", "Windows не смогла обратиться к системному диску", CrashCause.Storage),
        new BugCheckInfo(0xEF, "CRITICAL_PROCESS_DIED", "неожиданно завершился важный системный процесс", CrashCause.System),
        new BugCheckInfo(0xF4, "CRITICAL_OBJECT_TERMINATION", "завершился критически важный процесс — часто из-за сбоя диска", CrashCause.Storage),
        new BugCheckInfo(0xC000021A, "STATUS_SYSTEM_PROCESS_TERMINATED", "отказал системный процесс — повреждены файлы Windows", CrashCause.System),
        new BugCheckInfo(0x124, "WHEA_UNCORRECTABLE_ERROR", "оборудование сообщило о неисправимой ошибке", CrashCause.Hardware),
        new BugCheckInfo(0x101, "CLOCK_WATCHDOG_TIMEOUT", "одно из ядер процессора перестало отвечать", CrashCause.Hardware),
        new BugCheckInfo(0x7F, "UNEXPECTED_KERNEL_MODE_TRAP", "процессор столкнулся с ошибкой, которую ядро не ожидало — часто нестабильный разгон", CrashCause.Hardware),
        new BugCheckInfo(0x9C, "MACHINE_CHECK_EXCEPTION", "процессор сообщил об аппаратной ошибке", CrashCause.Hardware),
    }.ToDictionary(b => b.Code);

    public static string Hex(uint code) => "0x" + code.ToString("X8", CultureInfo.InvariantCulture);

    /// <summary>Убирает старший флаг 0x10000000 (0x1000007E → 0x7E).</summary>
    public static uint Normalize(uint code) => (code & 0xF0000000) == 0x10000000 ? code & 0x0FFFFFFF : code;

    public static BugCheckInfo Describe(uint code)
    {
        code = Normalize(code);
        return Map.TryGetValue(code, out var info)
            ? info
            : new BugCheckInfo(code, "BUGCHECK " + Hex(code), "редкий код ошибки — нужен разбор файла дампа", CrashCause.Unknown);
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
        CrashCause.Driver => "драйвер",
        CrashCause.Graphics => "видеокарта или видеодрайвер",
        CrashCause.Memory => "оперативная память или драйвер",
        CrashCause.Storage => "диск или его контроллер",
        CrashCause.Hardware => "оборудование: процессор, разгон, питание",
        CrashCause.System => "повреждённые файлы Windows",
        _ => "не определена",
    };

    public static IReadOnlyList<string> Advice(CrashCause cause) => cause switch
    {
        CrashCause.Graphics => new[]
        {
            "Установите последний стабильный драйвер видеокарты с сайта NVIDIA, AMD или Intel (при установке выберите «Чистая установка»).",
            "Если видеокарта разогнана (MSI Afterburner и т. п.) — верните заводские частоты.",
            "Проверьте температуру видеокарты под нагрузкой и подключение дополнительного питания.",
        },
        CrashCause.Memory => new[]
        {
            "Проверьте оперативную память: «Средство проверки памяти Windows» (кнопка ниже) или MemTest86.",
            "Если в BIOS включён профиль XMP/EXPO или память разогнана — временно отключите и проверьте стабильность.",
            "Если ошибки начались после установки драйвера или программы — откатите их.",
        },
        CrashCause.Storage => new[]
        {
            "Проверьте здоровье диска (пункт «Здоровье дисков» на этой странице) или программой производителя SSD.",
            "Обновите прошивку SSD и драйвер чипсета/контроллера хранения с сайта производителя.",
            "Для SATA-дисков проверьте кабель и разъём; запустите «chkdsk C: /scan» в командной строке от администратора.",
        },
        CrashCause.Hardware => new[]
        {
            "Снимите разгон и андервольт процессора и памяти (в BIOS — «Load Optimized Defaults»).",
            "Обновите BIOS материнской платы.",
            "Проверьте температуры под нагрузкой и блок питания — нестабильное питание тоже вызывает такие ошибки.",
        },
        CrashCause.System => new[]
        {
            "Проверьте системные файлы: в командной строке от администратора выполните «sfc /scannow», затем «DISM /Online /Cleanup-Image /RestoreHealth».",
            "Проверьте диск на ошибки — повреждённые файлы часто следствие сбоев диска.",
        },
        CrashCause.Driver => new[]
        {
            "Обновите драйверы чипсета, сети (Wi-Fi/Ethernet), звука и USB с сайта производителя материнской платы или ноутбука.",
            "Вспомните, что устанавливали перед первым сбоем: антивирусы, VPN, программы подсветки и мониторинга часто ставят свои драйверы.",
            "Точное имя драйвера-виновника можно узнать, открыв файл дампа в WinDbg или BlueScreenView.",
        },
        _ => new[]
        {
            "Откройте файл дампа в WinDbg или BlueScreenView, чтобы узнать драйвер-виновник.",
            "Обновите драйверы чипсета и видеокарты, снимите разгон.",
        },
    };
}

/// <summary>Коды проблем Диспетчера устройств (CM_PROB_*).</summary>
public static class DeviceProblemCodes
{
    public static string Describe(int code) => code switch
    {
        1 => "устройство неправильно настроено",
        3 => "драйвер повреждён или не хватает ресурсов",
        10 => "устройство не может запуститься",
        12 => "не хватает свободных ресурсов (конфликт)",
        14 => "нужна перезагрузка",
        18 => "драйверы нужно переустановить",
        19 => "повреждены настройки устройства в реестре",
        21 => "Windows удаляет устройство",
        22 => "устройство отключено",
        24 => "устройство отсутствует или работает неправильно",
        28 => "драйверы не установлены",
        29 => "устройство отключено в BIOS/прошивке",
        31 => "Windows не может загрузить драйвер",
        32 => "служба драйвера отключена",
        37 => "драйвер вернул ошибку при запуске",
        38 => "предыдущий экземпляр драйвера ещё в памяти — нужна перезагрузка",
        39 => "драйвер повреждён или отсутствует",
        40 => "повреждены сведения о службе драйвера",
        41 => "драйвер загружен, но устройство не найдено",
        43 => "Windows остановила устройство: оно сообщило о проблеме",
        47 => "устройство подготовлено к безопасному извлечению",
        48 => "драйвер заблокирован как несовместимый",
        52 => "у драйвера нет подписи или она неверна",
        _ => "код ошибки " + code,
    };
}
