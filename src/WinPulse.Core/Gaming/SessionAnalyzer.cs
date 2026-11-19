using WinPulse.Core.Formatting;

namespace WinPulse.Core.Gaming;

/// <summary>Вывод по сессии. Level: good | info | warn | bad.</summary>
public sealed record SessionFinding(string Level, string Title, string Detail);

/// <summary>Сторонняя программа, которая отнимала ресурсы у игры (средние значения за всю сессию).</summary>
public sealed record Interferer(string Name, double AvgCpu, double AvgGpu, double PeakCpu, double AvgIo, double SharePercent, string? Advice);

/// <summary>Заметный момент: что-то резко нагрузило систему или просели кадры.</summary>
public sealed record SessionMoment(double T, string Text);

public sealed class SessionReport
{
    public required GameSessionRecord Record { get; init; }
    public double AvgCpu { get; init; }
    public double AvgGpu { get; init; }
    public double PeakMemPercent { get; init; }
    public double? AvgFps { get; init; }
    public double? LowFps { get; init; }
    public double? PeakCpuTemp { get; init; }
    public double? PeakGpuTemp { get; init; }
    public IReadOnlyList<SessionFinding> Findings { get; init; } = [];
    public IReadOnlyList<Interferer> Interferers { get; init; } = [];
    public IReadOnlyList<SessionMoment> Moments { get; init; } = [];
    public double[] CpuSeries { get; init; } = [];
    public double[] GpuSeries { get; init; } = [];
    public double[] FpsSeries { get; init; } = [];
    /// <summary>Одна строка — главное.</summary>
    public string Headline { get; init; } = "";
}

