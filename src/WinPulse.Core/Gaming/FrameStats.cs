namespace WinPulse.Core.Gaming;

/// <summary>Один кадр из замера PresentMon.</summary>
/// <param name="FrameTimeMs">Время между кадрами (MsBetweenPresents) — из него считается FPS.</param>
/// <param name="GpuBusyMs">Сколько видеокарта реально работала над кадром (MsGPUBusy). null — нет данных.</param>
/// <param name="SyncInterval">Запрошенная игрой вертикальная синхронизация: 0 — выключена, 1+ — включена.</param>
public readonly record struct FrameSample(double FrameTimeMs, double? GpuBusyMs, int? SyncInterval = null);

/// <summary>Во что упирается игра.</summary>
public enum Bottleneck
{
    Unknown,
    /// <summary>Видеокарта занята почти всё время кадра.</summary>
    Gpu,
    /// <summary>Видеокарта ждёт: кадры не успевает готовить процессор или сама игра.</summary>
    CpuOrEngine,
    /// <summary>Кадры ровные и видеокарта не загружена — работает ограничитель кадров или VSync.</summary>
    Capped,
}

/// <summary>
/// Итог замера. Определения как в CapFrameX и обзорах:
/// «1% low» — FPS, ниже которого опускается самый медленный 1% кадров (99-й перцентиль времени кадра), «0,1% low» — то же для 0,1%.
/// </summary>
public sealed class FrameStats
{
    public int Frames { get; init; }
    public double DurationSec { get; init; }
    public double AvgFps { get; init; }
    public double Low1Fps { get; init; }
    public double Low01Fps { get; init; }
    public double MedianFrameMs { get; init; }
    public double P99FrameMs { get; init; }
    /// <summary>Резкие подвисания: кадр заметно дольше обычного.</summary>
    public int Stutters { get; init; }
    /// <summary>Секунды от начала замера, когда были подвисания (для графика и связи с нагрузкой).</summary>
    public IReadOnlyList<double> StutterTimesSec { get; init; } = [];
    /// <summary>Доля времени кадра, когда видеокарта занята (медиана). null — PresentMon не дал данных о видеокарте.</summary>
    public double? GpuBusyRatio { get; init; }
    public Bottleneck Bottleneck { get; init; }
    public bool VSync { get; init; }
    /// <summary>FPS по секундам — для графика.</summary>
    public IReadOnlyList<double> FpsPerSecond { get; init; } = [];

    /// <summary>Считает итог. null, если кадров слишком мало для выводов.</summary>
    public static FrameStats? Compute(IReadOnlyList<FrameSample> frames)
    {
        var valid = frames.Where(f => f.FrameTimeMs > 0 && f.FrameTimeMs < 5000).ToList();
        if (valid.Count < 20) return null;

        var times = valid.Select(f => f.FrameTimeMs).ToArray();
        double totalMs = times.Sum();
        var sorted = times.OrderBy(t => t).ToArray();
        double median = Percentile(sorted, 50);
        double p99 = Percentile(sorted, 99);
        double p999 = Percentile(sorted, 99.9);

        // Подвисание: кадр вдвое дольше обычного и при этом хотя бы на 8 мс дольше (иначе на 300 FPS любой шум — «фриз»).
        var stutters = new List<double>();
        double clock = 0;
        foreach (double t in times)
        {
            clock += t;
            if (t > median * 2 && t - median > 8) stutters.Add(clock / 1000.0);
        }

        double? gpuRatio = null;
        var busy = valid.Where(f => f.GpuBusyMs is > 0).Select(f => f.GpuBusyMs!.Value / f.FrameTimeMs).OrderBy(x => x).ToArray();
        if (busy.Length >= valid.Count / 2) gpuRatio = Math.Clamp(Percentile(busy, 50), 0, 1.5);

        var syncs = valid.Where(f => f.SyncInterval is not null).Select(f => f.SyncInterval!.Value).ToList();
        bool vsync = syncs.Count > 0 && syncs.Count(s => s >= 1) > syncs.Count / 2;

        // Насколько ровные кадры: отношение межквартильного размаха к медиане.
        double spread = (Percentile(sorted, 75) - Percentile(sorted, 25)) / Math.Max(median, 0.001);

        Bottleneck bottleneck = Bottleneck.Unknown;
        if (gpuRatio is double r)
        {
            if (r >= 0.85) bottleneck = Bottleneck.Gpu;
            else if ((vsync || spread < 0.06) && r < 0.8) bottleneck = Bottleneck.Capped;
            else if (r < 0.75) bottleneck = Bottleneck.CpuOrEngine;
        }
        else if (vsync && spread < 0.06)
        {
            bottleneck = Bottleneck.Capped;
        }

        return new FrameStats
        {
            Frames = valid.Count,
            DurationSec = totalMs / 1000.0,
            AvgFps = valid.Count * 1000.0 / totalMs,
            Low1Fps = 1000.0 / p99,
            Low01Fps = 1000.0 / p999,
            MedianFrameMs = median,
            P99FrameMs = p99,
            Stutters = stutters.Count,
            StutterTimesSec = stutters,
            GpuBusyRatio = gpuRatio,
            Bottleneck = bottleneck,
            VSync = vsync,
            FpsPerSecond = PerSecond(times),
        };
    }

