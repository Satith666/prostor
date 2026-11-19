using System.Buffers.Binary;
using System.Text;

namespace WinPulse.Core.Diagnostics;

/// <summary>Один драйвер в замере задержек: сколько раз и как долго он держал процессор в DPC и ISR.</summary>
public sealed class DriverLatency
{
    public string Name { get; set; } = "";
    public long DpcCount { get; set; }
    public long IsrCount { get; set; }
    public double MaxDpcUs { get; set; }
    public double MaxIsrUs { get; set; }
    public double TotalUs { get; set; }
    /// <summary>Сколько раз одна процедура длилась дольше 500 мкс.</summary>
    public long Over500 { get; set; }
    /// <summary>Сколько раз дольше 1 мс.</summary>
    public long Over1000 { get; set; }

    public double MaxUs => Math.Max(MaxDpcUs, MaxIsrUs);
}

/// <summary>Итог замера DPC/ISR — как у LatencyMon, но в WinPulse и с объяснением.</summary>
public sealed class LatencyReport
{
    public DateTime Taken { get; set; } = DateTime.Now;
    public double Seconds { get; set; }
    public int Processors { get; set; } = Environment.ProcessorCount;
    public long DpcCount { get; set; }
    public long IsrCount { get; set; }
    public double MaxDpcUs { get; set; }
    public double MaxIsrUs { get; set; }
    public double TotalUs { get; set; }
    /// <summary>Сколько событий ETW потеряно (буферы не успели прочитать).</summary>
    public long EventsLost { get; set; }
    /// <summary>Сколько драйверов (модулей ядра) удалось сопоставить адресам.</summary>
    public int ModulesKnown { get; set; }
    public string ModuleSource { get; set; } = "";
    /// <summary>Гистограмма длительностей: границы — <see cref="LatencyAggregator.Buckets"/>.</summary>
    public long[] Histogram { get; set; } = [];
    public List<DriverLatency> Drivers { get; set; } = [];
    public string? Error { get; set; }
    /// <summary>Техническая строка для разбора сбоев (в интерфейсе не показывается).</summary>
    public string? Debug { get; set; }

    public double MaxUs => Math.Max(MaxDpcUs, MaxIsrUs);
    public DriverLatency? Worst => Drivers.OrderByDescending(d => d.MaxUs).FirstOrDefault();
}

/// <summary>Сводит события DPC/ISR в отчёт по драйверам. Адрес процедуры → модуль ядра по диапазонам адресов.</summary>
public sealed class LatencyAggregator
{
    /// <summary>Верхние границы корзин гистограммы, мкс. Последняя корзина — всё, что дольше.</summary>
    public static readonly double[] Buckets = [10, 50, 100, 250, 500, 1000, 2000];

    private readonly List<(ulong Base, ulong End, string Name)> _modules = [];
    private bool _sorted;
    private readonly Dictionary<string, DriverLatency> _drivers = new(StringComparer.OrdinalIgnoreCase);
    private readonly long[] _histogram = new long[Buckets.Length + 1];
    private long _dpc, _isr;
    private double _maxDpc, _maxIsr, _total;

    public int ModuleCount => _modules.Count;

    /// <summary>Модуль ядра; size = 0, если размер неизвестен (тогда модуль тянется до следующего).</summary>
    public void AddModule(ulong imageBase, ulong size, string path)
    {
        if (imageBase == 0) return;
        string name = FileNameOf(path);
        if (name.Length == 0) return;
        lock (_modules)
        {
            if (_modules.Any(m => m.Base == imageBase)) return;
            _modules.Add((imageBase, size == 0 ? 0 : imageBase + size, name));
            _sorted = false;
        }
    }