/// <summary>Разбор записанной сессии: во что упиралась игра, кто мешал и когда.</summary>
public static class SessionAnalyzer
{
    public static SessionReport Analyze(GameSessionRecord r)
    {
        var s = r.Samples;
        if (s.Count == 0) return new SessionReport { Record = r, Headline = Lang.T("Нет данных", "No data") };

        double avgCpu = s.Average(x => x.Cpu);
        double avgGpu = s.Average(x => x.Gpu);
        double avgGameGpu = s.Average(x => x.GameGpu);
        double peakMem = s.Max(x => x.MemPercent);
        var fps = s.Where(x => x.Fps is > 0).Select(x => x.Fps!.Value).ToArray();
        double? avgFps = fps.Length >= 10 ? fps.Average() : null;
        double? lowFps = fps.Length >= 10 ? FrameStats.Percentile(fps.OrderBy(v => v).ToArray(), 5) : null;

        var findings = new List<SessionFinding>();
        var interferers = Interferers(s);
        var moments = Moments(s);

        // Во что упиралась игра
        if (avgGpu >= 90)
            findings.Add(new("info", Lang.T("Игра упиралась в видеокарту", "The game was GPU-bound"),
                Lang.T($"Видеокарта в среднем загружена на {avgGpu:0}%. Больше FPS дадут апскейлинг (DLSS, FSR, XeSS) или настройки графики полегче; фоновые программы тут почти ни при чём.",
                    $"The GPU averaged {avgGpu:0}% load. Upscaling (DLSS, FSR, XeSS) or lighter graphics settings will add FPS; background apps barely matter here.")));
        else if (avgCpu >= 85)
            findings.Add(new("warn", Lang.T("Игра упиралась в процессор", "The game was CPU-bound"),
                Lang.T($"Процессор в среднем загружен на {avgCpu:0}%, а видеокарта — только на {avgGpu:0}%: она ждала кадры. Помогут: закрыть лишние программы, снизить дальность прорисовки и количество объектов, включить XMP для памяти.",
                    $"The CPU averaged {avgCpu:0}% while the GPU was at {avgGpu:0}%: it was waiting for frames. Close extra apps, lower view distance and object density, enable XMP.")));
        else if (avgGpu < 60 && avgCpu < 60 && avgGameGpu > 0)
            findings.Add(new("info", Lang.T("Запас по железу есть", "There's hardware headroom"),
                Lang.T($"И процессор ({avgCpu:0}%), и видеокарта ({avgGpu:0}%) работали не в полную силу. Скорее всего, стоит ограничитель кадров или VSync — или игра упирается в один поток процессора.",
                    $"Both the CPU ({avgCpu:0}%) and the GPU ({avgGpu:0}%) had headroom. Likely a frame cap or VSync — or the game is limited by a single CPU thread.")));

        // Сторонние программы
        double otherCpu = interferers.Sum(i => i.AvgCpu);
        double otherGpu = interferers.Sum(i => i.AvgGpu);
        if (interferers.Count > 0 && (otherCpu >= 8 || otherGpu >= 6))
        {
            var top = interferers.Take(3).Select(i => i.Name);
            findings.Add(new("warn", Lang.T("Фоновые программы отнимали ресурсы", "Background apps took resources"),
                Lang.T($"Вместе — около {otherCpu:0}% процессора и {otherGpu:0}% видеокарты. Больше всех: {string.Join(", ", top)}.",
                    $"Together about {otherCpu:0}% CPU and {otherGpu:0}% GPU. Top: {string.Join(", ", top)}.")));
        }

        // Память
        double avgFaults = s.Where(x => x.HardFaults is not null).Select(x => x.HardFaults!.Value).DefaultIfEmpty(0).Average();
        if (peakMem >= 92 || avgFaults >= 300)
            findings.Add(new("warn", Lang.T("Оперативной памяти не хватало", "RAM ran short"),
                Lang.T($"Занято до {peakMem:0}% памяти" + (avgFaults >= 300 ? $", Windows подкачивала с диска в среднем {avgFaults:0} раз в секунду" : "") +
                       ". Это даёт подвисания при подгрузке. Закройте браузер и другие тяжёлые программы перед игрой.",
                    $"Up to {peakMem:0}% RAM in use" + (avgFaults >= 300 ? $", {avgFaults:0} hard faults per second on average" : "") +
                    ". That causes hitches while loading. Close the browser and other heavy apps before playing.")));

        // Видеопамять
        if (r.VramTotal > 0)
        {
            long peakVram = s.Max(x => x.VramUsed);
            if (peakVram >= r.VramTotal * 0.95)
                findings.Add(new("warn", Lang.T("Видеопамять была заполнена", "Video memory was full"),
                    Lang.T($"Занято {Ru.Bytes(peakVram)} из {Ru.Bytes(r.VramTotal)}. Когда видеопамяти не хватает, игра подгружает текстуры из обычной памяти — появляются рывки. Снизьте качество текстур на одну ступень.",
                        $"{Ru.Bytes(peakVram)} of {Ru.Bytes(r.VramTotal)} in use. When VRAM runs out, textures stream from system RAM and the game stutters. Lower texture quality one step.")));
        }

        // Диск
        int busyDisk = s.Count(x => x.DiskActive >= 90);
        if (busyDisk >= Math.Max(10, s.Count / 10))
        {
            var io = interferers.Where(i => i.AvgIo >= 2_000_000).Select(i => i.Name).Take(2).ToList();
            findings.Add(new("warn", Lang.T("Диск был перегружен", "The disk was overloaded"),
                Lang.T($"Диск был занят почти полностью {busyDisk} с из {s.Count}." + (io.Count > 0 ? $" Читали и писали также: {string.Join(", ", io)}." : "") +
                       " В такие моменты игра ждёт данные — это заметно как фризы.",
                    $"The disk was nearly fully busy for {busyDisk} s of {s.Count}." + (io.Count > 0 ? $" Also reading/writing: {string.Join(", ", io)}." : "") +
                    " The game waits for data at those moments — you see it as freezes.")));
        }

        // Перегрев
        var (peakGpu, peakCpu) = Thermal(r, s, findings);

        if (avgFps is double a && lowFps is double low && low < a * 0.6)
            findings.Add(new("warn", Lang.T("Кадры заметно проседали", "Frame rate dipped noticeably"),
                Lang.T($"В среднем {a:0} FPS, но в худшие секунды — {low:0}. Посмотрите моменты ниже: что происходило в системе, когда FPS падал.",
                    $"{a:0} FPS on average but {low:0} in the worst seconds. See the moments below for what the system was doing.")));

        if (!findings.Any(f => f.Level is "warn" or "bad"))
            findings.Add(new("good", Lang.T("Ничего постороннего игре не мешало", "Nothing else got in the game's way"),
                Lang.T("Фоновые программы, память и диск в норме.", "Background apps, memory and disk were fine.")));

        string headline = findings.FirstOrDefault(f => f.Level is "warn" or "bad")?.Title ?? findings[0].Title;
        return new SessionReport
        {
            Record = r,
            AvgCpu = avgCpu,
            AvgGpu = avgGpu,
            PeakMemPercent = peakMem,
            AvgFps = avgFps,
            LowFps = lowFps,
            PeakCpuTemp = peakCpu,
            PeakGpuTemp = peakGpu,
            Findings = findings,
            Interferers = interferers,
            Moments = moments,
            CpuSeries = Downsample(s.Select(x => x.Cpu).ToArray(), 600),
            GpuSeries = Downsample(s.Select(x => x.Gpu).ToArray(), 600),
            FpsSeries = Downsample(s.Select(x => x.Fps ?? 0).ToArray(), 600),
            Headline = headline,
        };
    }

