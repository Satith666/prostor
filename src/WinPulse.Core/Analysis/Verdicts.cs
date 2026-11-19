using WinPulse.Core.Formatting;
using WinPulse.Core.Model;

namespace WinPulse.Core.Analysis;

public enum Severity { Ok = 0, Info = 1, Warning = 2, Critical = 3 }

/// <summary>Вывод простыми словами: что происходит и кто виноват.</summary>
public sealed record Verdict(string Area, Severity Severity, string Title, string Detail, string? AppKey = null);

public static class VerdictRules
{
    /// <summary>Программы, которые часто нагружают видеокарту в фоне.</summary>
    public static readonly HashSet<string> BackgroundGpuApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "wallpaper32.exe", "wallpaper64.exe", "Lively.exe", "livelywpf.exe",
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "browser.exe", "vivaldi.exe",
        "Discord.exe", "NVIDIA Overlay.exe", "NVIDIA Share.exe", "GameBar.exe", "RTSS.exe", "obs64.exe",
    };

    public static List<Verdict> Evaluate(RawSnapshot s, IReadOnlyList<AppGroup> apps, SystemHistory h, MemoryBreakdown mem)
    {
        var list = new List<Verdict>();
        Cpu(s, apps, h, list);
        Memory(s, apps, h, mem, list);
        Leaks(apps, list);
        Disks(s, apps, h, list);
        Gpus(s, apps, h, list);
        return list.OrderByDescending(v => v.Severity).ToList();
    }

    private static void Cpu(RawSnapshot s, IReadOnlyList<AppGroup> apps, SystemHistory h, List<Verdict> list)
    {
        double cpu = h.Cpu.AverageOfLast(30);
        var top = apps.Where(a => !a.Key.StartsWith("sys:idle", StringComparison.Ordinal))
                      .OrderByDescending(a => a.CpuAvg1m).FirstOrDefault();
        string culprit = top is null || top.CpuAvg1m < 5
            ? ""
            : $" Больше всего нагружает «{top.DisplayName}» — {Ru.Percent(top.CpuAvg1m)} в среднем за минуту." +
              (top.Hint is null ? "" : " " + top.Hint);

        if (cpu >= 90)
            list.Add(new Verdict("cpu", Severity.Critical, $"Процессор перегружен: {Ru.Percent(cpu)}",
                "Программы ждут своей очереди, всё будет подтормаживать." + culprit, top?.Key));
        else if (cpu >= 70)
            list.Add(new Verdict("cpu", Severity.Warning, $"Процессор сильно загружен: {Ru.Percent(cpu)}",
                "Запаса почти нет — тяжёлая задача или игра может замедлиться." + culprit, top?.Key));
        else
            list.Add(new Verdict("cpu", Severity.Ok, "Процессор справляется",
                $"Средняя загрузка за 30 секунд — {Ru.Percent(cpu)}." + (cpu >= 30 ? culprit : "")));

        if (s.Cpu.PerformancePercent is double perf && perf < 50 && cpu > 50 && s.Cpu.EffectiveMHz is double mhz)
            list.Add(new Verdict("cpu", Severity.Warning, "Процессор работает на пониженной частоте",
                $"Сейчас {Ru.GHz(mhz)} при загрузке {Ru.Percent(cpu)}. Частые причины: схема питания «Экономия энергии», " +
                "работа ноутбука от батареи или перегрев."));
    }

    private static void Memory(RawSnapshot s, IReadOnlyList<AppGroup> apps, SystemHistory h, MemoryBreakdown mem, List<Verdict> list)
    {
        var m = s.Memory;
        if (m.TotalPhysical <= 0) return;
        double availPct = m.Available * 100.0 / m.TotalPhysical;
        double faults = h.HardFaults.AverageOfLast(10);
        double commitPct = m.CommitLimit > 0 ? m.CommitTotal * 100.0 / m.CommitLimit : 0;
        var top = apps.OrderByDescending(a => a.PrivateMemory).FirstOrDefault();
        string culprit = top is null ? "" : $" Больше всего памяти у «{top.DisplayName}» — {Ru.Bytes(top.PrivateMemory)}.";

        if (faults > 1000 && availPct < 10)
            list.Add(new Verdict("memory", Severity.Critical, "Не хватает оперативной памяти",
                $"Windows постоянно подгружает данные с диска ({faults:0} стр./с) — отсюда подтормаживания." + culprit, top?.Key));
        else if (availPct < 10 || (faults > 300 && availPct < 15))
            list.Add(new Verdict("memory", Severity.Warning, "Оперативная память почти закончилась",
                $"Свободно и в кэше осталось {Ru.Bytes(m.Available)}. Закройте лишние программы." + culprit, top?.Key));
        else if (commitPct > 90)
            list.Add(new Verdict("memory", Severity.Warning, "Почти исчерпан лимит выделения памяти",
                $"Выделено {Ru.Bytes(m.CommitTotal)} из {Ru.Bytes(m.CommitLimit)}. Если файл подкачки отключён или мал, " +
                "программы могут закрываться с ошибкой нехватки памяти." + culprit, top?.Key));
        else if (m.UsedPercent >= 80)
            list.Add(new Verdict("memory", Severity.Ok, $"Память занята на {Ru.Percent(m.UsedPercent)}, но Windows справляется",
                $"Подкачки с диска почти нет. Кэш ({Ru.Bytes(mem.Standby)}) отдаётся программам мгновенно — чистить его не нужно."));
        else
            list.Add(new Verdict("memory", Severity.Ok, "Памяти хватает",
                $"Занято {Ru.Bytes(mem.InUse)} из {Ru.Bytes(mem.Total)}, ещё {Ru.Bytes(mem.Standby)} — кэш, который Windows отдаст по первому требованию."));
    }

    private static void Leaks(IReadOnlyList<AppGroup> apps, List<Verdict> list)
    {
        foreach (var a in apps.Where(a => a.LooksLikeLeak).OrderByDescending(a => a.MemorySlopePerHour).Take(3))
            list.Add(new Verdict("leak", Severity.Warning, $"«{a.DisplayName}» постоянно набирает память",
                $"+{Ru.Bytes(a.MemorySlopePerHour)} в час, сейчас {Ru.Bytes(a.PrivateMemory)}. Похоже на утечку памяти: " +
                "если программа мешает — перезапустите её.", a.Key));
    }

    private static void Disks(RawSnapshot s, IReadOnlyList<AppGroup> apps, SystemHistory h, List<Verdict> list)
    {
        bool any = false;
        var topIo = apps.OrderByDescending(a => a.IoBps).FirstOrDefault();
        string culprit = topIo is null || topIo.IoBps < 512 * 1024
            ? ""
            : $" Больше всего читает и пишет «{topIo.DisplayName}» — {Ru.Rate(topIo.IoBps)}.";

        foreach (var d in s.Disks)
        {
            if (!h.Disks.TryGetValue(d.Index, out var series)) continue;
            double active = series.Active.AverageOfLast(10);
            double latency = series.Latency.AverageOfLast(10);
            bool solid = d.MediaType is DiskMediaType.Ssd or DiskMediaType.Nvme;
            double warn = solid ? 15 : 30;
            double crit = solid ? 50 : 80;
            string name = DiskName(d);

            if (active > 90 && latency > crit)
            {
                any = true;
                list.Add(new Verdict("disk", Severity.Critical, $"Диск {name} перегружен",
                    $"Занят {Ru.Percent(active)} времени, отвечает за {Ru.Ms(latency)}." + culprit, topIo?.Key));
            }
            else if (active > 90 && latency > warn)
            {
                any = true;
                list.Add(new Verdict("disk", Severity.Warning, $"Диск {name} сильно загружен",
                    $"Занят {Ru.Percent(active)} времени, отвечает за {Ru.Ms(latency)}." + culprit, topIo?.Key));
            }
            else if (active > 90)
            {
                any = true;
                list.Add(new Verdict("disk", Severity.Info, $"Диск {name} занят, но отвечает быстро",
                    $"Время ответа {Ru.Ms(latency)} — это нормально." + culprit, topIo?.Key));
            }
        }

        if (!any && s.Disks.Count > 0)
            list.Add(new Verdict("disk", Severity.Ok, "Диски справляются", "Очередей и долгих ответов нет."));
    }

    private static void Gpus(RawSnapshot s, IReadOnlyList<AppGroup> apps, SystemHistory h, List<Verdict> list)
    {
        foreach (var g in s.Gpus)
        {
            double util = h.Gpus.TryGetValue(g.Luid, out var gs) ? gs.Util.AverageOfLast(10) : g.UtilPercent;
            if (g.DedicatedTotal > 0 && g.DedicatedUsed > g.DedicatedTotal * 0.9)
                list.Add(new Verdict("gpu", Severity.Warning, "Видеопамять почти заполнена",
                    $"{g.Name}: занято {Ru.Bytes(g.DedicatedUsed)} из {Ru.Bytes(g.DedicatedTotal)}. Игра начнёт брать текстуры " +
                    "из оперативной памяти — появятся подтормаживания. Снизьте качество текстур."));
            if (util > 95)
                list.Add(new Verdict("gpu", Severity.Info, "Видеокарта загружена полностью",
                    $"{g.Name}: {Ru.Percent(util)}. Если сейчас идёт игра — это нормально: она упирается в видеокарту."));
        }

        foreach (var a in apps)
        {
            if (a.GpuPercent < 10) continue;
            if (!a.Processes.Any(p => BackgroundGpuApps.Contains(p.Name))) continue;
            list.Add(new Verdict("gpu", Severity.Info, $"«{a.DisplayName}» нагружает видеокарту на {Ru.Percent(a.GpuPercent)}",
                "В играх эти ресурсы пригодились бы. Поставьте программу на паузу или закройте её на время игры.", a.Key));
        }
    }

    public static string DiskName(DiskState d)
    {
        var letters = string.Join(" ", d.Volumes.Select(v => v.Letter));
        return letters.Length > 0 ? letters : $"№{d.Index}";
    }
}