    public string ModuleOf(ulong address)
    {
        lock (_modules)
        {
            if (!_sorted)
            {
                _modules.Sort((a, b) => a.Base.CompareTo(b.Base));
                _sorted = true;
            }
            int lo = 0, hi = _modules.Count - 1, found = -1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (_modules[mid].Base <= address) { found = mid; lo = mid + 1; }
                else hi = mid - 1;
            }
            if (found < 0) return Unknown;
            var m = _modules[found];
            if (m.End != 0 && address >= m.End) return Unknown;
            // Без размера модуль ограничиваем 256 МБ, чтобы адрес в «пустоте» не приписать соседу.
            if (m.End == 0 && address - m.Base > 256UL * 1024 * 1024) return Unknown;
            return m.Name;
        }
    }

    public string DebugRange()
    {
        lock (_modules)
        {
            if (_modules.Count == 0) return "none";
            var sorted = _modules.OrderBy(m => m.Base).ToList();
            return $"{_modules.Count}, {sorted[0].Name}@{sorted[0].Base:X} .. {sorted[^1].Name}@{sorted[^1].Base:X}";
        }
    }

    public static string Unknown => Lang.T("неизвестный модуль", "unknown module");

    public void Add(bool isr, ulong routine, double us)
    {
        if (!(us >= 0) || us > 1_000_000) return; // мусор и переполнения отбрасываем
        string name = ModuleOf(routine);
        if (!_drivers.TryGetValue(name, out var d)) _drivers[name] = d = new DriverLatency { Name = name };
        if (isr)
        {
            _isr++;
            d.IsrCount++;
            if (us > d.MaxIsrUs) d.MaxIsrUs = us;
            if (us > _maxIsr) _maxIsr = us;
        }
        else
        {
            _dpc++;
            d.DpcCount++;
            if (us > d.MaxDpcUs) d.MaxDpcUs = us;
            if (us > _maxDpc) _maxDpc = us;
        }
        d.TotalUs += us;
        _total += us;
        if (us > 500) d.Over500++;
        if (us > 1000) d.Over1000++;
        int bucket = 0;
        while (bucket < Buckets.Length && us > Buckets[bucket]) bucket++;
        _histogram[bucket]++;
    }

    public LatencyReport Build(double seconds, long eventsLost, string moduleSource) => new()
    {
        Seconds = seconds,
        DpcCount = _dpc,
        IsrCount = _isr,
        MaxDpcUs = _maxDpc,
        MaxIsrUs = _maxIsr,
        TotalUs = _total,
        EventsLost = eventsLost,
        ModulesKnown = _modules.Count,
        ModuleSource = moduleSource,
        Histogram = (long[])_histogram.Clone(),
        Drivers = _drivers.Values.OrderByDescending(d => d.MaxUs).ThenByDescending(d => d.TotalUs).ToList(),
    };

    public static string FileNameOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        int slash = path.LastIndexOfAny(['\\', '/']);
        return (slash >= 0 ? path[(slash + 1)..] : path).Trim().TrimEnd('\0');
    }
}

/// <summary>Разбор полезной нагрузки событий ядра (классические MOF-события NT Kernel Logger).</summary>
public static class KernelEvents
{
    /// <summary>PerfInfo: DPC, ISR и т. п.</summary>
    public static readonly Guid PerfInfo = new("ce1dbfb4-137e-4da6-87b0-3f59aa102cbc");
    /// <summary>Image: загрузка модулей (DCStart — уже загруженные на момент старта).</summary>
    public static readonly Guid Image = new("2cb15d1d-5fc1-11d2-abe1-00a0c91e2aa2");

    public const byte OpThreadedDpc = 66, OpIsr = 67, OpDpc = 68, OpTimerDpc = 69, OpIsrMsi = 50;
    public const byte OpImageLoad = 10, OpImageDcStart = 3;

    public static bool IsDpc(byte opcode) => opcode is OpDpc or OpTimerDpc or OpThreadedDpc;
    public static bool IsIsr(byte opcode) => opcode is OpIsr or OpIsrMsi;