    /// <summary>
    /// Перегрев: 95-й перцентиль температуры (чтобы единичный выброс датчика не считался).
    /// Для видеокарты дополнительно — насколько падала частота в горячие секунды по сравнению с прохладными.
    /// </summary>
    private static (double? PeakGpu, double? PeakCpu) Thermal(GameSessionRecord r, List<SessionSample> s, List<SessionFinding> findings)
    {
        double? peakGpu = null, peakCpu = null;
        var gpu = s.Where(x => x.GpuTemp is not null).ToList();
        if (gpu.Count >= 30)
        {
            var temps = gpu.Select(x => x.GpuTemp!.Value).OrderBy(v => v).ToArray();
            peakGpu = temps[^1];
            double p95 = FrameStats.Percentile(temps, 95);
            if (p95 >= 85)
            {
                var hot = gpu.Where(x => x.GpuTemp >= p95 - 2 && x.GpuClock is > 0).Select(x => x.GpuClock!.Value).ToList();
                var cool = gpu.Where(x => x.GpuTemp < p95 - 8 && x.GpuClock is > 0 && x.Gpu >= 80).Select(x => x.GpuClock!.Value).ToList();
                double drop = hot.Count >= 10 && cool.Count >= 10 ? (cool.Average() - hot.Average()) * 100 / cool.Average() : 0;
                findings.Add(new("warn", Lang.T("Видеокарта перегревалась", "The GPU ran hot"),
                    Lang.T($"Температура доходила до {peakGpu:0} °C" + (drop >= 4 ? $", и в горячие моменты частота падала на {drop:0}% — это прямые потери FPS." : ".") +
                           " Почистите радиатор и вентиляторы от пыли, проверьте продув корпуса. Можно поднять обороты вентиляторов (MSI Afterburner) — частота станет стабильнее.",
                        $"Temperature reached {peakGpu:0} °C" + (drop >= 4 ? $", and at hot moments the clock dropped by {drop:0}% — a direct FPS loss." : ".") +
                        " Clean dust from the heatsink and fans, check case airflow. A more aggressive fan curve (MSI Afterburner) keeps clocks steadier.")));
            }
        }

        var cpu = s.Where(x => x.CpuTemp is not null).Select(x => x.CpuTemp!.Value).OrderBy(v => v).ToArray();
        if (cpu.Length >= 30)
        {
            peakCpu = cpu[^1];
            double p95 = FrameStats.Percentile(cpu, 95);
            double limit = r.CpuName.Contains("AMD", StringComparison.OrdinalIgnoreCase) ? 90 : 95;
            if (p95 >= limit)
                findings.Add(new("warn", Lang.T("Процессор работал у предельной температуры", "The CPU ran at its temperature limit"),
                    Lang.T($"До {peakCpu:0} °C — при такой температуре процессор сам снижает частоту. Проверьте кулер: крепление, пыль, термопасту (если ей больше 3–4 лет).",
                        $"Up to {peakCpu:0} °C — at this point the CPU lowers its own clocks. Check the cooler: mounting, dust, thermal paste (if older than 3–4 years).")));
        }
        return (peakGpu, peakCpu);
    }

    private static List<Interferer> Interferers(List<SessionSample> s)
    {
        var acc = new Dictionary<string, (double Cpu, double Gpu, double Peak, double Io)>(StringComparer.OrdinalIgnoreCase);
        foreach (var sample in s)
        {
            foreach (var o in sample.Others)
            {
                acc.TryGetValue(o.Name, out var a);
                acc[o.Name] = (a.Cpu + o.Cpu, a.Gpu + o.Gpu, Math.Max(a.Peak, o.Cpu), a.Io + o.Io);
            }
        }
        double n = s.Count;
        double totalLoad = acc.Values.Sum(v => v.Cpu + v.Gpu) / n;
        return acc
            .Select(kv => (Name: kv.Key, Cpu: kv.Value.Cpu / n, Gpu: kv.Value.Gpu / n, Peak: kv.Value.Peak, Io: kv.Value.Io / n))
            .Where(x => x.Cpu >= 1.5 || x.Gpu >= 1.5 || x.Io >= 3_000_000)
            .OrderByDescending(x => x.Cpu + x.Gpu * 1.5 + x.Io / 5_000_000)
            .Take(8)
            .Select(x => new Interferer(x.Name, x.Cpu, x.Gpu, x.Peak, x.Io,
                totalLoad > 0 ? (x.Cpu + x.Gpu) * 100 / totalLoad : 0, AdviceFor(x.Name)))
            .ToList();
    }

