using System.Globalization;
using System.Text.RegularExpressions;
using WinPulse.Core.Analysis;
using WinPulse.Core.Formatting;

namespace WinPulse.Core.Diagnostics;

/// <summary>
/// Детекторы проблем: каждый смотрит на свою часть журналов или состояния системы и выдаёт находку —
/// либо проблему с объяснением и советами, либо «проверено, всё в порядке».
/// </summary>
public static partial class ProblemDetectors
{
    // Источники событий
    public const string KernelPower = "Microsoft-Windows-Kernel-Power";
    public const string EventLogSource = "EventLog";
    public const string Wer = "Microsoft-Windows-WER-SystemErrorReporting";
    public const string Display = "Display";
    public const string Whea = "Microsoft-Windows-WHEA-Logger";
    public const string ProcessorPower = "Microsoft-Windows-Kernel-Processor-Power";
    public const string MemoryDiagnostics = "Microsoft-Windows-MemoryDiagnostics-Results";
    public const string WindowsUpdate = "Microsoft-Windows-WindowsUpdateClient";
    public const string ServiceControlManager = "Service Control Manager";
    public const string ApplicationError = "Application Error";
    public const string ApplicationHang = "Application Hang";
    public const string DotNetRuntime = ".NET Runtime";
    public const string BootPerformance = "Microsoft-Windows-Diagnostics-Performance";