    /// <summary>DPC/ISR: InitialTime (uint64), Routine (указатель).</summary>
    public static bool TryParseDpc(ReadOnlySpan<byte> data, bool pointer64, out long initialTime, out ulong routine)
    {
        initialTime = 0;
        routine = 0;
        int need = 8 + (pointer64 ? 8 : 4);
        if (data.Length < need) return false;
        initialTime = BinaryPrimitives.ReadInt64LittleEndian(data);
        routine = pointer64 ? BinaryPrimitives.ReadUInt64LittleEndian(data[8..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
        return true;
    }

    /// <summary>
    /// Image_Load (версии 2 и 3): ImageBase, ImageSize (указатели), ProcessId, ImageCheckSum, TimeDateStamp,
    /// 4 байта (подпись или резерв), DefaultBase (указатель), 4×uint32 резерв, FileName (UTF-16 до нуля).
    /// </summary>
    public static bool TryParseImage(ReadOnlySpan<byte> data, bool pointer64, out ulong imageBase, out ulong size, out uint processId, out string fileName)
    {
        imageBase = size = 0;
        processId = 0;
        fileName = "";
        int p = pointer64 ? 8 : 4;
        int nameOffset = p * 3 + 4 * 4 + 16;
        if (data.Length < nameOffset) return false;
        imageBase = pointer64 ? BinaryPrimitives.ReadUInt64LittleEndian(data) : BinaryPrimitives.ReadUInt32LittleEndian(data);
        size = pointer64 ? BinaryPrimitives.ReadUInt64LittleEndian(data[p..]) : BinaryPrimitives.ReadUInt32LittleEndian(data[p..]);
        processId = BinaryPrimitives.ReadUInt32LittleEndian(data[(p * 2)..]);
        var name = data[nameOffset..];
        int end = 0;
        while (end + 1 < name.Length && (name[end] != 0 || name[end + 1] != 0)) end += 2;
        fileName = Encoding.Unicode.GetString(name[..end]);
        return true;
    }
}

public enum LatencyLevel { Good, Warn, Bad }

/// <summary>Оценка и советы по драйверам.</summary>
public static class LatencyAdvisor
{
    public const double WarnUs = 500, BadUs = 2000;

    public static LatencyLevel Level(double us) => us >= BadUs ? LatencyLevel.Bad : us >= WarnUs ? LatencyLevel.Warn : LatencyLevel.Good;

    public static string Verdict(LatencyReport r)
    {
        if (r.DpcCount + r.IsrCount == 0)
            return Lang.T("Событий DPC/ISR не получено — замер не удался.", "No DPC/ISR events received — the measurement failed.");
        var worst = r.Worst;
        string who = worst is null ? "" : Lang.T($" Дольше всех — {worst.Name} ({Us(worst.MaxUs)}).", $" Worst: {worst.Name} ({Us(worst.MaxUs)}).");
        return Level(r.MaxUs) switch
        {
            LatencyLevel.Good => Lang.T("Задержки в норме: драйверы не задерживают процессор надолго, звук и ввод не должны прерываться.",
                "Latency is fine: drivers don't hold the CPU for long, audio and input shouldn't stutter.") + who,
            LatencyLevel.Warn => Lang.T("Есть заметные всплески. Под нагрузкой возможны щелчки звука и редкие рывки.",
                "There are noticeable spikes. Under load you may get audio crackles and occasional hitches.") + who,
            _ => Lang.T("Есть длинные задержки — это может давать щелчки звука, рывки мыши и микрофризы при нормальном FPS.",
                "There are long delays — this can cause audio crackles, mouse hitches and micro-stutter despite good FPS.") + who,
        };
    }

    public static string Us(double us) => us >= 1000
        ? (us / 1000).ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) + Lang.T(" мс", " ms")
        : Math.Round(us).ToString(System.Globalization.CultureInfo.CurrentCulture) + Lang.T(" мкс", " µs");

    private static readonly (string[] Names, string Ru, string En)[] Known =
    [
        (["nvlddmkm.sys"], "Драйвер видеокарты NVIDIA. Всплески часто убирает чистая установка драйвера (без лишних компонентов) или откат на предыдущую версию.",
            "NVIDIA graphics driver. Spikes are often fixed by a clean driver install (without extras) or rolling back a version."),
        (["amdkmdag.sys", "atikmdag.sys", "atikmpag.sys"], "Драйвер видеокарты AMD. Помогает чистая установка драйвера или другая версия.",
            "AMD graphics driver. A clean install or a different version usually helps."),
        (["igdkmd64.sys", "igdkmdn64.sys", "igdkmdnd64.sys"], "Драйвер встроенной графики Intel. Обновите его с сайта Intel или производителя ноутбука.",
            "Intel integrated graphics driver. Update it from Intel or the laptop maker."),
        (["dxgkrnl.sys", "dxgmms2.sys"], "Графическое ядро Windows — обычно за ним стоит драйвер видеокарты.",
            "Windows graphics kernel — usually reflects the graphics driver."),
        (["ndis.sys", "tcpip.sys", "netio.sys", "wfplwfs.sys"], "Сетевой стек Windows: за ним стоит драйвер сетевой карты. Обновите его; на Wi-Fi попробуйте кабель или отключите энергосбережение адаптера.",
            "Windows network stack, driven by the network adapter driver. Update it; on Wi-Fi try a cable or disable adapter power saving."),
        (["netwtw", "netwbw", "rtwlan", "athw", "athr", "qcamain", "bcmwl", "mrvlpcie", "rtl8192", "mtkwl"], "Драйвер Wi-Fi — одна из самых частых причин всплесков. Обновите его с сайта производителя; если играете по кабелю, Wi-Fi можно отключить.",
            "Wi-Fi driver — one of the most common causes of spikes. Update it from the vendor; if you play on a cable, you can disable Wi-Fi."),
        (["rt640x64.sys", "rt68cx21x64.sys", "rtcx21x64.sys", "e1d68x64.sys", "e1i68x64.sys", "e2f68.sys", "killer", "b57nd60a.sys", "l1c63x64.sys"], "Драйвер проводной сети. Обновите его с сайта производителя материнской платы или чипа (Realtek, Intel).",
            "Wired network driver. Update it from the motherboard or chip vendor (Realtek, Intel)."),
        (["storport.sys", "stornvme.sys", "storahci.sys", "iastora", "iastorac.sys", "iastorvd.sys", "ataport.sys", "amdsata", "nvme"], "Контроллер дисков. Обновите драйвер чипсета; драйвер Intel RST иногда работает хуже штатного драйвера Windows.",
            "Storage controller. Update the chipset driver; Intel RST sometimes performs worse than the built-in Windows driver."),
        (["acpi.sys"], "ACPI — управление питанием через BIOS. На ноутбуках часто виноват опрос батареи; помогает обновление BIOS и драйверов чипсета.",
            "ACPI — power management via BIOS. On laptops battery polling is often to blame; a BIOS and chipset driver update helps."),
        (["hdaudbus.sys", "portcls.sys", "rtkvhd64.sys", "rtkhdaud.sys", "ksthunk.sys", "hdaudio.sys", "nahimicvad", "a-volute"], "Звуковой драйвер. Обновите его с сайта производителя платы; «улучшайзеры» звука (Nahimic, Sonic Studio) лучше отключить.",
            "Audio driver. Update it from the motherboard vendor; audio 'enhancers' (Nahimic, Sonic Studio) are better turned off."),
        (["usbxhci.sys", "usbport.sys", "ucx01000.sys", "usbhub3.sys", "usbehci.sys", "hidclass.sys", "hidusb.sys"], "USB. Нагрузку дают мыши и клавиатуры с высоким опросом и дешёвые хабы — попробуйте другой порт (на задней панели) или опрос 1000 Гц вместо 4000–8000.",
            "USB. High-polling mice and keyboards and cheap hubs add load — try another (rear) port or 1000 Hz polling instead of 4000–8000."),
        (["wdf01000.sys"], "Общая библиотека драйверов (KMDF): за ней стоит какой-то другой драйвер — смотрите соседние строки.",
            "Shared driver framework (KMDF): some other driver is behind it — check the other rows."),
        (["ntoskrnl.exe", "hal.dll"], "Ядро Windows (таймеры, планировщик). Обычно не причина, а следствие; проверьте схему питания и энергосбережение процессора.",
            "Windows kernel (timers, scheduler). Usually a symptom, not the cause; check the power plan and CPU power saving."),
    ];

    /// <summary>Что это за драйвер и что делать; null — драйвер неизвестен.</summary>
    public static string? About(string driverName)
    {
        string n = driverName.ToLowerInvariant();
        foreach (var (names, ru, en) in Known)
            if (names.Any(k => k.EndsWith(".sys") || k.EndsWith(".exe") || k.EndsWith(".dll") ? n == k : n.StartsWith(k, StringComparison.Ordinal)))
                return Lang.T(ru, en);
        return null;
    }
}

/// <summary>Режим прерываний PCI-устройства: MSI (сообщения) или старые линии IRQ.</summary>
public sealed record DeviceInterrupt(string Name, string Class, string InstanceId, bool Msi, int? MessageLimit)
{
    public string Category => Class.ToLowerInvariant() switch
    {
        "display" => Lang.T("Видеокарта", "Graphics"),
        "media" => Lang.T("Звук", "Audio"),
        "net" => Lang.T("Сеть", "Network"),
        "scsiadapter" or "hdc" => Lang.T("Диски", "Storage"),
        "usb" => "USB",
        _ => Class,
    };

    /// <summary>Порядок в списке: сначала то, что важнее для игр.</summary>
    public int Order => Class.ToLowerInvariant() switch
    {
        "display" => 0,
        "media" => 1,
        "net" => 2,
        "scsiadapter" or "hdc" => 3,
        "usb" => 4,
        _ => 5,
    };

    public static bool Interesting(string? cls) =>
        cls is not null && cls.ToLowerInvariant() is "display" or "media" or "net" or "scsiadapter" or "hdc" or "usb";
}
