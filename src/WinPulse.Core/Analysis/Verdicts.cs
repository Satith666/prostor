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
            : Lang.T($" Больше всего нагружает «{top.DisplayName}» — {Ru.Percent(top.CpuAvg1m)} в среднем за минуту.", $" The heaviest is “{top.DisplayName}” — {Ru.Percent(top.CpuAvg1m)} on average over a minute.") +
              (top.Hint is null ? "" : " " + top.Hint);

        if (cpu >= 90)
            list.Add(new Verdict("cpu", Severity.Critical, Lang.T($"Процессор перегружен: {Ru.Percent(cpu)}", $"CPU is overloaded: {Ru.Percent(cpu)}"),
                Lang.T("Программы ждут своей очереди, всё будет подтормаживать.", "Programs are waiting their turn, everything will lag.") + culprit, top?.Key));
        else if (cpu >= 70)
            list.Add(new Verdict("cpu", Severity.Warning, Lang.T($"Процессор сильно загружен: {Ru.Percent(cpu)}", $"CPU is heavily loaded: {Ru.Percent(cpu)}"),
                Lang.T("Запаса почти нет — тяжёлая задача или игра может замедлиться.", "There is almost no headroom — a heavy task or game may slow down.") + culprit, top?.Key));
        else
            list.Add(new Verdict("cpu", Severity.Ok, Lang.T("Процессор справляется", "CPU is coping"),
                Lang.T($"Средняя загрузка за 30 секунд — {Ru.Percent(cpu)}.", $"Average load over 30 seconds: {Ru.Percent(cpu)}.") + (cpu >= 30 ? culprit : "")));

        if (s.Cpu.PerformancePercent is double perf && perf < 50 && cpu > 50 && s.Cpu.EffectiveMHz is double mhz)
            list.Add(new Verdict("cpu", Severity.Warning, Lang.T("Процессор работает на пониженной частоте", "CPU is running at a reduced clock"),
                Lang.T($"Сейчас {Ru.GHz(mhz)} при загрузке {Ru.Percent(cpu)}. Частые причины: схема питания «Экономия энергии», ", $"Now {Ru.GHz(mhz)} at {Ru.Percent(cpu)} load. Common causes: the Power saver plan, ") +
                Lang.T("работа ноутбука от батареи или перегрев.", "a laptop running on battery, or overheating.")));
    }

    private static void Memory(RawSnapshot s, IReadOnlyList<AppGroup> apps, SystemHistory h, MemoryBreakdown mem, List<Verdict> list)
    {
        var m = s.Memory;
        if (m.TotalPhysical <= 0) return;
        double availPct = m.Available * 100.0 / m.TotalPhysical;
        double faults = h.HardFaults.AverageOfLast(10);
        double commitPct = m.CommitLimit > 0 ? m.CommitTotal * 100.0 / m.CommitLimit : 0;
        var top = apps.OrderByDescending(a => a.PrivateMemory).FirstOrDefault();
        string culprit = top is null ? "" : Lang.T($" Больше всего памяти у «{top.DisplayName}» — {Ru.Bytes(top.PrivateMemory)}.", $" The biggest user is “{top.DisplayName}” — {Ru.Bytes(top.PrivateMemory)}.");

        if (faults > 1000 && availPct < 10)
            list.Add(new Verdict("memory", Severity.Critical, Lang.T("Не хватает оперативной памяти", "Not enough RAM"),
                Lang.T($"Windows постоянно подгружает данные с диска ({faults:0} стр./с) — отсюда подтормаживания.", $"Windows keeps paging data in from disk ({faults:0} pages/s) — that causes the stutter.") + culprit, top?.Key));
        else if (availPct < 10 || (faults > 300 && availPct < 15))
            list.Add(new Verdict("memory", Severity.Warning, Lang.T("Оперативная память почти закончилась", "RAM is almost full"),
                Lang.T($"Свободно и в кэше осталось {Ru.Bytes(m.Available)}. Закройте лишние программы.", $"Only {Ru.Bytes(m.Available)} free or in cache. Close programs you don't need.") + culprit, top?.Key));
        else if (commitPct > 90)
            list.Add(new Verdict("memory", Severity.Warning, Lang.T("Почти исчерпан лимит выделения памяти", "Commit limit almost reached"),
                Lang.T($"Выделено {Ru.Bytes(m.CommitTotal)} из {Ru.Bytes(m.CommitLimit)}. Если файл подкачки отключён или мал, ", $"{Ru.Bytes(m.CommitTotal)} of {Ru.Bytes(m.CommitLimit)} committed. If the page file is off or small, ") +
                Lang.T("программы могут закрываться с ошибкой нехватки памяти.", "programs may close with out-of-memory errors.") + culprit, top?.Key));
        else if (m.UsedPercent >= 80)
            list.Add(new Verdict("memory", Severity.Ok, Lang.T($"Память занята на {Ru.Percent(m.UsedPercent)}, но Windows справляется", $"Memory is {Ru.Percent(m.UsedPercent)} used, but Windows is coping"),
                Lang.T($"Подкачки с диска почти нет. Кэш ({Ru.Bytes(mem.Standby)}) отдаётся программам мгновенно — чистить его не нужно.", $"There is almost no paging from disk. The cache ({Ru.Bytes(mem.Standby)}) is handed to programs instantly — no need to clear it.")));
        else
            list.Add(new Verdict("memory", Severity.Ok, Lang.T("Памяти хватает", "Enough memory"),
                Lang.T($"Занято {Ru.Bytes(mem.InUse)} из {Ru.Bytes(mem.Total)}, ещё {Ru.Bytes(mem.Standby)} — кэш, который Windows отдаст по первому требованию.", $"{Ru.Bytes(mem.InUse)} of {Ru.Bytes(mem.Total)} in use, plus {Ru.Bytes(mem.Standby)} of cache that Windows gives back on demand.")));
    }

    private static void Leaks(IReadOnlyList<AppGroup> apps, List<Verdict> list)
    {
        foreach (var a in apps.Where(a => a.LooksLikeLeak).OrderByDescending(a => a.MemorySlopePerHour).Take(3))
            list.Add(new Verdict("leak", Severity.Warning, Lang.T($"«{a.DisplayName}» постоянно набирает память", $"“{a.DisplayName}” keeps growing in memory"),
                Lang.T($"+{Ru.Bytes(a.MemorySlopePerHour)} в час, сейчас {Ru.Bytes(a.PrivateMemory)}. Похоже на утечку памяти: ", $"+{Ru.Bytes(a.MemorySlopePerHour)} per hour, now {Ru.Bytes(a.PrivateMemory)}. Looks like a memory leak: ") +
                Lang.T("если программа мешает — перезапустите её.", "if the program gets in the way, restart it."), a.Key));
    }

    private static void Disks(RawSnapshot s, IReadOnlyList<AppGroup> apps, SystemHistory h, List<Verdict> list)
    {
        bool any = false;
        var topIo = apps.OrderByDescending(a => a.IoBps).FirstOrDefault();
        string culprit = topIo is null || topIo.IoBps < 512 * 1024
            ? ""
            : Lang.T($" Больше всего читает и пишет «{topIo.DisplayName}» — {Ru.Rate(topIo.IoBps)}.", $" The busiest is “{topIo.DisplayName}” — {Ru.Rate(topIo.IoBps)}.");

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
                list.Add(new Verdict("disk", Severity.Critical, Lang.T($"Диск {name} перегружен", $"Disk {name} is overloaded"),
                    Lang.T($"Занят {Ru.Percent(active)} времени, отвечает за {Ru.Ms(latency)}.", $"Busy {Ru.Percent(active)} of the time, responding in {Ru.Ms(latency)}.") + culprit, topIo?.Key));
            }
            else if (active > 90 && latency > warn)
            {
                any = true;
                list.Add(new Verdict("disk", Severity.Warning, Lang.T($"Диск {name} сильно загружен", $"Disk {name} is heavily loaded"),
                    Lang.T($"Занят {Ru.Percent(active)} времени, отвечает за {Ru.Ms(latency)}.", $"Busy {Ru.Percent(active)} of the time, responding in {Ru.Ms(latency)}.") + culprit, topIo?.Key));
            }
            else if (active > 90)
            {
                any = true;
                list.Add(new Verdict("disk", Severity.Info, Lang.T($"Диск {name} занят, но отвечает быстро", $"Disk {name} is busy but responds fast"),
                    Lang.T($"Время ответа {Ru.Ms(latency)} — это нормально.", $"Response time {Ru.Ms(latency)} — that's normal.") + culprit, topIo?.Key));
            }
        }

        if (!any && s.Disks.Count > 0)
            list.Add(new Verdict("disk", Severity.Ok, Lang.T("Диски справляются", "Disks are coping"), Lang.T("Очередей и долгих ответов нет.", "No queues or slow responses.")));
    }

    private static void Gpus(RawSnapshot s, IReadOnlyList<AppGroup> apps, SystemHistory h, List<Verdict> list)
    {
        foreach (var g in s.Gpus)
        {
            double util = h.Gpus.TryGetValue(g.Luid, out var gs) ? gs.Util.AverageOfLast(10) : g.UtilPercent;
            if (g.DedicatedTotal > 0 && g.DedicatedUsed > g.DedicatedTotal * 0.9)
                list.Add(new Verdict("gpu", Severity.Warning, Lang.T("Видеопамять почти заполнена", "Video memory is almost full"),
                    Lang.T($"{g.Name}: занято {Ru.Bytes(g.DedicatedUsed)} из {Ru.Bytes(g.DedicatedTotal)}. Игра начнёт брать текстуры ", $"{g.Name}: {Ru.Bytes(g.DedicatedUsed)} of {Ru.Bytes(g.DedicatedTotal)} used. The game will start pulling textures ") +
                    Lang.T("из оперативной памяти — появятся подтормаживания. Снизьте качество текстур.", "from system RAM — expect stutter. Lower texture quality.")));
            if (util > 95)
                list.Add(new Verdict("gpu", Severity.Info, Lang.T("Видеокарта загружена полностью", "GPU is fully loaded"),
                    Lang.T($"{g.Name}: {Ru.Percent(util)}. Если сейчас идёт игра — это нормально: она упирается в видеокарту.", $"{g.Name}: {Ru.Percent(util)}. If a game is running, that's normal: it's GPU-bound.")));
        }

        foreach (var a in apps)
        {
            if (a.GpuPercent < 10) continue;
            if (!a.Processes.Any(p => BackgroundGpuApps.Contains(p.Name))) continue;
            list.Add(new Verdict("gpu", Severity.Info, Lang.T($"«{a.DisplayName}» нагружает видеокарту на {Ru.Percent(a.GpuPercent)}", $"“{a.DisplayName}” loads the GPU by {Ru.Percent(a.GpuPercent)}"),
                Lang.T("В играх эти ресурсы пригодились бы. Поставьте программу на паузу или закройте её на время игры.", "Games could use these resources. Pause the program or close it while you play."), a.Key));
        }
    }

    public static string DiskName(DiskState d)
    {
        var letters = string.Join(" ", d.Volumes.Select(v => v.Letter));
        return letters.Length > 0 ? letters : $"№{d.Index}";
    }
}