    /// <summary>
    /// Эпизоды: сторонняя программа заметно грузила систему или проседали кадры. Соседние секунды с одной причиной
    /// объединяются в один эпизод — «с 25:10, 1 мин 10 с: … грузил процессор до 30%, FPS падал до 44».
    /// </summary>
    private static List<SessionMoment> Moments(List<SessionSample> s)
    {
        var fps = s.Where(x => x.Fps is > 0).Select(x => x.Fps!.Value).OrderBy(v => v).ToArray();
        double fpsMedian = fps.Length > 0 ? FrameStats.Percentile(fps, 50) : 0;

        // Причина в каждую секунду: программа (нагрузка или диск) или просто провал FPS.
        static (string Key, string Kind, SessionApp? App) Cause(SessionSample x, double median)
        {
            var top = x.Others.OrderByDescending(o => o.Cpu + o.Gpu * 1.5).FirstOrDefault();
            if (top is not null && (top.Cpu >= 15 || top.Gpu >= 15)) return (top.Name, "load", top);
            var topIo = x.Others.OrderByDescending(o => o.Io).FirstOrDefault();
            if (x.DiskActive >= 95 && topIo is not null && topIo.Io >= 20_000_000) return (topIo.Name, "disk", topIo);
            if (x.Fps is double f && median > 0 && f < median * 0.6) return ("", "fps", null);
            return ("", "", null);
        }

        var episodes = new List<(string Key, string Kind, int From, int To)>();
        for (int i = 0; i < s.Count; i++)
        {
            var (key, kind, _) = Cause(s[i], fpsMedian);
            if (kind.Length == 0) continue;
            if (episodes.Count > 0)
            {
                var last = episodes[^1];
                if (last.Key == key && last.Kind == kind && s[i].T - s[last.To].T <= 6)
                {
                    episodes[^1] = (last.Key, last.Kind, last.From, i);
                    continue;
                }
            }
            episodes.Add((key, kind, i, i));
        }

        var result = new List<SessionMoment>();
        foreach (var e in episodes)
        {
            var range = s.Skip(e.From).Take(e.To - e.From + 1).ToList();
            double seconds = range[^1].T - range[0].T + 1;
            var rangeFps = range.Where(x => x.Fps is > 0).Select(x => x.Fps!.Value).ToList();
            double? minFps = rangeFps.Count > 0 ? rangeFps.Min() : null;
            bool fpsHit = minFps is double m && fpsMedian > 0 && m < fpsMedian * 0.75;
            if (e.Kind != "fps" && seconds < 3 && !fpsHit) continue; // мимолётный всплеск без последствий — не шумим

            string fpsPart = minFps is null ? ""
                : fpsHit ? Lang.T($" FPS падал до {minFps:0} (обычно {fpsMedian:0}).", $" FPS dropped to {minFps:0} (usually {fpsMedian:0}).")
                : Lang.T(" На FPS это заметно не повлияло.", " FPS wasn't noticeably affected.");
            string length = Seconds(seconds);
            string text;
            if (e.Kind == "load")
            {
                double cpu = range.Max(x => Find(x, e.Key)?.Cpu ?? 0), gpu = range.Max(x => Find(x, e.Key)?.Gpu ?? 0);
                var ru = new List<string>();
                var en = new List<string>();
                if (cpu >= 10) { ru.Add($"процессор до {cpu:0}%"); en.Add($"the CPU up to {cpu:0}%"); }
                if (gpu >= 10) { ru.Add($"видеокарту до {gpu:0}%"); en.Add($"the GPU up to {gpu:0}%"); }
                text = Lang.T($"«{e.Key}» грузил {string.Join(" и ", ru)} в течение {length}.",
                    $"“{e.Key}” loaded {string.Join(" and ", en)} for {length}.") + fpsPart;
            }
            else if (e.Kind == "disk")
            {
                string rate = Ru.Rate(range.Max(x => Find(x, e.Key)?.Io ?? 0));
                text = Lang.T($"Диск был занят полностью в течение {length}: «{e.Key}» читал и писал до {rate}.",
                    $"The disk was fully busy for {length}: “{e.Key}” moved up to {rate}.") + fpsPart;
            }
            else
            {
                bool disk = range.Any(x => x.DiskActive >= 90);
                text = Lang.T(
                    $"FPS падал до {minFps:0} (обычно {fpsMedian:0}) в течение {length}. Сторонние программы ни при чём" +
                    (disk ? ", но диск в это время был занят — похоже на подгрузку уровня." : " — вероятно, тяжёлая сцена или подгрузка."),
                    $"FPS dropped to {minFps:0} (usually {fpsMedian:0}) for {length}. Not caused by other apps" +
                    (disk ? ", but the disk was busy — looks like level streaming." : " — likely a heavy scene or loading."));
            }
            result.Add(new SessionMoment(range[0].T, text));
        }

        // Сначала то, что било по FPS, потом остальное; в списке — по времени.
        return result
            .OrderByDescending(m => m.Text.Contains(Lang.T("FPS падал", "FPS dropped"), StringComparison.Ordinal))
            .Take(8)
            .OrderBy(m => m.T)
            .ToList();
    }

