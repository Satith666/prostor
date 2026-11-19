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

    private static readonly FindingAction EventViewer = new("Открыть «Просмотр событий»", "eventvwr.msc");
    private static readonly FindingAction ReliabilityMonitor = new("Монитор стабильности Windows", "perfmon.exe", "/rel");
    private static readonly FindingAction MemoryTest = new("Проверить память средствами Windows", "mdsched.exe");
    private static readonly FindingAction DeviceManager = new("Открыть Диспетчер устройств", "devmgmt.msc");
    private static readonly FindingAction UpdateSettings = new("Открыть Центр обновления", "ms-settings:windowsupdate");
    private static readonly FindingAction StorageSettings = new("Открыть «Память устройства»", "ms-settings:storagesense");
    private static readonly FindingAction DiskCleanup = new("Очистка диска", "cleanmgr.exe");
    private static readonly FindingAction PerformanceOptions = new("Параметры быстродействия", "SystemPropertiesPerformance.exe");
    private static readonly FindingAction PowerOptions = new("Электропитание", "control.exe", "powercfg.cpl");

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

    private static string Times(int n) => Ru.Count(n, "раз", "раза", "раз");

    private static string Period(DiagnosticInput input) => Ru.Count(input.PeriodDays, "день", "дня", "дней");

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
            return new() { Ok("bsod", "Сбои системы", "Синих экранов не было", $"За {Period(input)} Windows ни разу не падала с синим экраном.") };

        crashes.Sort((a, b) => b.Time.CompareTo(a.Time));
        var groups = crashes.GroupBy(c => c.Info.Code).OrderByDescending(g => g.Count()).ToList();
        var top = groups[0].First().Info;
        int count7 = Within(crashes.Select(c => c.Time), input.Now, 7);

        CrashCause cause;
        string summary;
        if (groups.Count == 1)
        {
            cause = top.Cause;
            summary = $"Код {top.Hex} ({top.Name}): {top.Meaning}. Вероятная причина — {BugChecks.CauseText(cause)}.";
        }
        else
        {
            var causes = crashes.Select(c => c.Info.Cause).Distinct().ToList();
            cause = causes.Count == 1 ? causes[0] : causes.Contains(CrashCause.Graphics) && causes.Count == 2 ? CrashCause.Graphics : CrashCause.Hardware;
            summary = $"Коды ошибок разные: {string.Join(", ", groups.Select(g => $"{g.First().Info.Name} ×{g.Count()}"))}. " +
                      (causes.Count > 2
                          ? "Когда синие экраны каждый раз с новым кодом, частая причина — нестабильное оборудование: оперативная память, разгон или питание."
                          : $"Чаще всего — {top.Meaning}.");
            if (causes.Count > 2) cause = CrashCause.Memory;
        }

        var advice = BugChecks.Advice(cause).ToList();
        advice.Add("Если синие экраны начались после обновления Windows или драйвера — откатите его.");
        var actions = new List<FindingAction> { ReliabilityMonitor, EventViewer };
        if (cause is CrashCause.Memory or CrashCause.Hardware) actions.Insert(0, MemoryTest);

        return new()
        {
            new Finding
            {
                Id = "bsod",
                Category = "Сбои системы",
                Severity = count7 >= 1 || crashes.Count >= 2 ? Severity.Critical : Severity.Warning,
                Title = $"Синий экран: {Times(crashes.Count)} за {Period(input)}",
                Summary = summary,
                Advice = advice,
                Evidence = crashes.Select(c => new Evidence(c.Time,
                    $"{c.Info.Hex} {c.Info.Name}" + (c.Dump is null ? "" : $" · дамп: {c.Dump}"))).ToList(),
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
            return new() { Ok("shutdowns", "Сбои системы", "Внезапных выключений не было", "Каждый раз Windows завершала работу нормально.") };

        unexpected.Sort((a, b) => b.Time.CompareTo(a.Time));
        int count7 = Within(unexpected.Select(e => e.Time), input.Now, 7);
        int byButton = unexpected.Count(e => TryLong(e.Get("PowerButtonTimestamp"), out long t) && t != 0);

        return new()
        {
            new Finding
            {
                Id = "shutdowns",
                Category = "Сбои системы",
                Severity = count7 >= 3 ? Severity.Critical : Severity.Warning,
                Title = $"Компьютер внезапно выключался: {Times(unexpected.Count)}",
                Summary = "Windows не успела нормально завершить работу, и синего экрана при этом не было. " +
                          "Так бывает, когда компьютер зависает и его перезапускают кнопкой, при пропадании питания, перегреве " +
                          "или проблемах блока питания." +
                          (byButton > 0 ? $" В {Times(byButton)} перед этим была нажата кнопка питания — похоже, компьютер завис." : ""),
                Advice = new[]
                {
                    "Если вы сами выключали компьютер кнопкой или из розетки — эти записи можно не учитывать.",
                    "Если компьютер сам перезагружается в играх — проверьте температуры процессора и видеокарты и блок питания (мощность, возраст).",
                    "Снимите разгон процессора, памяти и видеокарты и проверьте, повторится ли.",
                    "Для ноутбука: проверьте зарядное устройство и батарею.",
                },
                Evidence = unexpected.Select(e => new Evidence(e.Time,
                    e.Id == 6008 ? "Предыдущее завершение работы было неожиданным"
                    : "Перезагрузка без нормального завершения" +
                      (TryLong(e.Get("SleepInProgress"), out long sleep) && sleep != 0 ? " · во время сна" : "") +
                      (TryLong(e.Get("PowerButtonTimestamp"), out long pb) && pb != 0 ? " · была нажата кнопка питания" : ""))).ToList(),
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
            return new() { Ok("tdr", "Видеокарта", "Видеодрайвер не сбрасывался", "Драйвер видеокарты ни разу не переставал отвечать.") };

        int count7 = Within(resets.Select(e => e.Time), input.Now, 7);
        return new()
        {
            new Finding
            {
                Id = "tdr",
                Category = "Видеокарта",
                Severity = count7 >= 3 ? Severity.Critical : Severity.Warning,
                Title = $"Видеодрайвер перезапускался: {Times(resets.Count)}",
                Summary = "Видеокарта переставала отвечать, и Windows перезапускала её драйвер: экран мигал или гас на пару секунд, " +
                          "игра могла вылететь. Частые причины — нестабильный разгон видеокарты, перегрев, неудачная версия драйвера, нехватка питания.",
                Advice = BugChecks.Advice(CrashCause.Graphics).Concat(new[]
                {
                    "Если сбой только в одной игре — снизьте в ней настройки и отключите оверлеи (Discord, RivaTuner, NVIDIA).",
                }).ToList(),
                Evidence = resets.Select(e => new Evidence(e.Time, "Драйвер перезапущен: " + (e.At(0) ?? "видеодрайвер"))).ToList(),
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
            return new() { Ok("whea", "Оборудование", "Аппаратных ошибок не было", "Процессор, память и шина PCI Express не сообщали об ошибках (WHEA).") };

        var list = new List<Finding>();
        var fatal = whea.Where(e => e.Level is >= 1 and <= 2).ToList();
        var corrected = whea.Where(e => e.Level >= 3).ToList();

        if (fatal.Count > 0)
        {
            list.Add(new Finding
            {
                Id = "whea-fatal",
                Category = "Оборудование",
                Severity = Severity.Critical,
                Title = $"Неисправимые аппаратные ошибки: {fatal.Count}",
                Summary = "Оборудование сообщило об ошибке, которую не смогло исправить, — обычно после этого бывает синий экран WHEA_UNCORRECTABLE_ERROR. " +
                          "Частые причины: нестабильный разгон или андервольт, перегрев, проблемы питания, реже — неисправный процессор или память.",
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
                Category = "Оборудование",
                Severity = cpu || count7 >= 10 ? Severity.Warning : Severity.Info,
                Title = $"Исправленные аппаратные ошибки: {corrected.Count}",
                Summary = string.Join(" ", byId.Select(g => $"{WheaText(g.Key)} — {Times(g.Count())}.")) +
                          " Сами по себе исправленные ошибки не опасны, но частые — признак нестабильности.",
                Advice = cpu
                    ? BugChecks.Advice(CrashCause.Hardware)
                    : new[]
                    {
                        "Ошибки шины PCI Express часто вызывает энергосбережение PCIe: в «Электропитании» → «Изменить доп. параметры» → «PCI Express» → «Управление питанием состояния связи» поставьте «Откл.».",
                        "Обновите BIOS и драйвер чипсета; если видеокарта подключена через райзер — проверьте его.",
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
        17 => "исправленная ошибка шины PCI Express (видеокарта, NVMe-диск или слот)",
        19 => "процессор исправил внутреннюю ошибку — частая причина: разгон или андервольт",
        47 => "память исправила ошибку (ECC)",
        18 or 1 => "неисправимая ошибка процессора (machine check)",
        20 => "неисправимая ошибка шины PCI Express",
        46 => "неисправимая ошибка памяти",
        _ => "аппаратная ошибка",
    };

    // ───────────────────────────── ошибки дисков ─────────────────────────────

    private static List<Finding> DiskErrors(DiagnosticInput input)
    {
        var events = input.Events.Where(e => StorageProviders.Contains(e.Provider) && e.Level is >= 1 and <= 3)
            .OrderByDescending(e => e.Time).ToList();
        if (events.Count == 0)
            return new() { Ok("disk-errors", "Диски", "Ошибок дисков в журнале нет", "Диски и контроллеры не сообщали об ошибках чтения, записи или сбросах.") };

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
                Category = "Диски",
                Severity = badBlocks || count7 >= 10 ? Severity.Critical : onlyRemovals ? Severity.Info : Severity.Warning,
                Title = onlyRemovals ? $"Диски отключались внезапно: {Times(events.Count)}" : $"Ошибки дисков: {events.Count} за {Period(input)}",
                Summary = string.Join("; ", kinds) + "." +
                          (badBlocks ? " Повреждённые сектора — серьёзный признак износа: сделайте резервную копию важных данных." : ""),
                Advice = onlyRemovals
                    ? new[] { "Если вы сами отключали флешку или внешний диск — всё в порядке. Иначе проверьте кабель и разъём." }
                    : BugChecks.Advice(CrashCause.Storage).Prepend(badBlocks
                        ? "Сначала сделайте резервную копию важных файлов с этого диска."
                        : "Посмотрите, к какому диску относятся ошибки (номер диска в записях ниже).").ToList(),
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
                7 => (Severity.Critical, "на диске найдены повреждённые сектора"),
                11 => (Severity.Warning, "ошибка контроллера при обращении к диску"),
                51 => (Severity.Warning, "ошибка во время записи страниц памяти на диск"),
                153 => (Severity.Warning, "диск не ответил вовремя, Windows повторила операцию"),
                157 => (Severity.Info, "диск был внезапно отключён"),
                _ => (Severity.Warning, $"ошибка диска (код события {e.Id})"),
            };
        if (p.Contains("ntfs"))
            return (Severity.Warning, "повреждение файловой системы NTFS");
        if (e.Id == 129)
            return (Severity.Warning, "контроллер сбрасывал диск — подвисание на несколько секунд");
        return (Severity.Warning, $"ошибка хранилища: {e.Provider} {e.Id}");
    }

    private static string DiskSuffix(EventItem e, DiagnosticInput input)
    {
        foreach (var d in e.Data)
        {
            var m = HarddiskPattern().Match(d);
            if (!m.Success || !int.TryParse(m.Groups[1].Value, out int n)) continue;
            return input.DiskNames.TryGetValue(n, out var name) ? $" · диск {n} ({name})" : $" · диск {n}";
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
                issues.Add("Windows считает диск неисправным");
                severity = Severity.Critical;
            }
            else if (health == "Warning")
            {
                issues.Add("Windows предупреждает о проблемах с диском");
                severity = Severity.Critical;
            }
            if (d.WearPercent is int wear && wear >= 90)
            {
                issues.Add($"ресурс записи израсходован на {wear}%");
                severity = Max(severity, Severity.Warning);
            }
            if (d.ReadErrorsUncorrected is long re && re > 0)
            {
                issues.Add($"неисправленных ошибок чтения: {re}");
                severity = Max(severity, Severity.Warning);
            }
            int hot = d.Media.Contains("HDD", StringComparison.OrdinalIgnoreCase) ? 55 : 70;
            if (d.Temperature is int t && t >= hot)
            {
                issues.Add($"температура {t} °C");
                severity = Max(severity, Severity.Warning);
            }

            string details = DiskDetails(d);
            if (issues.Count == 0)
            {
                okEvidence.Add(new Evidence(null, $"{d.Name}: исправен{details}"));
                continue;
            }

            list.Add(new Finding
            {
                Id = "disk-health-" + d.Name,
                Category = "Диски",
                Severity = severity,
                Title = $"Диск «{d.Name}»: {issues[0]}",
                Summary = string.Join("; ", issues) + "." +
                          (severity == Severity.Critical ? " Скопируйте важные данные с этого диска как можно скорее." : ""),
                Advice = new[]
                {
                    "Сделайте резервную копию важных файлов.",
                    "Посмотрите подробные данные SMART программой производителя диска или CrystalDiskInfo.",
                    "Перегрев: проверьте обдув диска; для NVMe-дисков помогает радиатор.",
                },
                Evidence = new[] { new Evidence(null, $"{d.Name}{details}") },
                Technical = $"Get-PhysicalDisk: HealthStatus={d.Health}, Media={d.Media}, Bus={d.Bus}",
            });
        }

        if (list.Count == 0)
            list.Add(Ok("disk-health", "Диски", "Диски исправны", "По данным Windows все физические диски в порядке.", okEvidence));
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
        if (d.WearPercent is int w) parts.Add($"износ {w}%");
        if (d.Temperature is int t && t > 0) parts.Add($"{t} °C");
        if (d.PowerOnHours is long h && h > 0) parts.Add($"{h:N0} ч работы".Replace(",", " "));
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
                Category = "Диски",
                Severity = s,
                Title = $"Мало места на диске {v.Letter}",
                Summary = $"Свободно {Ru.Bytes(v.FreeBytes)} из {Ru.Bytes(v.TotalBytes)} ({pct:0}%)." +
                          (system
                              ? " Когда на системном диске мало места, Windows не может установить обновления, а программы и игры начинают сбоить при записи."
                              : ""),
                Advice = new[]
                {
                    "Включите «Контроль памяти» или запустите «Очистку диска» → «Очистить системные файлы».",
                    "Удалите ненужные игры и программы, перенесите большие файлы на другой диск.",
                    "Не удаляйте вручную папки Windows (WinSxS, Installer) — это ломает обновления.",
                },
                Evidence = new[] { new Evidence(null, $"{v.Letter} {v.Label}: свободно {Ru.Bytes(v.FreeBytes)} из {Ru.Bytes(v.TotalBytes)}") },
                Actions = system ? new[] { StorageSettings, DiskCleanup } : new[] { StorageSettings },
            });
        }
        if (list.Count == 0 && input.Volumes.Count > 0)
            list.Add(Ok("space", "Диски", "Места на дисках достаточно", "На системном диске больше 10% свободного места.",
                input.Volumes.Where(v => v.TotalBytes > 0)
                    .Select(v => new Evidence(null, $"{v.Letter}: свободно {Ru.Bytes(v.FreeBytes)} из {Ru.Bytes(v.TotalBytes)}")).ToList()));
        return list;
    }
}
