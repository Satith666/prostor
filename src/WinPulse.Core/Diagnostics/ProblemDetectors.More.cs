using System.Globalization;
using WinPulse.Core.Analysis;
using WinPulse.Core.Formatting;

namespace WinPulse.Core.Diagnostics;

public static partial class ProblemDetectors
{
    // ───────────────────────────── падения программ ─────────────────────────────

    private sealed record Crash(DateTime Time, string App, string? Module, string? Exception, string? Path);

    private static readonly HashSet<string> GpuModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "nvwgf2umx.dll", "nvwgf2um.dll", "nvd3dumx.dll", "nvd3dum.dll", "nvoglv64.dll", "nvoglv32.dll", "nvgpucomp64.dll",
        "nvcuda64.dll", "nvlddmkm.sys", "nvapi64.dll", "nvldumdx.dll",
        "amdxx64.dll", "amdxc64.dll", "atidxx64.dll", "atiumd64.dll", "atiumdag.dll", "amdvlk64.dll", "atig6pxx.dll", "atio6axx.dll",
        "igd10iumd64.dll", "igd12umd64.dll", "igxelpicd64.dll", "igc64.dll", "igdumdim64.dll", "ig9icd64.dll",
        "d3d11.dll", "d3d12.dll", "d3d12core.dll", "dxgi.dll", "vulkan-1.dll",
    };

    private static readonly Dictionary<string, string> OverlayModules = new(StringComparer.OrdinalIgnoreCase)
    {
        ["RTSSHooks64.dll"] = "RivaTuner Statistics Server",
        ["RTSSHooks.dll"] = "RivaTuner Statistics Server",
        ["gameoverlayrenderer64.dll"] = "оверлей Steam",
        ["gameoverlayrenderer.dll"] = "оверлей Steam",
        ["DiscordHook64.dll"] = "оверлей Discord",
        ["DiscordHook.dll"] = "оверлей Discord",
        ["nvspcap64.dll"] = "оверлей NVIDIA",
        ["GameOverlayRenderer64.dll"] = "оверлей Steam",
        ["EOSOVH-Win64-Shipping.dll"] = "оверлей Epic Games",
        ["igo64.dll"] = "оверлей EA App",
        ["MSIAfterburner.exe"] = "MSI Afterburner",
    };

    private static string ExceptionText(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "";
        string c = code.Trim().ToLowerInvariant();
        if (c.StartsWith("0x")) c = c[2..];
        return c switch
        {
            "c0000005" => "нарушение доступа к памяти",
            "c0000409" => "аварийное завершение (переполнение буфера или быстрый выход)",
            "c0000374" => "повреждение памяти программы (heap)",
            "e0434352" => "необработанное исключение .NET",
            "80000003" => "точка останова",
            "c00000fd" => "переполнение стека",
            "c0000417" => "недопустимый параметр",
            "c000041d" => "исключение в обратном вызове",
            "c0000142" => "ошибка инициализации библиотеки",
            "c0000135" => "не найдена нужная библиотека DLL",
            "c000001d" => "недопустимая инструкция процессора",
            "c0000006" => "ошибка чтения страницы (диск или сеть)",
            _ => "код " + code.Trim(),
        };
    }

    private static Crash? ParseCrash(EventItem e)
    {
        if (e.Id == 1026)
        {
            // .NET Runtime: «Application: Foo.exe\nFramework Version: …\nDescription: …\nException Info: System.X…»
            string text = e.At(0) ?? "";
            string? app = null, exception = null;
            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith("Application:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Приложение:", StringComparison.OrdinalIgnoreCase))
                    app ??= line[(line.IndexOf(':') + 1)..].Trim();
                else if (line.StartsWith("Exception Info:", StringComparison.OrdinalIgnoreCase) || line.StartsWith("Сведения об исключении:", StringComparison.OrdinalIgnoreCase))
                    exception ??= line[(line.IndexOf(':') + 1)..].Trim();
            }
            return app is null ? null : new Crash(e.Time, app, ".NET", exception, null);
        }

        string? name = e.Get("AppName", 0);
        if (string.IsNullOrWhiteSpace(name)) return null;
        return new Crash(e.Time, name.Trim(), e.Get("ModuleName", 3)?.Trim(), e.Get("ExceptionCode", 6), e.Get("AppPath", 10));
    }

    private static List<Finding> AppCrashes(DiagnosticInput input)
    {
        var crashes = input.Events
            .Where(e => e.Is(ApplicationError, 1000) || e.Is(DotNetRuntime, 1026))
            .Select(ParseCrash)
            .Where(c => c is not null)
            .Select(c => c!)
            .OrderByDescending(c => c.Time)
            .ToList();

        if (crashes.Count == 0)
            return new() { Ok("crashes", "Программы", "Программы не падали", $"За {Period(input)} в журнале нет записей об аварийном завершении программ.") };

        var list = new List<Finding>();
        var byApp = crashes.GroupBy(c => c.App, StringComparer.OrdinalIgnoreCase)
            .Select(g => (App: g.First().App, Items: g.ToList(), Count7: Within(g.Select(c => c.Time), input.Now, 7)))
            .OrderByDescending(g => g.Items.Count)
            .ToList();

        var notable = byApp.Where(g => g.Items.Count >= 2 || g.Count7 >= 1).Take(6).ToList();
        foreach (var g in notable)
        {
            var modules = g.Items.Where(c => !string.IsNullOrWhiteSpace(c.Module))
                .GroupBy(c => c.Module!, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(m => m.Count()).ToList();
            string? topModule = modules.FirstOrDefault()?.Key;
            var (summary, advice) = CrashAdvice(g.App, topModule, g.Items[0].Exception);

            list.Add(new Finding
            {
                Id = "crash-" + g.App.ToLowerInvariant(),
                Category = "Программы",
                Severity = g.Count7 >= 3 ? Severity.Warning : Severity.Info,
                Title = $"«{g.App}» аварийно завершалась: {Times(g.Items.Count)}",
                Summary = summary,
                Advice = advice,
                Evidence = g.Items.Take(15).Select(c => new Evidence(c.Time,
                    (c.Module is null ? "" : "модуль " + c.Module) +
                    (string.IsNullOrWhiteSpace(c.Exception) ? "" : (c.Module is null ? "" : " · ") + ExceptionText(c.Exception)))).ToList(),
                Count7 = g.Count7,
                Count30 = g.Items.Count,
                LastSeen = g.Items[0].Time,
                Technical = "Application: Application Error 1000 / .NET Runtime 1026" + (g.Items[0].Path is { } p ? "\n" + p : "") +
                            (modules.Count > 0 ? "\nМодули: " + string.Join(", ", modules.Select(m => $"{m.Key} ×{m.Count()}")) : ""),
                Actions = new[] { ReliabilityMonitor },
            });
        }

        var rest = byApp.Except(notable).ToList();
        if (rest.Count > 0)
        {
            list.Add(new Finding
            {
                Id = "crash-other",
                Category = "Программы",
                Severity = Severity.Info,
                Title = $"Другие программы с аварийными завершениями: {rest.Count}",
                Summary = "Редкие сбои случаются у любых программ. Если какая-то из них начнёт падать часто — она появится отдельным пунктом.",
                Evidence = rest.Take(15).Select(g => new Evidence(g.Items[0].Time, g.App + (g.Items[0].Module is { } m ? " · модуль " + m : ""))).ToList(),
                Count7 = rest.Sum(g => g.Count7),
                Count30 = rest.Sum(g => g.Items.Count),
                LastSeen = rest.Max(g => g.Items[0].Time),
            });
        }

        // Падают разные программы в разных модулях — подозрение на память или систему.
        var distinctApps = byApp.Count;
        var distinctModules = crashes.Where(c => c.Module is not null && !GpuModules.Contains(c.Module))
            .Select(c => c.Module!).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int memoryLike = crashes.Count(c => c.Exception?.Contains("c0000005", StringComparison.OrdinalIgnoreCase) == true ||
                                            c.Exception?.Contains("c0000374", StringComparison.OrdinalIgnoreCase) == true);
        if (distinctApps >= 5 && distinctModules >= 4 && memoryLike >= 5)
        {
            list.Add(new Finding
            {
                Id = "crash-many",
                Category = "Память",
                Severity = Severity.Warning,
                Title = $"Падают разные программы: {distinctApps}",
                Summary = "Сбои с ошибками памяти случаются у многих несвязанных программ и в разных модулях. " +
                          "Так бывает, когда нестабильна оперативная память (часто из-за разгона/XMP) или повреждены системные файлы.",
                Advice = BugChecks.Advice(CrashCause.Memory).Concat(BugChecks.Advice(CrashCause.System)).ToList(),
                Count30 = crashes.Count,
                Count7 = Within(crashes.Select(c => c.Time), input.Now, 7),
                LastSeen = crashes[0].Time,
                Actions = new[] { MemoryTest, ReliabilityMonitor },
            });
        }
        return list;
    }

    private static (string Summary, IReadOnlyList<string> Advice) CrashAdvice(string app, string? module, string? exception)
    {
        string ex = ExceptionText(exception);
        string exPart = ex.Length > 0 ? $" Тип ошибки: {ex}." : "";

        if (module is not null && GpuModules.Contains(module))
            return ($"Сбой происходит внутри видеодрайвера или DirectX ({module}).{exPart}",
                BugChecks.Advice(CrashCause.Graphics).Concat(new[] { "Проверьте, не включён ли в игре экспериментальный режим (DX12/Vulkan) — попробуйте другой." }).ToList());

        if (module is not null && OverlayModules.TryGetValue(module, out var overlay))
            return ($"Сбой происходит в модуле «{overlay}» ({module}), который встраивается в программу.{exPart}",
                new[]
                {
                    $"Отключите {overlay} для этой программы или полностью и проверьте, повторится ли.",
                    "Обновите программу оверлея до последней версии.",
                });

        if (module is not null && (module.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase) || module.Equals("clr.dll", StringComparison.OrdinalIgnoreCase) ||
                                   module == ".NET" || exception?.Contains("e0434352", StringComparison.OrdinalIgnoreCase) == true))
            return ($"Ошибка в самой программе (платформа .NET).{exPart}",
                new[] { "Обновите программу до последней версии.", "Если не помогает — сообщите разработчику, приложив время сбоя." });

        if (module is not null && module.Equals("ntdll.dll", StringComparison.OrdinalIgnoreCase) && exception?.Contains("c0000374", StringComparison.OrdinalIgnoreCase) == true)
            return ($"Программа повредила собственную память.{exPart}",
                new[]
                {
                    "Обновите программу; для игр — проверьте целостность файлов в Steam/лаунчере и отключите моды.",
                    "Если так падают многие программы — проверьте оперативную память.",
                });

        if (module is not null && (module.Equals(app, StringComparison.OrdinalIgnoreCase) || module.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            return ($"Ошибка в коде самой программы.{exPart}",
                new[]
                {
                    "Обновите программу до последней версии.",
                    "Для игр: проверьте целостность файлов в Steam/лаунчере, отключите моды и сторонние оверлеи.",
                });

        return ((module is null ? "Программа завершилась с ошибкой." : $"Сбой происходит в модуле {module}.") + exPart,
            new[]
            {
                "Обновите программу и драйверы, с которыми она работает (видео, звук, сеть).",
                "Для игр: проверьте целостность файлов в Steam/лаунчере и отключите оверлеи.",
                "Если модуль относится к стороннему ПО (антивирус, подсветка, перехват клавиатуры) — проверьте без него.",
            });
    }

    // ───────────────────────────── зависания программ ─────────────────────────────

    private static List<Finding> AppHangs(DiagnosticInput input)
    {
        var hangs = input.Events.Where(e => e.Is(ApplicationHang, 1002))
            .Select(e => (e.Time, App: (e.Get("AppName", 0) ?? "").Trim()))
            .Where(h => h.App.Length > 0)
            .OrderByDescending(h => h.Time)
            .ToList();
        if (hangs.Count == 0)
            return new() { Ok("hangs", "Программы", "Программы не зависали", "Windows не закрывала зависшие программы.") };

        var byApp = hangs.GroupBy(h => h.App, StringComparer.OrdinalIgnoreCase)
            .Select(g => (App: g.First().App, Count: g.Count(), Count7: Within(g.Select(x => x.Time), input.Now, 7), Last: g.Max(x => x.Time)))
            .OrderByDescending(g => g.Count).ToList();

        return new()
        {
            new Finding
            {
                Id = "hangs",
                Category = "Программы",
                Severity = byApp.Any(g => g.Count7 >= 5) ? Severity.Warning : Severity.Info,
                Title = $"Программы зависали и закрывались: {Times(hangs.Count)}",
                Summary = "Программа переставала отвечать, и её закрыли (вы или Windows). Чаще всего: " +
                          string.Join(", ", byApp.Take(3).Select(g => $"{g.App} ({Times(g.Count)})")) + ".",
                Advice = new[]
                {
                    "Если зависания совпадают с высокой загрузкой диска или памяти — посмотрите разделы «Диски» и «Память».",
                    "Обновите программу; для браузеров — отключите лишние расширения.",
                },
                Evidence = byApp.Take(10).Select(g => new Evidence(g.Last, $"{g.App}: {Times(g.Count)}")).ToList(),
                Count7 = byApp.Sum(g => g.Count7),
                Count30 = hangs.Count,
                LastSeen = hangs[0].Time,
                Technical = "Application: Application Hang 1002",
                Actions = new[] { ReliabilityMonitor },
            },
        };
    }

    // ───────────────────────────── устройства ─────────────────────────────

    private static List<Finding> Devices(DiagnosticInput input)
    {
        if (!input.DevicesChecked) return new();
        var broken = input.Devices.Where(d => d.ProblemCode is not (22 or 47)).ToList();
        var disabled = input.Devices.Where(d => d.ProblemCode == 22).ToList();
        var list = new List<Finding>();

        if (broken.Count > 0)
        {
            bool gpu = broken.Any(d => string.Equals(d.DeviceClass, "Display", StringComparison.OrdinalIgnoreCase) && d.ProblemCode is 43 or 10 or 31);
            var advice = new List<string>();
            if (broken.Any(d => d.ProblemCode is 28 or 18 or 39 or 31 or 37))
                advice.Add("Установите или переустановите драйвер с сайта производителя устройства (для ноутбука — с сайта производителя ноутбука).");
            if (broken.Any(d => d.ProblemCode == 43))
                advice.Add("Код 43: Windows остановила устройство. Для видеокарты — чистая переустановка драйвера; для USB — другой порт или кабель.");
            if (broken.Any(d => d.ProblemCode == 10))
                advice.Add("Код 10: обновите драйвер, переподключите устройство; для встроенных устройств — обновите BIOS и драйвер чипсета.");
            if (broken.Any(d => d.ProblemCode is 48 or 52))
                advice.Add("Драйвер заблокирован или без подписи — замените его официальной версией.");
            if (broken.Any(d => d.ProblemCode is 14 or 38))
                advice.Add("Перезагрузите компьютер.");
            advice.Add("В Диспетчере устройств: правый клик по устройству → «Свойства» — там точное описание ошибки.");

            list.Add(new Finding
            {
                Id = "devices",
                Category = "Устройства",
                Severity = gpu ? Severity.Critical : Severity.Warning,
                Title = gpu ? "Видеокарта остановлена Windows" : $"Устройства с ошибками: {broken.Count}",
                Summary = "В Диспетчере устройств у этих устройств жёлтый значок — они не работают или работают неправильно.",
                Advice = advice,
                Evidence = broken.Select(d => new Evidence(null, $"{d.Name} — {DeviceProblemCodes.Describe(d.ProblemCode)} (код {d.ProblemCode})")).ToList(),
                Technical = string.Join("\n", broken.Select(d => d.InstanceId)),
                Actions = new[] { DeviceManager },
            });
        }

        if (disabled.Count > 0)
        {
            list.Add(new Finding
            {
                Id = "devices-disabled",
                Category = "Устройства",
                Severity = Severity.Info,
                Title = $"Отключённые устройства: {disabled.Count}",
                Summary = "Эти устройства отключены вручную. Если вы отключали их сами — всё в порядке.",
                Evidence = disabled.Select(d => new Evidence(null, d.Name)).ToList(),
                Actions = new[] { DeviceManager },
            });
        }

        if (list.Count == 0)
            list.Add(Ok("devices", "Устройства", "Все устройства работают", "В Диспетчере устройств нет устройств с ошибками."));
        return list;
    }

    // ───────────────────────────── службы ─────────────────────────────

    private static List<Finding> Services(DiagnosticInput input)
    {
        var events = input.Events.Where(e => e.From(ServiceControlManager)).ToList();
        var crashed = events.Where(e => e.Id is 7031 or 7034).ToList();
        var failed = events.Where(e => e.Id is 7000 or 7009 or 7023).ToList();
        var list = new List<Finding>();

        if (crashed.Count > 0)
        {
            var byName = crashed.GroupBy(e => e.Get("param1", 0) ?? "?").OrderByDescending(g => g.Count()).ToList();
            list.Add(new Finding
            {
                Id = "services-crashed",
                Category = "Службы",
                Severity = byName.Any(g => g.Count() >= 5) ? Severity.Warning : Severity.Info,
                Title = $"Службы аварийно завершались: {Times(crashed.Count)}",
                Summary = "Служба неожиданно остановилась (Windows обычно перезапускает её сама). Чаще всего: " +
                          string.Join(", ", byName.Take(3).Select(g => $"«{g.Key}» ({Times(g.Count())})")) + ".",
                Advice = new[]
                {
                    "Если служба принадлежит сторонней программе — обновите или переустановите программу.",
                    "Если это служба Windows и сбои частые — проверьте системные файлы: «sfc /scannow» в командной строке от администратора.",
                },
                Evidence = byName.Take(10).Select(g => new Evidence(g.Max(e => e.Time), $"{g.Key}: {Times(g.Count())}")).ToList(),
                Count7 = Within(crashed.Select(e => e.Time), input.Now, 7),
                Count30 = crashed.Count,
                LastSeen = crashed.Max(e => e.Time),
                Technical = "System: Service Control Manager 7031, 7034",
                Actions = new[] { EventViewer },
            });
        }

        if (failed.Count > 0)
        {
            var byName = failed.GroupBy(e => e.Get("param1", 0) ?? "?").OrderByDescending(g => g.Count()).ToList();
            list.Add(new Finding
            {
                Id = "services-failed",
                Category = "Службы",
                Severity = Severity.Info,
                Title = $"Службы не запускались: {Times(failed.Count)}",
                Summary = "Часть служб не смогла запуститься или ответить вовремя. Часто это службы удалённых программ или драйверов — тогда это безвредно.",
                Advice = new[]
                {
                    "Если служба относится к удалённой программе — её остатки можно убрать переустановкой и удалением этой программы.",
                    "Если не запускается служба нужной программы — переустановите программу.",
                },
                Evidence = byName.Take(10).Select(g => new Evidence(g.Max(e => e.Time), $"{g.Key}: {Times(g.Count())}")).ToList(),
                Count7 = Within(failed.Select(e => e.Time), input.Now, 7),
                Count30 = failed.Count,
                LastSeen = failed.Max(e => e.Time),
                Technical = "System: Service Control Manager 7000, 7009, 7023",
                Actions = new[] { EventViewer },
            });
        }

        if (list.Count == 0)
            list.Add(Ok("services", "Службы", "Службы работают без сбоев", "Ни одна служба не завершалась аварийно."));
        return list;
    }

    // ───────────────────────────── обновления ─────────────────────────────

    private static List<Finding> Updates(DiagnosticInput input)
    {
        var events = input.Events.Where(e => e.From(WindowsUpdate)).ToList();
        var failures = events.Where(e => e.Id == 20).ToList();
        var successes = events.Where(e => e.Id == 19).ToList();

        var stillFailing = failures
            .GroupBy(e => (e.Get("updateTitle", 1) ?? e.At(0) ?? "?").Trim())
            .Where(g =>
            {
                var lastFail = g.Max(e => e.Time);
                return !successes.Any(s => string.Equals((s.Get("updateTitle", 0) ?? "").Trim(), g.Key, StringComparison.OrdinalIgnoreCase) && s.Time > lastFail);
            })
            .OrderByDescending(g => g.Count())
            .ToList();

        if (stillFailing.Count == 0)
            return new() { Ok("updates", "Обновления", "Обновления устанавливаются", "Нет обновлений, которые не удалось установить.") };

        return new()
        {
            new Finding
            {
                Id = "updates",
                Category = "Обновления",
                Severity = stillFailing.Any(g => g.Count() >= 2) ? Severity.Warning : Severity.Info,
                Title = $"Не устанавливаются обновления: {stillFailing.Count}",
                Summary = "Windows пыталась установить эти обновления, но не смогла, и успешной установки после этого не было.",
                Advice = new[]
                {
                    "Освободите место на системном диске (нужно хотя бы 10–15 ГБ).",
                    "Запустите «Параметры» → «Система» → «Устранение неполадок» → «Центр обновления Windows».",
                    "Проверьте системные файлы: «DISM /Online /Cleanup-Image /RestoreHealth», затем «sfc /scannow» (командная строка от администратора).",
                },
                Evidence = stillFailing.Take(10).Select(g =>
                {
                    var last = g.OrderByDescending(e => e.Time).First();
                    string code = last.Get("errorCode", 0) ?? "";
                    return new Evidence(last.Time, $"{g.Key}" + (code.Length > 0 ? $" · ошибка {code}" : "") + $" · попыток: {g.Count()}");
                }).ToList(),
                Count7 = Within(failures.Select(e => e.Time), input.Now, 7),
                Count30 = failures.Count,
                LastSeen = failures.Max(e => e.Time),
                Technical = "System: WindowsUpdateClient 20 (ошибка), 19 (успех)",
                Actions = new[] { UpdateSettings },
            },
        };
    }

    // ───────────────────────────── ограничение частоты процессора ─────────────────────────────

    private static List<Finding> Throttling(DiagnosticInput input)
    {
        var events = input.Events.Where(e => e.Is(ProcessorPower, 37)).OrderByDescending(e => e.Time).ToList();
        if (events.Count == 0)
            return new() { Ok("throttling", "Оборудование", "Прошивка не ограничивала процессор", "Нет записей о том, что процессор замедлялся из-за перегрева или питания.") };

        int count7 = Within(events.Select(e => e.Time), input.Now, 7);
        return new()
        {
            new Finding
            {
                Id = "throttling",
                Category = "Оборудование",
                Severity = count7 >= 20 ? Severity.Warning : Severity.Info,
                Title = $"Процессор замедлялся прошивкой: {Times(events.Count)}",
                Summary = "BIOS/прошивка ограничивала частоту процессора. Обычно это перегрев или ограничение питания " +
                          "(ноутбук на батарее, слабый блок питания, лимиты мощности в BIOS). В такие моменты игры и программы работают медленнее.",
                Advice = new[]
                {
                    "Проверьте температуру процессора под нагрузкой: выше 90–95 °C — почистите систему охлаждения, замените термопасту.",
                    "Для ноутбука: работайте от сети, используйте подставку с обдувом, проверьте режим производительности в фирменной утилите.",
                    "Проверьте схему электропитания (раздел «Игры в окне», пункт 12).",
                },
                Evidence = events.Take(10).Select(e => new Evidence(e.Time, "Частота процессора ограничена прошивкой")).ToList(),
                Count7 = count7,
                Count30 = events.Count,
                LastSeen = events[0].Time,
                Technical = "System: Kernel-Processor-Power 37",
                Actions = new[] { PowerOptions },
            },
        };
    }

    // ───────────────────────────── проверка памяти ─────────────────────────────

    private static List<Finding> MemoryTestResults(DiagnosticInput input)
    {
        var last = input.Events.Where(e => e.From(MemoryDiagnostics) && e.Id is 1101 or 1102).OrderByDescending(e => e.Time).FirstOrDefault();
        if (last is null) return new();
        if (last.Id == 1101)
            return new() { Ok("memtest", "Память", "Проверка памяти не нашла ошибок", $"Последняя проверка средствами Windows: {last.Time:dd.MM.yyyy}.") };

        return new()
        {
            new Finding
            {
                Id = "memtest",
                Category = "Память",
                Severity = Severity.Critical,
                Title = "Проверка памяти нашла ошибки",
                Summary = "Средство проверки памяти Windows обнаружило аппаратные ошибки оперативной памяти. Это может вызывать синие экраны, вылеты и повреждение файлов.",
                Advice = new[]
                {
                    "Если память разогнана или включён XMP/EXPO — отключите и проверьте снова.",
                    "Проверяйте планки по одной (MemTest86), чтобы найти неисправную, и замените её.",
                    "Проверьте, что планки установлены в рекомендуемые слоты (см. инструкцию к материнской плате).",
                },
                Evidence = new[] { new Evidence(last.Time, "Средство проверки памяти Windows: обнаружены ошибки") },
                Count30 = 1,
                LastSeen = last.Time,
                Technical = "System: MemoryDiagnostics-Results 1102",
                Actions = new[] { MemoryTest },
            },
        };
    }

    // ───────────────────────────── загрузка Windows ─────────────────────────────

    private static List<Finding> Boot(DiagnosticInput input)
    {
        if (!input.BootLogRead) return new();
        var boots = input.Events.Where(e => e.Is(BootPerformance, 100))
            .Select(e => (e.Time, Main: Ms(e.Get("MainPathBootTime")), Total: Ms(e.Get("BootTime"))))
            .Where(b => b.Main > 0)
            .OrderByDescending(b => b.Time)
            .ToList();
        if (boots.Count == 0) return new();

        var recent = boots.Take(5).Select(b => b.Main).OrderBy(x => x).ToList();
        double median = recent[recent.Count / 2] / 1000.0;
        var last = boots[0];

        var slowdowns = input.Events.Where(e => e.From(BootPerformance) && e.Id is >= 101 and <= 110)
            .Select(e => (Name: (e.Get("FriendlyName") is { Length: > 0 } fn ? fn : e.Get("Name")) ?? "?", Ms: Ms(e.Get("DegradationTime")), e.Id))
            .Where(s => s.Ms > 0)
            .GroupBy(s => s.Name)
            .Select(g => (Name: g.Key, Total: g.Sum(x => x.Ms), Count: g.Count(), Kind: SlowKind(g.First().Id)))
            .OrderByDescending(g => g.Total)
            .Take(8)
            .ToList();

        var evidence = boots.Take(5).Select(b => new Evidence(b.Time,
            $"до рабочего стола {b.Main / 1000.0:0.0} с, до полной готовности {b.Total / 1000.0:0.0} с")).ToList();
        evidence.AddRange(slowdowns.Select(s => new Evidence(null, $"замедлял: {s.Name} ({s.Kind}) — в сумме {s.Total / 1000.0:0.0} с за {Times(s.Count)}")));

        string mainText = $"{median.ToString("0.0", Ru.Culture)} с";
        if (median <= 30)
            return new() { Ok("boot", "Загрузка", $"Загрузка быстрая: {mainText} до рабочего стола", "Медиана последних загрузок Windows.", evidence) };

        return new()
        {
            new Finding
            {
                Id = "boot",
                Category = "Загрузка",
                Severity = median > 60 ? Severity.Warning : Severity.Info,
                Title = $"Медленная загрузка: {mainText} до рабочего стола",
                Summary = "Столько в среднем проходит от включения до появления рабочего стола (по последним загрузкам)." +
                          (slowdowns.Count > 0 ? $" Больше всего замедлял: {slowdowns[0].Name}." : ""),
                Advice = new[]
                {
                    "Уберите лишние программы из автозагрузки (Диспетчер задач → «Автозагрузка»; в WinPulse — раздел «Автозагрузка», скоро).",
                    "Если Windows стоит на жёстком диске (HDD) — перенос на SSD ускорит загрузку в разы.",
                    "Драйверы и службы из списка ниже обновите или удалите вместе с ненужными программами.",
                },
                Evidence = evidence,
                LastSeen = last.Time,
                Technical = "Microsoft-Windows-Diagnostics-Performance/Operational: 100, 101–110",
            },
        };
    }

    private static double Ms(string? s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;

    private static string SlowKind(int id) => id switch
    {
        101 => "программа",
        102 => "драйвер",
        103 => "служба",
        106 => "фоновая оптимизация",
        109 => "устройство",
        _ => "компонент",
    };

    // ───────────────────────────── файл подкачки ─────────────────────────────

    private static List<Finding> PageFile(DiagnosticInput input)
    {
        bool disabled = input.PageFileDisabled == true ||
                        (input.PageFileDisabled is null && input.CommitLimit > 0 && input.TotalPhysical > 0 &&
                         input.CommitLimit - input.TotalPhysical < 256L * 1024 * 1024);
        if (!disabled)
        {
            var okList = new List<Finding>();
            if (input.PageFileDisabled is not null)
                okList.Add(Ok("pagefile", "Память", "Файл подкачки включён", "Windows может временно выгрузить данные на диск, когда памяти не хватает."));
            return okList;
        }

        return new()
        {
            new Finding
            {
                Id = "pagefile",
                Category = "Память",
                Severity = Severity.Warning,
                Title = "Файл подкачки отключён",
                Summary = "Без файла подкачки при нехватке памяти программы и игры закрываются с ошибкой «недостаточно памяти», " +
                          "а некоторые игры не запускаются вовсе. Скорости это не добавляет — это распространённый миф.",
                Advice = new[]
                {
                    "Откройте «Параметры быстродействия» → «Дополнительно» → «Виртуальная память» → «Изменить».",
                    "Отметьте «Автоматически выбирать объём файла подкачки» и перезагрузите компьютер.",
                },
                Evidence = new[] { new Evidence(null, $"Лимит выделения памяти {Ru.Bytes(input.CommitLimit)} при {Ru.Bytes(input.TotalPhysical)} ОЗУ") },
                Technical = @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management → PagingFiles",
                Actions = new[] { PerformanceOptions },
            },
        };
    }

    // ───────────────────────────── перезагрузка ─────────────────────────────

    private static List<Finding> Reboot(DiagnosticInput input)
    {
        var list = new List<Finding>();
        if (input.PendingReboot.Count > 0)
        {
            bool updates = input.PendingReboot.Any(r => r.Contains("обновл", StringComparison.OrdinalIgnoreCase));
            list.Add(new Finding
            {
                Id = "reboot",
                Category = "Система",
                Severity = updates && input.Uptime.TotalDays >= 7 ? Severity.Warning : Severity.Info,
                Title = "Windows ждёт перезагрузки",
                Summary = "Установка закончится только после перезагрузки. До этого часть изменений не действует, а новые обновления могут не устанавливаться.",
                Advice = new[] { "Сохраните работу и перезагрузите компьютер, когда будет удобно." },
                Evidence = input.PendingReboot.Select(r => new Evidence(null, r)).ToList(),
            });
        }
        if (input.Uptime.TotalDays >= 14)
        {
            list.Add(new Finding
            {
                Id = "uptime",
                Category = "Система",
                Severity = Severity.Info,
                Title = $"Компьютер работает без перезагрузки {Ru.Duration(input.Uptime)}",
                Summary = "«Быстрый запуск» и сон не перезагружают Windows по-настоящему. Перезагрузка раз в неделю-две закрывает утечки памяти в драйверах и завершает установку обновлений.",
                Advice = new[] { "Используйте «Перезагрузку», а не «Завершение работы» — при включённом быстром запуске только она очищает ядро." },
            });
        }
        if (list.Count == 0)
            list.Add(Ok("reboot", "Система", "Перезагрузка не требуется", $"Компьютер работает {Ru.Duration(input.Uptime)}, отложенных установок нет."));
        return list;
    }

    // ───────────────────────────── хронология и заметки ─────────────────────────────

    /// <summary>0 — критично, 1 — ошибка, 2 — предупреждение, −1 — не учитываем.</summary>
    public static int Classify(EventItem e)
    {
        if (e.Is(Wer, 1001) || e.Is(KernelPower, 41) || e.Is(MemoryDiagnostics, 1102)) return 0;
        if (e.From(Whea)) return e.Level is >= 1 and <= 2 ? 0 : 2;
        if (StorageProviders.Contains(e.Provider))
        {
            if (e.Provider.Equals("disk", StringComparison.OrdinalIgnoreCase) && e.Id == 7) return 0;
            if (e.Provider.Equals("disk", StringComparison.OrdinalIgnoreCase) && e.Id == 157) return -1;
            return e.Level is >= 1 and <= 2 ? 1 : e.Level == 3 ? 2 : -1;
        }
        if (e.Is(ApplicationError, 1000) || e.Is(DotNetRuntime, 1026) || e.Is(Display, 4101) || e.Is(WindowsUpdate, 20)) return 1;
        if (e.From(ServiceControlManager)) return e.Id is 7031 or 7034 ? 1 : e.Id is 7000 or 7009 or 7023 ? 2 : -1;
        if (e.Is(ApplicationHang, 1002)) return 2;
        return -1;
    }

    private static IReadOnlyList<DayBucket> Timeline(DiagnosticInput input)
    {
        int days = Math.Max(1, input.PeriodDays);
        var first = DateOnly.FromDateTime(input.Now.AddDays(-(days - 1)));
        var counts = new int[days, 3];
        foreach (var e in input.Events)
        {
            int index = DateOnly.FromDateTime(e.Time).DayNumber - first.DayNumber;
            if (index < 0 || index >= days) continue;
            int kind = Classify(e);
            if (kind >= 0) counts[index, kind]++;
        }
        var result = new List<DayBucket>(days);
        for (int i = 0; i < days; i++)
            result.Add(new DayBucket(first.AddDays(i), counts[i, 0], counts[i, 1], counts[i, 2]));
        return result;
    }

    private static IReadOnlyList<string> Notes(DiagnosticInput input)
    {
        var notes = new List<string>();
        if (input.BootLogDenied)
            notes.Add("Время загрузки не проверено: журнал загрузки Windows доступен только администратору.");
        if (!input.DevicesChecked)
            notes.Add("Не удалось проверить устройства.");
        if (!input.DiskHealthChecked)
            notes.Add("Не удалось узнать здоровье дисков (PowerShell недоступен или не ответил).");
        else if (!input.IsElevated && input.DiskHealth.All(d => d.WearPercent is null && d.Temperature is null))
            notes.Add("Износ и температура дисков видны только при запуске от имени администратора.");
        notes.AddRange(input.Errors);
        return notes;
    }
}