    /// <summary>Перцентиль по отсортированному массиву с линейной интерполяцией.</summary>
    public static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        if (sorted.Length == 1) return sorted[0];
        double rank = p / 100.0 * (sorted.Length - 1);
        int lo = (int)Math.Floor(rank);
        int hi = Math.Min(lo + 1, sorted.Length - 1);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    private static double[] PerSecond(double[] times)
    {
        var result = new List<double>();
        double bucket = 0;
        int count = 0;
        foreach (double t in times)
        {
            bucket += t;
            count++;
            if (bucket >= 1000)
            {
                result.Add(count * 1000.0 / bucket);
                bucket = 0;
                count = 0;
            }
        }
        if (count > 0 && bucket >= 250) result.Add(count * 1000.0 / bucket);
        return result.ToArray();
    }

    /// <summary>Разница в процентах между двумя замерами: +12 — новый быстрее на 12%.</summary>
    public static double ChangePercent(double before, double after) => before <= 0 ? 0 : (after - before) * 100.0 / before;

    public string BottleneckText => Bottleneck switch
    {
        Bottleneck.Gpu => Lang.T("Игра упирается в видеокарту", "The game is GPU-bound"),
        Bottleneck.CpuOrEngine => Lang.T("Игра упирается в процессор или сам движок", "The game is CPU- or engine-bound"),
        Bottleneck.Capped => Lang.T("Кадры ограничены: работает ограничитель FPS или VSync", "Frames are capped by a limiter or VSync"),
        _ => Lang.T("Не удалось определить, во что упирается игра", "Couldn't tell what limits the game"),
    };

    public string BottleneckAdvice => Bottleneck switch
    {
        Bottleneck.Gpu => Lang.T(
            "Видеокарта занята почти всё время кадра. Больше FPS дадут: апскейлинг (DLSS, FSR, XeSS), ниже разрешение или тяжёлые настройки — тени, отражения, трассировка лучей. Закрытие фоновых программ почти ничего не даст.",
            "The GPU is busy for almost the whole frame. More FPS will come from upscaling (DLSS, FSR, XeSS), lower resolution or lighter shadows, reflections and ray tracing. Closing background apps will barely help."),
        Bottleneck.CpuOrEngine => Lang.T(
            "Видеокарта простаивает, ожидая кадры. Помогут: закрыть программы, которые грузят процессор, ниже дальность прорисовки и количество объектов, включить XMP для памяти. Снижение разрешения FPS почти не прибавит.",
            "The GPU sits idle waiting for frames. What helps: closing CPU-heavy apps, lower view distance and object density, enabling XMP for RAM. Lowering resolution will barely add FPS."),
        Bottleneck.Capped => Lang.T(
            "Это нормально, если ограничение стоит специально. Если нужно больше кадров — снимите ограничитель или VSync (с G-SYNC/FreeSync лучше ограничить на 3 кадра ниже частоты монитора).",
            "That's fine if the cap is intentional. For more frames, lift the limiter or VSync (with G-SYNC/FreeSync, cap 3 below the refresh rate instead)."),
        _ => Lang.T("PresentMon не передал время работы видеокарты — вывод только по FPS.", "PresentMon didn't report GPU time — conclusions are FPS-only."),
    };
}