    private static SessionApp? Find(SessionSample x, string name) => x.Others.FirstOrDefault(o => o.Name == name);

    private static string Seconds(double seconds)
    {
        int total = (int)Math.Round(seconds);
        if (total < 60) return Lang.T($"{total} с", $"{total} s");
        int min = total / 60, sec = total % 60;
        return sec == 0 ? Lang.T($"{min} мин", $"{min} min") : Lang.T($"{min} мин {sec} с", $"{min} min {sec} s");
    }

    private static string? AdviceFor(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("wallpaper")) return Lang.T("В настройках Wallpaper Engine → «Производительность» поставьте «Пауза», когда другое приложение развёрнуто.", "In Wallpaper Engine → Performance, set “Pause” when another app is maximized.");
        if (n.Contains("chrome") || n.Contains("edge") || n.Contains("firefox") || n.Contains("opera") || n.Contains("яндекс") || n.Contains("yandex"))
            return Lang.T("Закройте вкладки с видео и стримами или весь браузер на время игры.", "Close tabs with video and streams, or the whole browser while playing.");
        if (n.Contains("discord")) return Lang.T("Отключите оверлей Discord и аппаратное ускорение в его настройках, если не нужны.", "Turn off Discord's overlay and hardware acceleration if you don't need them.");
        if (n.Contains("obs")) return Lang.T("Запись и трансляция всегда стоят ресурсов — это ожидаемо. Кодировщик NVENC нагружает видеокарту меньше, чем x264 — процессор.", "Recording and streaming always cost resources. NVENC loads the GPU less than x264 loads the CPU.");
        if (n.Contains("steam")) return Lang.T("Проверьте, не скачивает ли Steam обновления во время игры (Настройки → Загрузки).", "Check that Steam isn't downloading updates while you play (Settings → Downloads).");
        if (n.Contains("антивирус") || n.Contains("antimalware") || n.Contains("defender") || n.Contains("msmpeng"))
            return Lang.T("Проверка антивирусом. Добавьте папку игры в исключения — не отключайте защиту целиком.", "An antivirus scan. Add the game folder to exclusions — don't turn protection off entirely.");
        if (n.Contains("update") || n.Contains("обновлен") || n.Contains("tiworker") || n.Contains("trustedinstaller"))
            return Lang.T("Обновление Windows. Задайте «Период активности» в Центре обновления, чтобы оно не шло во время игр.", "Windows Update. Set active hours in Windows Update so it doesn't run while you play.");
        if (n.Contains("search") || n.Contains("поиск")) return Lang.T("Индексирование поиска Windows — обычно кратковременно.", "Windows Search indexing — usually short-lived.");
        return null;
    }

    private static double[] Downsample(double[] values, int max)
    {
        if (values.Length <= max) return values;
        var result = new double[max];
        double step = values.Length / (double)max;
        for (int i = 0; i < max; i++)
        {
            int from = (int)(i * step), to = Math.Min(values.Length, (int)((i + 1) * step));
            double sum = 0;
            for (int j = from; j < to; j++) sum += values[j];
            result[i] = to > from ? sum / (to - from) : values[from];
        }
        return result;
    }
}