    public static readonly HashSet<string> StorageProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        "disk", "Ntfs", "Microsoft-Windows-Ntfs", "stornvme", "storahci", "iaStorAC", "iaStorA", "iaStorAVC", "nvme",
    };

    private static FindingAction EventViewer => new(Lang.T("Открыть «Просмотр событий»", "Open Event Viewer"), "eventvwr.msc");
    private static FindingAction ReliabilityMonitor => new(Lang.T("Монитор стабильности Windows", "Windows Reliability Monitor"), "perfmon.exe", "/rel");
    private static FindingAction MemoryTest => new(Lang.T("Проверить память средствами Windows", "Test memory with Windows tools"), "mdsched.exe");
    private static FindingAction DeviceManager => new(Lang.T("Открыть Диспетчер устройств", "Open Device Manager"), "devmgmt.msc");
    private static FindingAction UpdateSettings => new(Lang.T("Открыть Центр обновления", "Open Windows Update"), "ms-settings:windowsupdate");
    private static FindingAction StorageSettings => new(Lang.T("Открыть «Память устройства»", "Open Storage settings"), "ms-settings:storagesense");
    private static FindingAction DiskCleanup => new(Lang.T("Очистка диска", "Disk Cleanup"), "cleanmgr.exe");
    private static FindingAction PerformanceOptions => new(Lang.T("Параметры быстродействия", "Performance Options"), "SystemPropertiesPerformance.exe");
    private static FindingAction PowerOptions => new(Lang.T("Электропитание", "Power Options"), "control.exe", "powercfg.cpl");

    [GeneratedRegex(@"Harddisk(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex HarddiskPattern();

    public static DiagnosticReport Run(DiagnosticInput input)
    {
        var all = new List<Finding>();
        void Add(Func<DiagnosticInput, IEnumerable<Finding>> detector, string name)
        {
            try
            {
                all.AddRange(detector(input));
            }
            catch (Exception ex)
            {
                Diag.Report("detector " + name, ex);
            }
        }

        Add(BlueScreens, "bsod");
        Add(UnexpectedShutdowns, "shutdowns");
        Add(GpuResets, "tdr");
        Add(HardwareErrors, "whea");
        Add(DiskErrors, "disk");
        Add(DiskHealthCheck, "disk-health");
        Add(LowSpace, "space");
        Add(AppCrashes, "crashes");
        Add(AppHangs, "hangs");
        Add(Devices, "devices");
        Add(Services, "services");
        Add(Updates, "updates");
        Add(Throttling, "throttling");
        Add(MemoryTestResults, "memtest");
        Add(Boot, "boot");
        Add(PageFile, "pagefile");
        Add(Reboot, "reboot");

        var problems = all.Where(f => f.Severity > Severity.Ok)
            .OrderByDescending(f => f.Severity)
            .ThenByDescending(f => f.LastSeen ?? DateTime.MinValue)
            .ToList();
        var passed = all.Where(f => f.Severity == Severity.Ok).ToList();

        return new DiagnosticReport
        {
            ScannedAt = input.Now,
            PeriodDays = input.PeriodDays,
            Problems = problems,
            Passed = passed,
            Days = Timeline(input),
            Notes = Notes(input),
        };
    }

    // ───────────────────────────── помощники ─────────────────────────────

    private static int Within(IEnumerable<DateTime> times, DateTime now, int days) =>
        times.Count(t => t >= now.AddDays(-days));

    private static Finding Ok(string id, string category, string title, string summary, IReadOnlyList<Evidence>? evidence = null) => new()
    {
        Id = id,
        Category = category,
        Severity = Severity.Ok,
        Title = title,
        Summary = summary,
        Evidence = evidence ?? [],
    };

    private static bool TryLong(string? s, out long value) =>
        long.TryParse(s?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static string Times(int n) => Ru.Count(n, "раз", "раза", "раз", "time", "times");

    private static string Period(DiagnosticInput input) => Ru.Count(input.PeriodDays, "день", "дня", "дней", "day", "days");

    // ───────────────────────────── синие экраны ─────────────────────────────

    private static List<Finding> BlueScreens(DiagnosticInput input)
    {
        var crashes = new List<(DateTime Time, BugCheckInfo Info, string? Dump)>();
        foreach (var e in input.Events.Where(e => e.Is(Wer, 1001)))
        {
            var code = BugChecks.ParseParam1(e.Get("param1", 0));
            if (code is null) continue;
            string? dump = e.Get("param2", 1);
            crashes.Add((e.Time, BugChecks.Describe(code.Value), string.IsNullOrWhiteSpace(dump) ? null : dump));
        }
        // Kernel-Power 41 с кодом ошибки — если отчёт 1001 не записался (например, дамп не успел сохраниться).
        foreach (var e in input.Events.Where(e => e.Is(KernelPower, 41)))
        {
            if (!TryLong(e.Get("BugcheckCode", 0), out long bc) || bc == 0) continue;
            if (crashes.Any(c => Math.Abs((c.Time - e.Time).TotalMinutes) < 15)) continue;
            crashes.Add((e.Time, BugChecks.Describe((uint)bc), null));
        }

        if (crashes.Count == 0)
            return new() { Ok("bsod", Lang.T("Сбои системы", "System crashes"), Lang.T("Синих экранов не было", "No blue screens"), Lang.T($"За {Period(input)} Windows ни разу не падала с синим экраном.", $"In {Period(input)} Windows never crashed with a blue screen.")) };

        crashes.Sort((a, b) => b.Time.CompareTo(a.Time));
        var groups = crashes.GroupBy(c => c.Info.Code).OrderByDescending(g => g.Count()).ToList();
        var top = groups[0].First().Info;
        int count7 = Within(crashes.Select(c => c.Time), input.Now, 7);

        CrashCause cause;
        string summary;
        if (groups.Count == 1)
        {
            cause = top.Cause;
            summary = Lang.T($"Код {top.Hex} ({top.Name}): {top.Meaning}. Вероятная причина — {BugChecks.CauseText(cause)}.", $"Code {top.Hex} ({top.Name}): {top.Meaning}. Likely cause: {BugChecks.CauseText(cause)}.");
        }
        else
        {
            var causes = crashes.Select(c => c.Info.Cause).Distinct().ToList();
            cause = causes.Count == 1 ? causes[0] : causes.Contains(CrashCause.Graphics) && causes.Count == 2 ? CrashCause.Graphics : CrashCause.Hardware;
            string codes = string.Join(", ", groups.Select(g => $"{g.First().Info.Name} ×{g.Count()}"));
            summary = Lang.T($"Коды ошибок разные: {codes}. ", $"Different error codes: {codes}. ") +
                      (causes.Count > 2
                          ? Lang.T("Когда синие экраны каждый раз с новым кодом, частая причина — нестабильное оборудование: оперативная память, разгон или питание.", "When every blue screen has a different code, the usual cause is unstable hardware: RAM, overclocking or power.")
                          : Lang.T($"Чаще всего — {top.Meaning}.", $"Most often: {top.Meaning}."));
            if (causes.Count > 2) cause = CrashCause.Memory;
        }

        var advice = BugChecks.Advice(cause).ToList();
        advice.Add(Lang.T("Если синие экраны начались после обновления Windows или драйвера — откатите его.", "If the blue screens started after a Windows or driver update, roll it back."));
        var actions = new List<FindingAction> { ReliabilityMonitor, EventViewer };
        if (cause is CrashCause.Memory or CrashCause.Hardware) actions.Insert(0, MemoryTest);

        return new()
        {
            new Finding
            {
                Id = "bsod",
                Category = Lang.T("Сбои системы", "System crashes"),
                Severity = count7 >= 1 || crashes.Count >= 2 ? Severity.Critical : Severity.Warning,
                Title = Lang.T($"Синий экран: {Times(crashes.Count)} за {Period(input)}", $"Blue screen: {Times(crashes.Count)} in {Period(input)}"),
                Summary = summary,
                Advice = advice,
                Evidence = crashes.Select(c => new Evidence(c.Time,
                    $"{c.Info.Hex} {c.Info.Name}" + (c.Dump is null ? "" : Lang.T($" · дамп: {c.Dump}", $" · dump: {c.Dump}")))).ToList(),
                Count7 = count7,
                Count30 = crashes.Count,
                LastSeen = crashes[0].Time,
                Technical = "System: WER-SystemErrorReporting 1001, Kernel-Power 41 (BugcheckCode)",
                Actions = actions,
            },
        };
    }

    // ───────────────────────────── внезапные выключения ─────────────────────────────

    private static List<Finding> UnexpectedShutdowns(DiagnosticInput input)
    {
        var k41 = input.Events.Where(e => e.Is(KernelPower, 41)).ToList();
        var unexpected = k41.Where(e => !TryLong(e.Get("BugcheckCode", 0), out long bc) || bc == 0).ToList();
        if (k41.Count == 0) unexpected = input.Events.Where(e => e.Is(EventLogSource, 6008)).ToList();

        if (unexpected.Count == 0)
            return new() { Ok("shutdowns", Lang.T("Сбои системы", "System crashes"), Lang.T("Внезапных выключений не было", "No sudden shutdowns"), Lang.T("Каждый раз Windows завершала работу нормально.", "Windows shut down normally every time.")) };

        unexpected.Sort((a, b) => b.Time.CompareTo(a.Time));
        int count7 = Within(unexpected.Select(e => e.Time), input.Now, 7);
        int byButton = unexpected.Count(e => TryLong(e.Get("PowerButtonTimestamp"), out long t) && t != 0);

        return new()
        {
            new Finding
            {
                Id = "shutdowns",
                Category = Lang.T("Сбои системы", "System crashes"),
                Severity = count7 >= 3 ? Severity.Critical : Severity.Warning,
                Title = Lang.T($"Компьютер внезапно выключался: {Times(unexpected.Count)}", $"The PC shut down unexpectedly: {Times(unexpected.Count)}"),
                Summary = Lang.T("Windows не успела нормально завершить работу, и синего экрана при этом не было. ", "Windows didn't get to shut down properly, and there was no blue screen. ") +
                          Lang.T("Так бывает, когда компьютер зависает и его перезапускают кнопкой, при пропадании питания, перегреве ", "This happens when the PC freezes and is reset with the button, on power loss, overheating ") +
                          Lang.T("или проблемах блока питания.", "or power supply problems.") +
                          (byButton > 0 ? Lang.T($" В {Times(byButton)} перед этим была нажата кнопка питания — похоже, компьютер завис.", $" {Times(byButton)} the power button was pressed right before — looks like the PC froze.") : ""),
                Advice = new[]
                {
                    Lang.T("Если вы сами выключали компьютер кнопкой или из розетки — эти записи можно не учитывать.", "If you turned the PC off with the button or at the wall yourself, ignore these entries."),
                    Lang.T("Если компьютер сам перезагружается в играх — проверьте температуры процессора и видеокарты и блок питания (мощность, возраст).", "If the PC restarts by itself in games, check CPU and GPU temperatures and the power supply (wattage, age)."),
                    Lang.T("Снимите разгон процессора, памяти и видеокарты и проверьте, повторится ли.", "Remove CPU, memory and GPU overclocks and see whether it happens again."),
                    Lang.T("Для ноутбука: проверьте зарядное устройство и батарею.", "For a laptop: check the charger and battery."),
                },
                Evidence = unexpected.Select(e => new Evidence(e.Time,
                    e.Id == 6008 ? Lang.T("Предыдущее завершение работы было неожиданным", "The previous shutdown was unexpected")
                    : Lang.T("Перезагрузка без нормального завершения", "Restart without a clean shutdown") +
                      (TryLong(e.Get("SleepInProgress"), out long sleep) && sleep != 0 ? Lang.T(" · во время сна", " · during sleep") : "") +
                      (TryLong(e.Get("PowerButtonTimestamp"), out long pb) && pb != 0 ? Lang.T(" · была нажата кнопка питания", " · power button was pressed") : ""))).ToList(),
                Count7 = count7,
                Count30 = unexpected.Count,
                LastSeen = unexpected[0].Time,
                Technical = "System: Kernel-Power 41 (BugcheckCode = 0), EventLog 6008",
                Actions = new[] { ReliabilityMonitor, EventViewer },
            },
        };
    }

    // ───────────────────────────── сбросы видеодрайвера ─────────────────────────────

    private static List<Finding> GpuResets(DiagnosticInput input)
    {
        var resets = input.Events.Where(e => e.Is(Display, 4101)).OrderByDescending(e => e.Time).ToList();
        if (resets.Count == 0)
            return new() { Ok("tdr", Lang.T("Видеокарта", "Graphics"), Lang.T("Видеодрайвер не сбрасывался", "No graphics driver resets"), Lang.T("Драйвер видеокарты ни разу не переставал отвечать.", "The graphics driver never stopped responding.")) };

        int count7 = Within(resets.Select(e => e.Time), input.Now, 7);
        return new()
        {
            new Finding
            {
                Id = "tdr",
                Category = Lang.T("Видеокарта", "Graphics"),
                Severity = count7 >= 3 ? Severity.Critical : Severity.Warning,
                Title = Lang.T($"Видеодрайвер перезапускался: {Times(resets.Count)}", $"Graphics driver restarted: {Times(resets.Count)}"),
                Summary = Lang.T("Видеокарта переставала отвечать, и Windows перезапускала её драйвер: экран мигал или гас на пару секунд, ", "The GPU stopped responding and Windows restarted its driver: the screen flickered or went black for a couple of seconds, ") +
                          Lang.T("игра могла вылететь. Частые причины — нестабильный разгон видеокарты, перегрев, неудачная версия драйвера, нехватка питания.", "and a game may have crashed. Common causes: an unstable GPU overclock, overheating, a bad driver version, insufficient power."),
                Advice = BugChecks.Advice(CrashCause.Graphics).Concat(new[]
                {
                    Lang.T("Если сбой только в одной игре — снизьте в ней настройки и отключите оверлеи (Discord, RivaTuner, NVIDIA).", "If it only happens in one game, lower its settings and turn off overlays (Discord, RivaTuner, NVIDIA)."),
                }).ToList(),
                Evidence = resets.Select(e => new Evidence(e.Time, Lang.T("Драйвер перезапущен: ", "Driver restarted: ") + (e.At(0) ?? Lang.T("видеодрайвер", "graphics driver")))).ToList(),
                Count7 = count7,
                Count30 = resets.Count,
                LastSeen = resets[0].Time,
                Technical = "System: Display 4101",
                Actions = new[] { EventViewer },
            },
        };
    }

    // ───────────────────────────── аппаратные ошибки WHEA ─────────────────────────────

    private static List<Finding> HardwareErrors(DiagnosticInput input)
    {
        var whea = input.Events.Where(e => e.From(Whea)).OrderByDescending(e => e.Time).ToList();
        if (whea.Count == 0)
            return new() { Ok("whea", Lang.T("Оборудование", "Hardware"), Lang.T("Аппаратных ошибок не было", "No hardware errors"), Lang.T("Процессор, память и шина PCI Express не сообщали об ошибках (WHEA).", "The CPU, memory and PCI Express bus reported no errors (WHEA).")) };

        var list = new List<Finding>();
        var fatal = whea.Where(e => e.Level is >= 1 and <= 2).ToList();
        var corrected = whea.Where(e => e.Level >= 3).ToList();

        if (fatal.Count > 0)
        {
            list.Add(new Finding
            {
                Id = "whea-fatal",
                Category = Lang.T("Оборудование", "Hardware"),
                Severity = Severity.Critical,
                Title = Lang.T($"Неисправимые аппаратные ошибки: {fatal.Count}", $"Uncorrectable hardware errors: {fatal.Count}"),
                Summary = Lang.T("Оборудование сообщило об ошибке, которую не смогло исправить, — обычно после этого бывает синий экран WHEA_UNCORRECTABLE_ERROR. ", "The hardware reported an error it couldn't correct — usually followed by a WHEA_UNCORRECTABLE_ERROR blue screen. ") +
                          Lang.T("Частые причины: нестабильный разгон или андервольт, перегрев, проблемы питания, реже — неисправный процессор или память.", "Common causes: an unstable overclock or undervolt, overheating, power problems, less often a faulty CPU or RAM."),
                Advice = BugChecks.Advice(CrashCause.Hardware),
                Evidence = fatal.Select(e => new Evidence(e.Time, $"WHEA {e.Id}: " + WheaText(e.Id))).ToList(),
                Count7 = Within(fatal.Select(e => e.Time), input.Now, 7),
                Count30 = fatal.Count,
                LastSeen = fatal[0].Time,
                Technical = "System: WHEA-Logger (Level 1–2)",
                Actions = new[] { MemoryTest, EventViewer },
            });
        }

        if (corrected.Count > 0)
        {
            var byId = corrected.GroupBy(e => e.Id).OrderByDescending(g => g.Count()).ToList();
            bool cpu = byId.Any(g => g.Key == 19);
            int count7 = Within(corrected.Select(e => e.Time), input.Now, 7);
            list.Add(new Finding
            {
                Id = "whea-corrected",
                Category = Lang.T("Оборудование", "Hardware"),
                Severity = cpu || count7 >= 10 ? Severity.Warning : Severity.Info,
                Title = Lang.T($"Исправленные аппаратные ошибки: {corrected.Count}", $"Corrected hardware errors: {corrected.Count}"),
                Summary = string.Join(" ", byId.Select(g => $"{WheaText(g.Key)} — {Times(g.Count())}.")) +
                          Lang.T(" Сами по себе исправленные ошибки не опасны, но частые — признак нестабильности.", " Corrected errors aren't dangerous by themselves, but frequent ones are a sign of instability."),
                Advice = cpu
                    ? BugChecks.Advice(CrashCause.Hardware)
                    : new[]
                    {
                        Lang.T("Ошибки шины PCI Express часто вызывает энергосбережение PCIe: в «Электропитании» → «Изменить доп. параметры» → «PCI Express» → «Управление питанием состояния связи» поставьте «Откл.».", "PCI Express bus errors are often caused by PCIe power saving: in Power Options → Change advanced settings → PCI Express → Link State Power Management, set Off."),
                        Lang.T("Обновите BIOS и драйвер чипсета; если видеокарта подключена через райзер — проверьте его.", "Update the BIOS and chipset driver; if the GPU is connected through a riser, check it."),
                    },
                Evidence = corrected.Take(20).Select(e => new Evidence(e.Time, $"WHEA {e.Id}: " + WheaText(e.Id))).ToList(),
                Count7 = count7,
                Count30 = corrected.Count,
                LastSeen = corrected[0].Time,
                Technical = "System: WHEA-Logger (Level 3): " + string.Join(", ", byId.Select(g => $"ID {g.Key} ×{g.Count()}")),
                Actions = cpu ? new[] { EventViewer } : new[] { PowerOptions, EventViewer },
            });
        }
        return list;
    }

    private static string WheaText(int id) => id switch
    {
        17 => Lang.T("исправленная ошибка шины PCI Express (видеокарта, NVMe-диск или слот)", "corrected PCI Express bus error (GPU, NVMe drive or slot)"),
        19 => Lang.T("процессор исправил внутреннюю ошибку — частая причина: разгон или андервольт", "the CPU corrected an internal error — common cause: overclock or undervolt"),
        47 => Lang.T("память исправила ошибку (ECC)", "memory corrected an error (ECC)"),
        18 or 1 => Lang.T("неисправимая ошибка процессора (machine check)", "uncorrectable CPU error (machine check)"),
        20 => Lang.T("неисправимая ошибка шины PCI Express", "uncorrectable PCI Express bus error"),
        46 => Lang.T("неисправимая ошибка памяти", "uncorrectable memory error"),
        _ => Lang.T("аппаратная ошибка", "hardware error"),
    };

    // ───────────────────────────── ошибки дисков ─────────────────────────────

    private static List<Finding> DiskErrors(DiagnosticInput input)
    {
        var events = input.Events.Where(e => StorageProviders.Contains(e.Provider) && e.Level is >= 1 and <= 3)
            .OrderByDescending(e => e.Time).ToList();
        if (events.Count == 0)
            return new() { Ok("disk-errors", Lang.T("Диски", "Disks"), Lang.T("Ошибок дисков в журнале нет", "No disk errors in the log"), Lang.T("Диски и контроллеры не сообщали об ошибках чтения, записи или сбросах.", "Disks and controllers reported no read, write or reset errors.")) };

        var classified = events.Select(e => (Event: e, Kind: ClassifyStorage(e))).ToList();
        bool badBlocks = classified.Any(c => c.Kind.Severity == Severity.Critical);
        bool onlyRemovals = classified.All(c => c.Kind.Severity == Severity.Info);
        int count7 = Within(events.Select(e => e.Time), input.Now, 7);

        var kinds = classified.GroupBy(c => c.Kind.Text).OrderByDescending(g => g.Count())
            .Select(g => $"{g.Key} — {Times(g.Count())}").ToList();

        return new()
        {
            new Finding
            {
                Id = "disk-errors",
                Category = Lang.T("Диски", "Disks"),
                Severity = badBlocks || count7 >= 10 ? Severity.Critical : onlyRemovals ? Severity.Info : Severity.Warning,
                Title = onlyRemovals ? Lang.T($"Диски отключались внезапно: {Times(events.Count)}", $"Disks were disconnected suddenly: {Times(events.Count)}") : Lang.T($"Ошибки дисков: {events.Count} за {Period(input)}", $"Disk errors: {events.Count} in {Period(input)}"),
                Summary = string.Join("; ", kinds) + "." +
                          (badBlocks ? Lang.T(" Повреждённые сектора — серьёзный признак износа: сделайте резервную копию важных данных.", " Bad sectors are a serious sign of wear: back up important data.") : ""),
                Advice = onlyRemovals
                    ? new[] { Lang.T("Если вы сами отключали флешку или внешний диск — всё в порядке. Иначе проверьте кабель и разъём.", "If you unplugged a flash drive or external disk yourself, all is well. Otherwise check the cable and port.") }
                    : BugChecks.Advice(CrashCause.Storage).Prepend(badBlocks
                        ? Lang.T("Сначала сделайте резервную копию важных файлов с этого диска.", "First back up important files from this disk.")
                        : Lang.T("Посмотрите, к какому диску относятся ошибки (номер диска в записях ниже).", "See which disk the errors belong to (the disk number in the entries below).")).ToList(),
                Evidence = classified.Take(25).Select(c => new Evidence(c.Event.Time, c.Kind.Text + DiskSuffix(c.Event, input))).ToList(),
                Count7 = count7,
                Count30 = events.Count,
                LastSeen = events[0].Time,
                Technical = "System: " + string.Join(", ", events.GroupBy(e => $"{e.Provider} {e.Id}").Select(g => $"{g.Key} ×{g.Count()}")),
                Actions = new[] { EventViewer },
            },
        };
    }

    private static (Severity Severity, string Text) ClassifyStorage(EventItem e)
    {
        string p = e.Provider.ToLowerInvariant();
        if (p == "disk")
            return e.Id switch
            {
                7 => (Severity.Critical, Lang.T("на диске найдены повреждённые сектора", "bad sectors found on the disk")),
                11 => (Severity.Warning, Lang.T("ошибка контроллера при обращении к диску", "controller error while accessing the disk")),
                51 => (Severity.Warning, Lang.T("ошибка во время записи страниц памяти на диск", "error while writing memory pages to disk")),
                153 => (Severity.Warning, Lang.T("диск не ответил вовремя, Windows повторила операцию", "the disk didn't respond in time, Windows retried")),
                157 => (Severity.Info, Lang.T("диск был внезапно отключён", "the disk was suddenly disconnected")),
                _ => (Severity.Warning, Lang.T($"ошибка диска (код события {e.Id})", $"disk error (event ID {e.Id})")),
            };
        if (p.Contains("ntfs"))
            return (Severity.Warning, Lang.T("повреждение файловой системы NTFS", "NTFS file system corruption"));
        if (e.Id == 129)
            return (Severity.Warning, Lang.T("контроллер сбрасывал диск — подвисание на несколько секунд", "the controller reset the disk — a freeze of a few seconds"));
        return (Severity.Warning, Lang.T($"ошибка хранилища: {e.Provider} {e.Id}", $"storage error: {e.Provider} {e.Id}"));
    }

    private static string DiskSuffix(EventItem e, DiagnosticInput input)
    {
        foreach (var d in e.Data)
        {
            var m = HarddiskPattern().Match(d);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out int n)) continue;
            return input.DiskNames.TryGetValue(n, out var name) ? Lang.T($" · диск {n} ({name})", $" · disk {n} ({name})") : Lang.T($" · диск {n}", $" · disk {n}");
        }
        return "";
    }

    // ───────────────────────────── здоровье дисков ─────────────────────────────

    private static List<Finding> DiskHealthCheck(DiagnosticInput input)
    {
        if (!input.DiskHealthChecked || input.DiskHealth.Count == 0) return new();
        var list = new List<Finding>();
        var okEvidence = new List<Evidence>();

        foreach (var d in input.DiskHealth)
        {
            var issues = new List<string>();
            var severity = Severity.Ok;
            string health = HealthName(d.Health);
            if (health == "Unhealthy")
            {
                issues.Add(Lang.T("Windows считает диск неисправным", "Windows considers the disk faulty"));
                severity = Severity.Critical;
            }
            else if (health == "Warning")
            {
                issues.Add(Lang.T("Windows предупреждает о проблемах с диском", "Windows warns about disk problems"));
                severity = Severity.Critical;
            }
            if (d.WearPercent is int wear && wear >= 90)
            {
                issues.Add(Lang.T($"ресурс записи израсходован на {wear}%", $"{wear}% of write endurance used"));
                severity = Max(severity, Severity.Warning);
            }
            if (d.ReadErrorsUncorrected is long re && re > 0)
            {
                issues.Add(Lang.T($"неисправленных ошибок чтения: {re}", $"uncorrected read errors: {re}"));
                severity = Max(severity, Severity.Warning);
            }
            int hot = d.Media.Contains("HDD", StringComparison.OrdinalIgnoreCase) ? 55 : 70;
            if (d.Temperature is int t && t >= hot)
            {
                issues.Add(Lang.T($"температура {t} °C", $"temperature {t} °C"));
                severity = Max(severity, Severity.Warning);
            }

            string details = DiskDetails(d);
            if (issues.Count == 0)
            {
                okEvidence.Add(new Evidence(null, Lang.T($"{d.Name}: исправен{details}", $"{d.Name}: healthy{details}")));
                continue;
            }

            list.Add(new Finding
            {
                Id = "disk-health-" + d.Name,
                Category = Lang.T("Диски", "Disks"),
                Severity = severity,
                Title = Lang.T($"Диск «{d.Name}»: {issues[0]}", $"Disk “{d.Name}”: {issues[0]}"),
                Summary = string.Join("; ", issues) + "." +
                          (severity == Severity.Critical ? Lang.T(" Скопируйте важные данные с этого диска как можно скорее.", " Copy important data off this disk as soon as possible.") : ""),
                Advice = new[]
                {
                    Lang.T("Сделайте резервную копию важных файлов.", "Back up important files."),
                    Lang.T("Посмотрите подробные данные SMART программой производителя диска или CrystalDiskInfo.", "Look at the detailed SMART data with the disk maker's tool or CrystalDiskInfo."),
                    Lang.T("Перегрев: проверьте обдув диска; для NVMe-дисков помогает радиатор.", "Overheating: check airflow over the disk; a heatsink helps NVMe drives."),
                },
                Evidence = new[] { new Evidence(null, $"{d.Name}{details}") },
                Technical = $"Get-PhysicalDisk: HealthStatus={d.Health}, Media={d.Media}, Bus={d.Bus}",
            });
        }

        if (list.Count == 0)
            list.Add(Ok("disk-health", Lang.T("Диски", "Disks"), Lang.T("Диски исправны", "Disks are healthy"), Lang.T("По данным Windows все физические диски в порядке.", "According to Windows, all physical disks are fine."), okEvidence));
        return list;
    }

    private static string HealthName(string h) => h.Trim() switch
    {
        "0" => "Healthy",
        "1" => "Warning",
        "2" => "Unhealthy",
        "5" => "Unknown",
        var s => s,
    };

    private static string DiskDetails(DiskHealthInfo d)
    {
        var parts = new List<string>();
        if (d.Size > 0) parts.Add(Ru.Bytes(d.Size));
        if (d.WearPercent is int w) parts.Add(Lang.T($"износ {w}%", $"wear {w}%"));
        if (d.Temperature is int t && t > 0) parts.Add($"{t} °C");
        if (d.PowerOnHours is long h && h > 0) parts.Add(Lang.T($"{h:N0} ч работы", $"{h:N0} h powered on").Replace(",", " "));
        return parts.Count == 0 ? "" : " · " + string.Join(", ", parts);
    }

    private static Severity Max(Severity a, Severity b) => a > b ? a : b;

    // ───────────────────────────── место на дисках ─────────────────────────────

    private static List<Finding> LowSpace(DiagnosticInput input)
    {
        var list = new List<Finding>();
        const long GB = 1024L * 1024 * 1024;
        foreach (var v in input.Volumes.Where(v => v.TotalBytes > 0))
        {
            bool system = v.Letter.Equals(input.SystemDrive, StringComparison.OrdinalIgnoreCase);
            double pct = (double)v.FreeBytes / v.TotalBytes * 100;
            Severity s = Severity.Ok;
            if (system && (pct < 5 || v.FreeBytes < 5 * GB)) s = Severity.Critical;
            else if (system && (pct < 10 || v.FreeBytes < 15 * GB)) s = Severity.Warning;
            else if (!system && pct < 5) s = Severity.Info;
            if (s == Severity.Ok) continue;

            list.Add(new Finding
            {
                Id = "space-" + v.Letter,
                Category = Lang.T("Диски", "Disks"),
                Severity = s,
                Title = Lang.T($"Мало места на диске {v.Letter}", $"Low space on {v.Letter}"),
                Summary = Lang.T($"Свободно {Ru.Bytes(v.FreeBytes)} из {Ru.Bytes(v.TotalBytes)} ({pct:0}%).", $"{Ru.Bytes(v.FreeBytes)} free of {Ru.Bytes(v.TotalBytes)} ({pct:0}%).") +
                          (system
                              ? Lang.T(" Когда на системном диске мало места, Windows не может установить обновления, а программы и игры начинают сбоить при записи.", " When the system disk is low on space, Windows can't install updates, and programs and games start failing when writing.")
                              : ""),
                Advice = new[]
                {
                    Lang.T("Включите «Контроль памяти» или запустите «Очистку диска» → «Очистить системные файлы».", "Turn on Storage Sense or run Disk Cleanup → Clean up system files."),
                    Lang.T("Удалите ненужные игры и программы, перенесите большие файлы на другой диск.", "Remove games and programs you no longer need, move large files to another disk."),
                    Lang.T("Не удаляйте вручную папки Windows (WinSxS, Installer) — это ломает обновления.", "Don't delete Windows folders (WinSxS, Installer) by hand — it breaks updates."),
                },
                Evidence = new[] { new Evidence(null, Lang.T($"{v.Letter} {v.Label}: свободно {Ru.Bytes(v.FreeBytes)} из {Ru.Bytes(v.TotalBytes)}", $"{v.Letter} {v.Label}: {Ru.Bytes(v.FreeBytes)} free of {Ru.Bytes(v.TotalBytes)}")) },
                Actions = system ? new[] { StorageSettings, DiskCleanup } : new[] { StorageSettings },
            });
        }
        if (list.Count == 0 && input.Volumes.Count > 0)
            list.Add(Ok("space", Lang.T("Диски", "Disks"), Lang.T("Места на дисках достаточно", "Enough disk space"), Lang.T("На системном диске больше 10% свободного места.", "The system disk has more than 10% free space."),
                input.Volumes.Where(v => v.TotalBytes > 0)
                    .Select(v => new Evidence(null, Lang.T($"{v.Letter}: свободно {Ru.Bytes(v.FreeBytes)} из {Ru.Bytes(v.TotalBytes)}", $"{v.Letter}: {Ru.Bytes(v.FreeBytes)} free of {Ru.Bytes(v.TotalBytes)}"))).ToList()));
        return list;
    }
}
