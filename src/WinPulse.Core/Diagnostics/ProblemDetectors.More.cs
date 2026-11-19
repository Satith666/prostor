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
            "c0000005" => Lang.T("нарушение доступа к памяти", "memory access violation"),
            "c0000409" => Lang.T("аварийное завершение (переполнение буфера или быстрый выход)", "abnormal termination (buffer overrun or fail-fast)"),
            "c0000374" => Lang.T("повреждение памяти программы (heap)", "program memory (heap) corruption"),
            "e0434352" => Lang.T("необработанное исключение .NET", "unhandled .NET exception"),
            "80000003" => Lang.T("точка останова", "breakpoint"),
            "c00000fd" => Lang.T("переполнение стека", "stack overflow"),
            "c0000417" => Lang.T("недопустимый параметр", "invalid parameter"),
            "c000041d" => Lang.T("исключение в обратном вызове", "exception in a callback"),
            "c0000142" => Lang.T("ошибка инициализации библиотеки", "DLL initialization failed"),
            "c0000135" => Lang.T("не найдена нужная библиотека DLL", "a required DLL was not found"),
            "c000001d" => Lang.T("недопустимая инструкция процессора", "illegal CPU instruction"),
            "c0000006" => Lang.T("ошибка чтения страницы (диск или сеть)", "page read error (disk or network)"),
            _ => Lang.T("код ", "code ") + code.Trim(),
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
            return new() { Ok("crashes", Lang.T("Программы", "Programs"), Lang.T("Программы не падали", "No program crashes"), Lang.T($"За {Period(input)} в журнале нет записей об аварийном завершении программ.", $"In {Period(input)} the log has no program crash records.")) };

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
                Category = Lang.T("Программы", "Programs"),
                Severity = g.Count7 >= 3 ? Severity.Warning : Severity.Info,
                Title = Lang.T($"«{g.App}» аварийно завершалась: {Times(g.Items.Count)}", $"“{g.App}” crashed: {Times(g.Items.Count)}"),
                Summary = summary,
                Advice = advice,
                Evidence = g.Items.Take(15).Select(c => new Evidence(c.Time,
                    (c.Module is null ? "" : Lang.T("модуль ", "module ") + c.Module) +
                    (string.IsNullOrWhiteSpace(c.Exception) ? "" : (c.Module is null ? "" : " · ") + ExceptionText(c.Exception)))).ToList(),
                Count7 = g.Count7,
                Count30 = g.Items.Count,
                LastSeen = g.Items[0].Time,
                Technical = "Application: Application Error 1000 / .NET Runtime 1026" + (g.Items[0].Path is { } p ? "\n" + p : "") +
                            (modules.Count > 0 ? Lang.T("\nМодули: ", "\nModules: ") + string.Join(", ", modules.Select(m => $"{m.Key} ×{m.Count()}")) : ""),
                Actions = new[] { ReliabilityMonitor },
            });
        }

        var rest = byApp.Except(notable).ToList();
        if (rest.Count > 0)
        {
            list.Add(new Finding
            {
                Id = "crash-other",
                Category = Lang.T("Программы", "Programs"),
                Severity = Severity.Info,
                Title = Lang.T($"Другие программы с аварийными завершениями: {rest.Count}", $"Other programs that crashed: {rest.Count}"),
                Summary = Lang.T("Редкие сбои случаются у любых программ. Если какая-то из них начнёт падать часто — она появится отдельным пунктом.", "Occasional crashes happen to any program. If one starts crashing often, it will get its own entry."),
                Evidence = rest.Take(15).Select(g => new Evidence(g.Items[0].Time, g.App + (g.Items[0].Module is { } m ? Lang.T(" · модуль ", " · module ") + m : ""))).ToList(),
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
                Category = Lang.T("Память", "Memory"),
                Severity = Severity.Warning,
                Title = Lang.T($"Падают разные программы: {distinctApps}", $"Different programs are crashing: {distinctApps}"),
                Summary = Lang.T("Сбои с ошибками памяти случаются у многих несвязанных программ и в разных модулях. ", "Memory-error crashes hit many unrelated programs in different modules. ") +
                          Lang.T("Так бывает, когда нестабильна оперативная память (часто из-за разгона/XMP) или повреждены системные файлы.", "This happens when RAM is unstable (often due to overclocking/XMP) or system files are corrupted."),
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
        string exPart = ex.Length > 0 ? Lang.T($" Тип ошибки: {ex}.", $" Error type: {ex}.") : "";

        if (module is not null && GpuModules.Contains(module))
            return (Lang.T($"Сбой происходит внутри видеодрайвера или DirectX ({module}).{exPart}", $"The crash happens inside the graphics driver or DirectX ({module}).{exPart}"),
                BugChecks.Advice(CrashCause.Graphics).Concat(new[] { Lang.T("Проверьте, не включён ли в игре экспериментальный режим (DX12/Vulkan) — попробуйте другой.", "Check whether the game uses an experimental mode (DX12/Vulkan) — try a different one.") }).ToList());

        if (module is not null && OverlayModules.TryGetValue(module, out var overlayRu))
        {
            string overlay = Lang.English && overlayRu.StartsWith("оверлей ", StringComparison.Ordinal) ? overlayRu[8..] + " overlay" : overlayRu;
            return (Lang.T($"Сбой происходит в модуле «{overlay}» ({module}), который встраивается в программу.{exPart}", $"The crash happens in the “{overlay}” module ({module}), which hooks into the program.{exPart}"),
                new[]
                {
                    Lang.T($"Отключите {overlay} для этой программы или полностью и проверьте, повторится ли.", $"Turn off {overlay} for this program or entirely and see whether it happens again."),
                    Lang.T("Обновите программу оверлея до последней версии.", "Update the overlay software to the latest version."),
                });
        }

        if (module is not null && (module.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase) || module.Equals("clr.dll", StringComparison.OrdinalIgnoreCase) ||
                                   module == ".NET" || exception?.Contains("e0434352", StringComparison.OrdinalIgnoreCase) == true))
            return (Lang.T($"Ошибка в самой программе (платформа .NET).{exPart}", $"An error in the program itself (.NET).{exPart}"),
                new[] { Lang.T("Обновите программу до последней версии.", "Update the program to the latest version."), Lang.T("Если не помогает — сообщите разработчику, приложив время сбоя.", "If that doesn't help, report it to the developer with the time of the crash.") });

        if (module is not null && module.Equals("ntdll.dll", StringComparison.OrdinalIgnoreCase) && exception?.Contains("c0000374", StringComparison.OrdinalIgnoreCase) == true)
            return (Lang.T($"Программа повредила собственную память.{exPart}", $"The program corrupted its own memory.{exPart}"),
                new[]
                {
                    Lang.T("Обновите программу; для игр — проверьте целостность файлов в Steam/лаунчере и отключите моды.", "Update the program; for games, verify file integrity in Steam/the launcher and turn off mods."),
                    Lang.T("Если так падают многие программы — проверьте оперативную память.", "If many programs crash like this, test the RAM."),
                });

        if (module is not null && (module.Equals(app, StringComparison.OrdinalIgnoreCase) || module.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)))
            return (Lang.T($"Ошибка в коде самой программы.{exPart}", $"An error in the program's own code.{exPart}"),
                new[]
                {
                    Lang.T("Обновите программу до последней версии.", "Update the program to the latest version."),
                    Lang.T("Для игр: проверьте целостность файлов в Steam/лаунчере, отключите моды и сторонние оверлеи.", "For games: verify file integrity in Steam/the launcher, turn off mods and third-party overlays."),
                });

        return ((module is null ? Lang.T("Программа завершилась с ошибкой.", "The program terminated with an error.") : Lang.T($"Сбой происходит в модуле {module}.", $"The crash happens in module {module}.")) + exPart,
            new[]
            {
                Lang.T("Обновите программу и драйверы, с которыми она работает (видео, звук, сеть).", "Update the program and the drivers it uses (graphics, audio, network)."),
                Lang.T("Для игр: проверьте целостность файлов в Steam/лаунчере и отключите оверлеи.", "For games: verify file integrity in Steam/the launcher and turn off overlays."),
                Lang.T("Если модуль относится к стороннему ПО (антивирус, подсветка, перехват клавиатуры) — проверьте без него.", "If the module belongs to third-party software (antivirus, RGB, keyboard hooks), test without it."),
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
            return new() { Ok("hangs", Lang.T("Программы", "Programs"), Lang.T("Программы не зависали", "No programs froze"), Lang.T("Windows не закрывала зависшие программы.", "Windows didn't close any hung programs.")) };

        var byApp = hangs.GroupBy(h => h.App, StringComparer.OrdinalIgnoreCase)
            .Select(g => (App: g.First().App, Count: g.Count(), Count7: Within(g.Select(x => x.Time), input.Now, 7), Last: g.Max(x => x.Time)))
            .OrderByDescending(g => g.Count).ToList();

        return new()
        {
            new Finding
            {
                Id = "hangs",
                Category = Lang.T("Программы", "Programs"),
                Severity = byApp.Any(g => g.Count7 >= 5) ? Severity.Warning : Severity.Info,
                Title = Lang.T($"Программы зависали и закрывались: {Times(hangs.Count)}", $"Programs hung and were closed: {Times(hangs.Count)}"),
                Summary = Lang.T("Программа переставала отвечать, и её закрыли (вы или Windows). Чаще всего: ", "A program stopped responding and was closed (by you or Windows). Most often: ") +
                          string.Join(", ", byApp.Take(3).Select(g => $"{g.App} ({Times(g.Count)})")) + ".",
                Advice = new[]
                {
                    Lang.T("Если зависания совпадают с высокой загрузкой диска или памяти — посмотрите разделы «Диски» и «Память».", "If the hangs coincide with high disk or memory load, check the Disks and Memory pages."),
                    Lang.T("Обновите программу; для браузеров — отключите лишние расширения.", "Update the program; for browsers, turn off unneeded extensions."),
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
                advice.Add(Lang.T("Установите или переустановите драйвер с сайта производителя устройства (для ноутбука — с сайта производителя ноутбука).", "Install or reinstall the driver from the device maker's site (for a laptop, from the laptop maker's site)."));
            if (broken.Any(d => d.ProblemCode == 43))
                advice.Add(Lang.T("Код 43: Windows остановила устройство. Для видеокарты — чистая переустановка драйвера; для USB — другой порт или кабель.", "Code 43: Windows stopped the device. For a GPU, do a clean driver reinstall; for USB, try another port or cable."));
            if (broken.Any(d => d.ProblemCode == 10))
                advice.Add(Lang.T("Код 10: обновите драйвер, переподключите устройство; для встроенных устройств — обновите BIOS и драйвер чипсета.", "Code 10: update the driver and reconnect the device; for built-in devices, update the BIOS and chipset driver."));
            if (broken.Any(d => d.ProblemCode is 48 or 52))
                advice.Add(Lang.T("Драйвер заблокирован или без подписи — замените его официальной версией.", "The driver is blocked or unsigned — replace it with the official version."));
            if (broken.Any(d => d.ProblemCode is 14 or 38))
                advice.Add(Lang.T("Перезагрузите компьютер.", "Restart the PC."));
            advice.Add(Lang.T("В Диспетчере устройств: правый клик по устройству → «Свойства» — там точное описание ошибки.", "In Device Manager: right-click the device → Properties shows the exact error."));

            list.Add(new Finding
            {
                Id = "devices",
                Category = Lang.T("Устройства", "Devices"),
                Severity = gpu ? Severity.Critical : Severity.Warning,
                Title = gpu ? Lang.T("Видеокарта остановлена Windows", "Windows stopped the GPU") : Lang.T($"Устройства с ошибками: {broken.Count}", $"Devices with errors: {broken.Count}"),
                Summary = Lang.T("В Диспетчере устройств у этих устройств жёлтый значок — они не работают или работают неправильно.", "These devices have a yellow mark in Device Manager — they don't work or don't work properly."),
                Advice = advice,
                Evidence = broken.Select(d => new Evidence(null, Lang.T($"{d.Name} — {DeviceProblemCodes.Describe(d.ProblemCode)} (код {d.ProblemCode})", $"{d.Name} — {DeviceProblemCodes.Describe(d.ProblemCode)} (code {d.ProblemCode})"))).ToList(),
                Technical = string.Join("\n", broken.Select(d => d.InstanceId)),
                Actions = new[] { DeviceManager },
            });
        }

        if (disabled.Count > 0)
        {
            list.Add(new Finding
            {
                Id = "devices-disabled",
                Category = Lang.T("Устройства", "Devices"),
                Severity = Severity.Info,
                Title = Lang.T($"Отключённые устройства: {disabled.Count}", $"Disabled devices: {disabled.Count}"),
                Summary = Lang.T("Эти устройства отключены вручную. Если вы отключали их сами — всё в порядке.", "These devices were disabled manually. If you did it yourself, all is well."),
                Evidence = disabled.Select(d => new Evidence(null, d.Name)).ToList(),
                Actions = new[] { DeviceManager },
            });
        }

        if (list.Count == 0)
            list.Add(Ok("devices", Lang.T("Устройства", "Devices"), Lang.T("Все устройства работают", "All devices work"), Lang.T("В Диспетчере устройств нет устройств с ошибками.", "Device Manager shows no devices with errors.")));
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
                Category = Lang.T("Службы", "Services"),
                Severity = byName.Any(g => g.Count() >= 5) ? Severity.Warning : Severity.Info,
                Title = Lang.T($"Службы аварийно завершались: {Times(crashed.Count)}", $"Services crashed: {Times(crashed.Count)}"),
                Summary = Lang.T("Служба неожиданно остановилась (Windows обычно перезапускает её сама). Чаще всего: ", "A service stopped unexpectedly (Windows usually restarts it). Most often: ") +
                          string.Join(", ", byName.Take(3).Select(g => $"«{g.Key}» ({Times(g.Count())})")) + ".",
                Advice = new[]
                {
                    Lang.T("Если служба принадлежит сторонней программе — обновите или переустановите программу.", "If the service belongs to a third-party program, update or reinstall that program."),
                    Lang.T("Если это служба Windows и сбои частые — проверьте системные файлы: «sfc /scannow» в командной строке от администратора.", "If it is a Windows service and crashes often, check system files: “sfc /scannow” in an administrator command prompt."),
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
                Category = Lang.T("Службы", "Services"),
                Severity = Severity.Info,
                Title = Lang.T($"Службы не запускались: {Times(failed.Count)}", $"Services failed to start: {Times(failed.Count)}"),
                Summary = Lang.T("Часть служб не смогла запуститься или ответить вовремя. Часто это службы удалённых программ или драйверов — тогда это безвредно.", "Some services couldn't start or respond in time. Often these belong to removed programs or drivers — then it's harmless."),
                Advice = new[]
                {
                    Lang.T("Если служба относится к удалённой программе — её остатки можно убрать переустановкой и удалением этой программы.", "If the service belongs to a removed program, its leftovers can be cleaned up by reinstalling and then uninstalling that program."),
                    Lang.T("Если не запускается служба нужной программы — переустановите программу.", "If a service of a program you need won't start, reinstall the program."),
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
            list.Add(Ok("services", Lang.T("Службы", "Services"), Lang.T("Службы работают без сбоев", "Services run without failures"), Lang.T("Ни одна служба не завершалась аварийно.", "No service crashed.")));
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
            return new() { Ok("updates", Lang.T("Обновления", "Updates"), Lang.T("Обновления устанавливаются", "Updates install fine"), Lang.T("Нет обновлений, которые не удалось установить.", "No updates failed to install.")) };

        return new()
        {
            new Finding
            {
                Id = "updates",
                Category = Lang.T("Обновления", "Updates"),
                Severity = stillFailing.Any(g => g.Count() >= 2) ? Severity.Warning : Severity.Info,
                Title = Lang.T($"Не устанавливаются обновления: {stillFailing.Count}", $"Updates failing to install: {stillFailing.Count}"),
                Summary = Lang.T("Windows пыталась установить эти обновления, но не смогла, и успешной установки после этого не было.", "Windows tried to install these updates but couldn't, and there was no successful install since."),
                Advice = new[]
                {
                    Lang.T("Освободите место на системном диске (нужно хотя бы 10–15 ГБ).", "Free up space on the system disk (at least 10–15 GB is needed)."),
                    Lang.T("Запустите «Параметры» → «Система» → «Устранение неполадок» → «Центр обновления Windows».", "Run Settings → System → Troubleshoot → Windows Update."),
                    Lang.T("Проверьте системные файлы: «DISM /Online /Cleanup-Image /RestoreHealth», затем «sfc /scannow» (командная строка от администратора).", "Check system files: “DISM /Online /Cleanup-Image /RestoreHealth”, then “sfc /scannow” (administrator command prompt)."),
                },
                Evidence = stillFailing.Take(10).Select(g =>
                {
                    var last = g.OrderByDescending(e => e.Time).First();
                    string code = last.Get("errorCode", 0) ?? "";
                    return new Evidence(last.Time, $"{g.Key}" + (code.Length > 0 ? Lang.T($" · ошибка {code}", $" · error {code}") : "") + Lang.T($" · попыток: {g.Count()}", $" · attempts: {g.Count()}"));
                }).ToList(),
                Count7 = Within(failures.Select(e => e.Time), input.Now, 7),
                Count30 = failures.Count,
                LastSeen = failures.Max(e => e.Time),
                Technical = Lang.T("System: WindowsUpdateClient 20 (ошибка), 19 (успех)", "System: WindowsUpdateClient 20 (failure), 19 (success)"),
                Actions = new[] { UpdateSettings },
            },
        };
    }

    // ───────────────────────────── ограничение частоты процессора ─────────────────────────────

    private static List<Finding> Throttling(DiagnosticInput input)
    {
        var events = input.Events.Where(e => e.Is(ProcessorPower, 37)).OrderByDescending(e => e.Time).ToList();
        if (events.Count == 0)
            return new() { Ok("throttling", Lang.T("Оборудование", "Hardware"), Lang.T("Прошивка не ограничивала процессор", "Firmware didn't throttle the CPU"), Lang.T("Нет записей о том, что процессор замедлялся из-за перегрева или питания.", "No records of the CPU slowing down due to heat or power.")) };

        int count7 = Within(events.Select(e => e.Time), input.Now, 7);
        return new()
        {
            new Finding
            {
                Id = "throttling",
                Category = Lang.T("Оборудование", "Hardware"),
                Severity = count7 >= 20 ? Severity.Warning : Severity.Info,
                Title = Lang.T($"Процессор замедлялся прошивкой: {Times(events.Count)}", $"Firmware throttled the CPU: {Times(events.Count)}"),
                Summary = Lang.T("BIOS/прошивка ограничивала частоту процессора. Обычно это перегрев или ограничение питания ", "The BIOS/firmware limited the CPU clock. Usually this is overheating or a power limit ") +
                          Lang.T("(ноутбук на батарее, слабый блок питания, лимиты мощности в BIOS). В такие моменты игры и программы работают медленнее.", "(a laptop on battery, a weak power supply, power limits in BIOS). At those moments games and programs run slower."),
                Advice = new[]
                {
                    Lang.T("Проверьте температуру процессора под нагрузкой: выше 90–95 °C — почистите систему охлаждения, замените термопасту.", "Check CPU temperature under load: above 90–95 °C, clean the cooling and replace the thermal paste."),
                    Lang.T("Для ноутбука: работайте от сети, используйте подставку с обдувом, проверьте режим производительности в фирменной утилите.", "For a laptop: run on AC power, use a cooling pad, check the performance mode in the maker's utility."),
                    Lang.T("Проверьте схему электропитания (раздел «Игры в окне», пункт 12).", "Check the power plan (Windowed games page, item 12)."),
                },
                Evidence = events.Take(10).Select(e => new Evidence(e.Time, Lang.T("Частота процессора ограничена прошивкой", "CPU clock limited by firmware"))).ToList(),
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
            return new() { Ok("memtest", Lang.T("Память", "Memory"), Lang.T("Проверка памяти не нашла ошибок", "The memory test found no errors"), Lang.T($"Последняя проверка средствами Windows: {last.Time:dd.MM.yyyy}.", $"Last Windows memory test: {last.Time:dd.MM.yyyy}.")) };

        return new()
        {
            new Finding
            {
                Id = "memtest",
                Category = Lang.T("Память", "Memory"),
                Severity = Severity.Critical,
                Title = Lang.T("Проверка памяти нашла ошибки", "The memory test found errors"),
                Summary = Lang.T("Средство проверки памяти Windows обнаружило аппаратные ошибки оперативной памяти. Это может вызывать синие экраны, вылеты и повреждение файлов.", "Windows Memory Diagnostic found hardware RAM errors. They can cause blue screens, crashes and file corruption."),
                Advice = new[]
                {
                    Lang.T("Если память разогнана или включён XMP/EXPO — отключите и проверьте снова.", "If the RAM is overclocked or XMP/EXPO is on, turn it off and test again."),
                    Lang.T("Проверяйте планки по одной (MemTest86), чтобы найти неисправную, и замените её.", "Test the sticks one at a time (MemTest86) to find the faulty one, and replace it."),
                    Lang.T("Проверьте, что планки установлены в рекомендуемые слоты (см. инструкцию к материнской плате).", "Check that the sticks are in the recommended slots (see the motherboard manual)."),
                },
                Evidence = new[] { new Evidence(last.Time, Lang.T("Средство проверки памяти Windows: обнаружены ошибки", "Windows Memory Diagnostic: errors found")) },
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
            Lang.T($"до рабочего стола {b.Main / 1000.0:0.0} с, до полной готовности {b.Total / 1000.0:0.0} с", $"to desktop {b.Main / 1000.0:0.0} s, fully ready {b.Total / 1000.0:0.0} s"))).ToList();
        evidence.AddRange(slowdowns.Select(s => new Evidence(null, Lang.T($"замедлял: {s.Name} ({s.Kind}) — в сумме {s.Total / 1000.0:0.0} с за {Times(s.Count)}", $"slowed by: {s.Name} ({s.Kind}) — {s.Total / 1000.0:0.0} s in total over {Times(s.Count)}"))));

        string mainText = median.ToString("0.0", Ru.Culture) + Lang.T(" с", " s");
        if (median <= 30)
            return new() { Ok("boot", Lang.T("Загрузка", "Boot"), Lang.T($"Загрузка быстрая: {mainText} до рабочего стола", $"Fast boot: {mainText} to desktop"), Lang.T("Медиана последних загрузок Windows.", "Median of recent Windows boots."), evidence) };

        return new()
        {
            new Finding
            {
                Id = "boot",
                Category = Lang.T("Загрузка", "Boot"),
                Severity = median > 60 ? Severity.Warning : Severity.Info,
                Title = Lang.T($"Медленная загрузка: {mainText} до рабочего стола", $"Slow boot: {mainText} to desktop"),
                Summary = Lang.T("Столько в среднем проходит от включения до появления рабочего стола (по последним загрузкам).", "Average time from power-on to desktop (recent boots).") +
                          (slowdowns.Count > 0 ? Lang.T($" Больше всего замедлял: {slowdowns[0].Name}.", $" The biggest slowdown: {slowdowns[0].Name}.") : ""),
                Advice = new[]
                {
                    Lang.T("Уберите лишние программы из автозагрузки (Диспетчер задач → «Автозагрузка»; в WinPulse — раздел «Автозагрузка», скоро).", "Remove unneeded programs from startup (Task Manager → Startup, or the Startup page in WinPulse)."),
                    Lang.T("Если Windows стоит на жёстком диске (HDD) — перенос на SSD ускорит загрузку в разы.", "If Windows is on a hard disk (HDD), moving it to an SSD will make boot several times faster."),
                    Lang.T("Драйверы и службы из списка ниже обновите или удалите вместе с ненужными программами.", "Update the drivers and services listed below, or remove them along with programs you no longer need."),
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
        101 => Lang.T("программа", "program"),
        102 => Lang.T("драйвер", "driver"),
        103 => Lang.T("служба", "service"),
        106 => Lang.T("фоновая оптимизация", "background optimization"),
        109 => Lang.T("устройство", "device"),
        _ => Lang.T("компонент", "component"),
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
                okList.Add(Ok("pagefile", Lang.T("Память", "Memory"), Lang.T("Файл подкачки включён", "Page file is on"), Lang.T("Windows может временно выгрузить данные на диск, когда памяти не хватает.", "Windows can temporarily move data to disk when memory runs short.")));
            return okList;
        }

        return new()
        {
            new Finding
            {
                Id = "pagefile",
                Category = Lang.T("Память", "Memory"),
                Severity = Severity.Warning,
                Title = Lang.T("Файл подкачки отключён", "Page file is off"),
                Summary = Lang.T("Без файла подкачки при нехватке памяти программы и игры закрываются с ошибкой «недостаточно памяти», ", "Without a page file, programs and games close with “out of memory” errors when memory runs short, ") +
                          Lang.T("а некоторые игры не запускаются вовсе. Скорости это не добавляет — это распространённый миф.", "and some games won't start at all. It doesn't add speed — that's a common myth."),
                Advice = new[]
                {
                    Lang.T("Откройте «Параметры быстродействия» → «Дополнительно» → «Виртуальная память» → «Изменить».", "Open Performance Options → Advanced → Virtual memory → Change."),
                    Lang.T("Отметьте «Автоматически выбирать объём файла подкачки» и перезагрузите компьютер.", "Check “Automatically manage paging file size” and restart the PC."),
                },
                Evidence = new[] { new Evidence(null, Lang.T($"Лимит выделения памяти {Ru.Bytes(input.CommitLimit)} при {Ru.Bytes(input.TotalPhysical)} ОЗУ", $"Commit limit {Ru.Bytes(input.CommitLimit)} with {Ru.Bytes(input.TotalPhysical)} of RAM")) },
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
            bool updates = input.PendingReboot.Any(r => (r.Contains("обновл", StringComparison.OrdinalIgnoreCase) || r.Contains("update", StringComparison.OrdinalIgnoreCase)));
            list.Add(new Finding
            {
                Id = "reboot",
                Category = Lang.T("Система", "System"),
                Severity = updates && input.Uptime.TotalDays >= 7 ? Severity.Warning : Severity.Info,
                Title = Lang.T("Windows ждёт перезагрузки", "Windows is waiting for a restart"),
                Summary = Lang.T("Установка закончится только после перезагрузки. До этого часть изменений не действует, а новые обновления могут не устанавливаться.", "Installation finishes only after a restart. Until then some changes don't apply and new updates may not install."),
                Advice = new[] { Lang.T("Сохраните работу и перезагрузите компьютер, когда будет удобно.", "Save your work and restart the PC when convenient.") },
                Evidence = input.PendingReboot.Select(r => new Evidence(null, r)).ToList(),
            });
        }
        if (input.Uptime.TotalDays >= 14)
        {
            list.Add(new Finding
            {
                Id = "uptime",
                Category = Lang.T("Система", "System"),
                Severity = Severity.Info,
                Title = Lang.T($"Компьютер работает без перезагрузки {Ru.Duration(input.Uptime)}", $"The PC has run without a restart for {Ru.Duration(input.Uptime)}"),
                Summary = Lang.T("«Быстрый запуск» и сон не перезагружают Windows по-настоящему. Перезагрузка раз в неделю-две закрывает утечки памяти в драйверах и завершает установку обновлений.", "Fast Startup and sleep don't truly restart Windows. A restart every week or two clears driver memory leaks and finishes update installs."),
                Advice = new[] { Lang.T("Используйте «Перезагрузку», а не «Завершение работы» — при включённом быстром запуске только она очищает ядро.", "Use Restart, not Shut down — with Fast Startup on, only a restart clears the kernel.") },
            });
        }
        if (list.Count == 0)
            list.Add(Ok("reboot", Lang.T("Система", "System"), Lang.T("Перезагрузка не требуется", "No restart needed"), Lang.T($"Компьютер работает {Ru.Duration(input.Uptime)}, отложенных установок нет.", $"Uptime {Ru.Duration(input.Uptime)}, no pending installs.")));
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
            notes.Add(Lang.T("Время загрузки не проверено: журнал загрузки Windows доступен только администратору.", "Boot time not checked: the Windows boot log is available only to administrators."));
        if (!input.DevicesChecked)
            notes.Add(Lang.T("Не удалось проверить устройства.", "Couldn't check devices."));
        if (!input.DiskHealthChecked)
            notes.Add(Lang.T("Не удалось узнать здоровье дисков (PowerShell недоступен или не ответил).", "Couldn't get disk health (PowerShell is unavailable or didn't respond)."));
        else if (!input.IsElevated && input.DiskHealth.All(d => d.WearPercent is null && d.Temperature is null))
            notes.Add(Lang.T("Износ и температура дисков видны только при запуске от имени администратора.", "Disk wear and temperature are visible only when running as administrator."));
        notes.AddRange(input.Errors);
        return notes;
    }
}
