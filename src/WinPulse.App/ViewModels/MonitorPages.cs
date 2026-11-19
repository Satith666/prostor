using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using WinPulse.App.Controls;
using WinPulse.App.Infrastructure;
using WinPulse.Core;
using WinPulse.Core.Analysis;
using WinPulse.Core.Formatting;
using WinPulse.Core.Model;

namespace WinPulse.App.ViewModels;

public abstract class PageViewModel : ObservableObject
{
    public abstract string Key { get; }

    /// <summary>Новый кадр мониторинга. Вызывается в потоке интерфейса только для открытой страницы (и обзора).</summary>
    public virtual void Update(MonitorFrame frame)
    {
    }

    public virtual void OnShown()
    {
    }

    protected static Brush Res(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
}

public sealed class KpiVm : ObservableObject
{
    private string _value = "";
    private string _sub = "";
    private double[] _spark = [];

    private readonly string _titleRu, _titleEn;

    public KpiVm(string titleRu, string titleEn, string target, Brush brush, double sparkMax, Action<string> navigate)
    {
        _titleRu = titleRu;
        _titleEn = titleEn;
        Lang.Changed += () => OnPropertyChanged(nameof(Title));
        Brush = brush;
        SparkMax = sparkMax;
        OpenCommand = new RelayCommand(() => navigate(target));
    }

    public string Title => Lang.English ? _titleEn : _titleRu;
    public Brush Brush { get; }
    public double SparkMax { get; }
    public ICommand OpenCommand { get; }
    public string Value { get => _value; set => Set(ref _value, value); }
    public string Sub { get => _sub; set => Set(ref _sub, value); }
    public double[] Spark { get => _spark; set => Set(ref _spark, value); }

    private double _percent;
    private string _main = "";
    /// <summary>Значение для кругового датчика, 0–100.</summary>
    public double Percent
    {
        get => _percent;
        set
        {
            if (Set(ref _percent, value)) OnPropertyChanged(nameof(PercentText));
        }
    }
    public string PercentText => double.IsNaN(_percent) ? "—" : Math.Round(_percent).ToString("0");
    /// <summary>Крупная строка рядом с датчиком: частота, «9,8 / 16 ГБ», имя видеокарты.</summary>
    public string Main { get => _main; set => Set(ref _main, value); }

    private string _short = "";
    /// <summary>Короткая подпись под крупной строкой на датчике.</summary>
    public string Short { get => _short; set => Set(ref _short, value); }
}

public sealed class VerdictVm : ObservableObject
{
    private string _title = "";
    private string _detail = "";
    private Severity _severity;

    public VerdictVm(string key) => Key = key;

    public string Key { get; }
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    public Severity Severity
    {
        get => _severity;
        set
        {
            if (Set(ref _severity, value)) OnPropertyChanged(nameof(Level));
        }
    }

    /// <summary>ok | info | warn | bad — для стилей.</summary>
    public string Level => _severity switch
    {
        Severity.Critical => "bad",
        Severity.Warning => "warn",
        Severity.Info => "info",
        _ => "ok",
    };
}

public sealed class OverviewViewModel : PageViewModel
{
    private string _systemLine = "";
    private string _metric = "cpu";

    public OverviewViewModel(Action<string> navigate)
    {
        Cpu = new KpiVm("Процессор", "Processor", "cpu", Res("Series.Cpu"), 100, navigate);
        Memory = new KpiVm("Память", "Memory", "memory", Res("Series.Memory"), 100, navigate);
        Disk = new KpiVm("Диски", "Disks", "disk", Res("Series.Disk"), 100, navigate);
        Gpu = new KpiVm("Видеокарта", "Graphics", "gpu", Res("Series.Gpu"), 100, navigate);
        Network = new KpiVm("Сеть", "Network", "network", Res("Series.Net"), double.NaN, navigate);
        Table = new AppTableViewModel("cpu");
        MetricCommand = new RelayCommand(p => Metric = p as string ?? "cpu");
        Kpis = new[] { Cpu, Memory, Disk, Gpu, Network };
        Rings = new[] { Cpu, Gpu, Memory, Disk };
    }

    public IReadOnlyList<KpiVm> Kpis { get; }
    /// <summary>Страница игр — для переключателя игрового режима в шапке «Пульта».</summary>
    public GamesViewModel? Games { get; set; }
    /// <summary>Четыре круговых датчика «Пульта».</summary>
    public IReadOnlyList<KpiVm> Rings { get; }

    private double[] _cpuChart = [];
    private double[] _gpuChart = [];
    private string _statusText = "";
    private string _statusLevel = "ok";
    private string _netText = "";
    public double[] CpuChart { get => _cpuChart; private set => Set(ref _cpuChart, value); }
    public double[] GpuChart { get => _gpuChart; private set => Set(ref _gpuChart, value); }
    /// <summary>«Всё спокойно» или «2 проблемы сейчас».</summary>
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    /// <summary>ok | warn | bad — цвет плашки статуса.</summary>
    public string StatusLevel { get => _statusLevel; private set => Set(ref _statusLevel, value); }
    public string NetText { get => _netText; private set => Set(ref _netText, value); }

    public override string Key => "overview";
    public KpiVm Cpu { get; }
    public KpiVm Memory { get; }
    public KpiVm Disk { get; }
    public KpiVm Gpu { get; }
    public KpiVm Network { get; }
    public ObservableCollection<VerdictVm> Verdicts { get; } = new();
    public AppTableViewModel Table { get; }
    public ICommand MetricCommand { get; }
    public string SystemLine { get => _systemLine; private set => Set(ref _systemLine, value); }

    public string Metric
    {
        get => _metric;
        set
        {
            if (!Set(ref _metric, value)) return;
            Table.SortKey = value;
            OnPropertyChanged(nameof(MetricCpu));
            OnPropertyChanged(nameof(MetricMemory));
            OnPropertyChanged(nameof(MetricDisk));
            OnPropertyChanged(nameof(MetricGpu));
        }
    }

    public bool MetricCpu { get => _metric == "cpu"; set { if (value) Metric = "cpu"; } }
    public bool MetricMemory { get => _metric == "memory"; set { if (value) Metric = "memory"; } }
    public bool MetricDisk { get => _metric == "disk"; set { if (value) Metric = "disk"; } }
    public bool MetricGpu { get => _metric == "gpu"; set { if (value) Metric = "gpu"; } }

    public override void Update(MonitorFrame f)
    {
        var raw = f.Raw;
        var h = f.History;
        SystemLine = Lang.T($"{f.Info.OsName} · {f.Info.CpuName} · {Ru.Bytes(f.Info.InstalledMemory)} · работает {Ru.Duration(raw.Cpu.Uptime)}", $"{f.Info.OsName} · {f.Info.CpuName} · {Ru.Bytes(f.Info.InstalledMemory)} · up {Ru.Duration(raw.Cpu.Uptime)}");

        Cpu.Value = Ru.Percent(raw.Cpu.UtilityPercent);
        Cpu.Percent = raw.Cpu.UtilityPercent;
        Cpu.Main = raw.Cpu.EffectiveMHz is double cpuMhz ? Ru.GHz(cpuMhz) : Ru.Percent(raw.Cpu.UtilityPercent);
        Cpu.Short = f.Sensors?.CpuTemp is double cpuTemp
            ? $"{cpuTemp:0} °C · {Ru.Count(raw.Cpu.ProcessCount, "процесс", "процесса", "процессов", "process", "processes")}"
            : Ru.Count(raw.Cpu.ProcessCount, "процесс", "процесса", "процессов", "process", "processes");
        Cpu.Sub = raw.Cpu.EffectiveMHz is double mhz ? Ru.GHz(mhz) + " · " + Ru.Count(raw.Cpu.ProcessCount, "процесс", "процесса", "процессов", "process", "processes")
            : Ru.Count(raw.Cpu.ProcessCount, "процесс", "процесса", "процессов", "process", "processes");
        Cpu.Spark = HistorySnapshot.Tail(h.Cpu, 60);

        Memory.Value = Ru.Percent(raw.Memory.UsedPercent);
        Memory.Sub = Lang.T($"{Ru.Bytes(f.Memory.InUse)} из {Ru.Bytes(f.Memory.Total)}", $"{Ru.Bytes(f.Memory.InUse)} of {Ru.Bytes(f.Memory.Total)}");
        Memory.Percent = raw.Memory.UsedPercent;
        Memory.Main = UsedOfTotal(f.Memory.InUse, f.Memory.Total);
        Memory.Short = Lang.T("свободно ", "free ") + Ru.Bytes(Math.Max(0, f.Memory.Total - f.Memory.InUse));
        Memory.Spark = HistorySnapshot.Tail(h.MemPercent, 60);

        var busiest = raw.Disks.OrderByDescending(d => d.ActivePercent).FirstOrDefault();
        if (busiest is not null)
        {
            Disk.Value = Ru.Percent(busiest.ActivePercent);
            Disk.Sub = $"{VerdictRules.DiskName(busiest)} · {Ru.Rate(busiest.ReadBps + busiest.WriteBps)}";
            Disk.Percent = busiest.ActivePercent;
            Disk.Main = Ru.Rate(busiest.ReadBps + busiest.WriteBps);
            Disk.Short = VerdictRules.DiskName(busiest);
            Disk.Spark = h.Disks.TryGetValue(busiest.Index, out var dh) ? HistorySnapshot.Tail(dh.Active, 60) : [];
        }
        else
        {
            Disk.Value = "—";
            Disk.Sub = Lang.T("нет данных", "no data");
            Disk.Percent = 0;
            Disk.Main = "—";
            Disk.Short = Lang.T("нет данных", "no data");
        }

        var gpu = raw.Gpus.OrderByDescending(g => g.UtilPercent).FirstOrDefault();
        if (gpu is not null)
        {
            Gpu.Value = Ru.Percent(gpu.UtilPercent);
            Gpu.Sub = gpu.DedicatedTotal > 0 ? Lang.T($"видеопамять {Ru.Bytes(gpu.DedicatedUsed)} из {Ru.Bytes(gpu.DedicatedTotal)}", $"VRAM {Ru.Bytes(gpu.DedicatedUsed)} of {Ru.Bytes(gpu.DedicatedTotal)}") : gpu.Name;
            Gpu.Percent = gpu.UtilPercent;
            Gpu.Main = ShortGpuName(gpu.Name);
            string vram = gpu.DedicatedTotal > 0 ? UsedOfTotal(gpu.DedicatedUsed, gpu.DedicatedTotal) : "";
            Gpu.Short = f.Sensors?.GpuTemp is double gpuTemp ? $"{gpuTemp:0} °C · {vram}" : vram.Length > 0 ? "VRAM " + vram : "";
            Gpu.Spark = HistorySnapshot.Tail(h.GpuUtil, 60);
        }
        else
        {
            Gpu.Value = "—";
            Gpu.Sub = Lang.T("нет данных", "no data");
            Gpu.Percent = 0;
            Gpu.Main = "—";
            Gpu.Short = Lang.T("нет данных", "no data");
        }

        double rx = raw.Network.Sum(n => n.ReceiveBps), tx = raw.Network.Sum(n => n.SendBps);
        Network.Value = Ru.Bits(rx + tx);
        Network.Sub = $"↓ {Ru.Bits(rx)} · ↑ {Ru.Bits(tx)}";
        Network.Spark = HistorySnapshot.Tail(h.NetRx, 60);
        NetText = Lang.T($"Сеть ↓ {Ru.Bits(rx)} · ↑ {Ru.Bits(tx)}", $"Network ↓ {Ru.Bits(rx)} · ↑ {Ru.Bits(tx)}");

        CpuChart = HistorySnapshot.Tail(h.Cpu, 120);
        GpuChart = HistorySnapshot.Tail(h.GpuUtil, 120);

        int problems = f.Verdicts.Count(v => v.Severity >= Severity.Warning);
        bool critical = f.Verdicts.Any(v => v.Severity == Severity.Critical);
        StatusLevel = critical ? "bad" : problems > 0 ? "warn" : "ok";
        StatusText = problems == 0 ? Lang.T("Всё спокойно", "All quiet") : Lang.T($"{Ru.Count(problems, "проблема", "проблемы", "проблем")} сейчас", $"{Ru.Count(problems, "", "", "", "problem", "problems")} now");

        var verdicts = f.Verdicts.Take(6).ToList();
        CollectionSync.Sync(Verdicts, verdicts, v => v.Area + "|" + v.Title, vm => vm.Key, v => new VerdictVm(v.Area + "|" + v.Title),
            (vm, v) =>
            {
                vm.Title = v.Title;
                vm.Detail = v.Detail;
                vm.Severity = v.Severity;
            });

        Table.Update(f.Apps, raw.Memory.TotalPhysical);
    }

    /// <summary>«12,4 / 16 ГБ» — компактно для карточки датчика.</summary>
    private static string UsedOfTotal(double used, double total)
    {

        const double Gb = 1024d * 1024 * 1024;
        if (total < Gb) return $"{Ru.Bytes((long)used)} / {Ru.Bytes((long)total)}";
        return string.Format(Ru.Culture, Lang.T("{0:0.0} / {1:0.#} ГБ", "{0:0.0} / {1:0.#} GB"), used / Gb, total / Gb);
    }

    /// <summary>«NVIDIA GeForce RTX 3050» → «RTX 3050»: на датчике мало места.</summary>
    private static string ShortGpuName(string name)
    {
        string n = name;
        foreach (var prefix in new[] { "NVIDIA ", "GeForce ", "AMD ", "Radeon(TM) ", "Intel(R) ", "Intel " })
            if (n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) n = n[prefix.Length..];
        return n.Trim();
    }
}

public sealed class StatVm : ObservableObject
{
    private string _value = "";

    private readonly string _labelRu, _labelEn;
    private readonly string? _hintRu, _hintEn;

    public StatVm(string labelRu, string labelEn, string? hintRu = null, string? hintEn = null)
    {
        (_labelRu, _labelEn, _hintRu, _hintEn) = (labelRu, labelEn, hintRu, hintEn);
        Lang.Changed += () =>
        {
            OnPropertyChanged(nameof(Label));
            OnPropertyChanged(nameof(Hint));
        };
    }

    public string Label => Lang.English ? _labelEn : _labelRu;
    public string? Hint => Lang.English ? _hintEn : _hintRu;
    public string Value { get => _value; set => Set(ref _value, value); }
}

public sealed class CpuViewModel : PageViewModel
{
    private double[] _chart = [];
    private string _utility = "", _title = "", _verdict = "", _verdictDetail = "";

    public CpuViewModel()
    {
        Speed = new StatVm("Частота сейчас", "Current clock", "Средняя по всем ядрам с учётом турбо-режима.", "Average over all cores, including turbo.");
        BaseSpeed = new StatVm("Базовая частота", "Base clock");
        Processes = new StatVm("Процессы", "Processes");
        Threads = new StatVm("Потоки", "Threads");
        Handles = new StatVm("Дескрипторы", "Handles", "Открытые программами файлы, ключи реестра и другие объекты.", "Files, registry keys and other objects opened by programs.");
        Uptime = new StatVm("Работает без перезагрузки", "Uptime");
        Temperature = new StatVm("Температура", "Temperature", "Самый горячий датчик процессора (Package или Tctl/Tdie).", "The hottest CPU sensor (Package or Tctl/Tdie).");
        Power = new StatVm("Мощность", "Power", "Потребление процессора по его собственному датчику.", "CPU power draw from its own sensor.");
        Stats = new[] { Speed, BaseSpeed, Temperature, Power, Processes, Threads, Handles, Uptime };
        Table = new AppTableViewModel("cpu");
    }

    public override string Key => "cpu";
    public double[] Chart { get => _chart; private set => Set(ref _chart, value); }
    public string Utility { get => _utility; private set => Set(ref _utility, value); }
    public string Title { get => _title; private set => Set(ref _title, value); }
    public string Verdict { get => _verdict; private set => Set(ref _verdict, value); }
    public string VerdictDetail { get => _verdictDetail; private set => Set(ref _verdictDetail, value); }
    public StatVm Speed { get; }
    public StatVm BaseSpeed { get; }
    public StatVm Processes { get; }
    public StatVm Threads { get; }
    public StatVm Handles { get; }
    public StatVm Uptime { get; }
    public StatVm Temperature { get; }
    public StatVm Power { get; }
    public IReadOnlyList<StatVm> Stats { get; }
    public AppTableViewModel Table { get; }

    public override void Update(MonitorFrame f)
    {
        var c = f.Raw.Cpu;
        Title = $"{f.Info.CpuName} · {Ru.Count(f.Info.LogicalCores, "логический процессор", "логических процессора", "логических процессоров", "logical processor", "logical processors")}";
        Chart = f.History.Cpu;
        Utility = Ru.Percent(c.UtilityPercent);
        Speed.Value = c.EffectiveMHz is double e ? Ru.GHz(e) : "—";
        BaseSpeed.Value = c.NominalMHz is double n ? Ru.GHz(n) : "—";
        Processes.Value = c.ProcessCount.ToString("N0", Ru.Culture);
        Threads.Value = c.ThreadCount.ToString("N0", Ru.Culture);
        Handles.Value = c.HandleCount.ToString("N0", Ru.Culture);
        Uptime.Value = Ru.Duration(c.Uptime);
        var sensors = f.Sensors;
        Temperature.Value = sensors?.CpuTemp is double t ? $"{t:0} °C" : sensors?.CpuNeedsDriver == true ? Lang.T("нужен PawnIO", "needs PawnIO") : "—";
        Power.Value = sensors?.CpuPower is double w ? Lang.T($"{w:0} Вт", $"{w:0} W") : "—";
        var v = f.Verdicts.FirstOrDefault(x => x.Area == "cpu");
        Verdict = v?.Title ?? "";
        VerdictDetail = v?.Detail ?? "";
        Table.Update(f.Apps, f.Raw.Memory.TotalPhysical);
    }
}

public sealed class LegendVm : ObservableObject
{
    private string _value = "";

    private readonly string _nameRu, _nameEn, _hintRu, _hintEn;

    public LegendVm(string nameRu, string nameEn, Brush brush, string hintRu, string hintEn)
    {
        (_nameRu, _nameEn, _hintRu, _hintEn) = (nameRu, nameEn, hintRu, hintEn);
        Brush = brush;
        Lang.Changed += () =>
        {
            OnPropertyChanged(nameof(Name));
            OnPropertyChanged(nameof(Hint));
        };
    }

    public string Name => Lang.English ? _nameEn : _nameRu;
    public Brush Brush { get; }
    public string Hint => Lang.English ? _hintEn : _hintRu;
    public string Value { get => _value; set => Set(ref _value, value); }
}

public sealed class MemoryViewModel : PageViewModel
{
    private double[] _chart = [];
    private IReadOnlyList<StackSegment> _segments = [];
    private string _used = "", _usedPercent = "", _verdict = "", _verdictDetail = "";

    public MemoryViewModel()
    {
        Programs = new LegendVm("Программы", "Programs", Res("Mem.Programs"), "Личная память всех запущенных программ.", "Private memory of all running programs.");
        SystemPart = new LegendVm("Система и драйверы", "System and drivers", Res("Mem.System"), "Ядро Windows, драйверы, общие библиотеки и кэш файловой системы в работе.", "Windows kernel, drivers, shared libraries and the active file system cache.");
        Compressed = new LegendVm("Сжатая", "Compressed", Res("Mem.Compressed"), "Редко используемые данные, которые Windows сжала вместо записи на диск.", "Rarely used data that Windows compressed instead of writing to disk.");
        Modified = new LegendVm("Ждёт записи", "Modified", Res("Mem.Modified"), "Изменённые данные, которые Windows скоро запишет на диск.", "Changed data that Windows will write to disk soon.");
        Standby = new LegendVm("Кэш", "Cache", Res("Mem.Standby"), "Недавно прочитанные файлы. Отдаётся программам мгновенно — это не «занятая» память.", "Recently read files. Handed to programs instantly — this is not “used” memory.");
        Free = new LegendVm("Свободно", "Free", Res("Mem.Free"), "Совсем пустая память.", "Completely empty memory.");
        Legend = new[] { Programs, SystemPart, Compressed, Modified, Standby, Free };

        Available = new StatVm("Доступно", "Available", "Кэш + свободно: столько программы могут получить прямо сейчас.", "Cache + free: what programs can get right now.");
        Commit = new StatVm("Выделено / лимит", "Committed / limit", "Сколько памяти программы попросили у Windows, и предел с учётом файла подкачки.", "How much memory programs asked Windows for, and the limit including the page file.");
        Faults = new StatVm("Подкачка с диска", "Paging from disk", "Сколько страниц в секунду Windows читает с диска из-за нехватки памяти. Норма — почти ноль.", "Pages per second Windows reads from disk due to low memory. Normal is near zero.");
        Paged = new StatVm("Выгружаемый пул ядра", "Kernel paged pool");
        NonPaged = new StatVm("Невыгружаемый пул ядра", "Kernel non-paged pool", "Если растёт часами — утечка в драйвере.", "If it grows for hours, a driver is leaking.");
        Reserved = new StatVm("Зарезервировано оборудованием", "Hardware reserved", "Память, которую забрали встроенная графика и устройства.", "Memory taken by integrated graphics and devices.");
        Stats = new[] { Available, Commit, Faults, Paged, NonPaged, Reserved };
        Table = new AppTableViewModel("memory");
    }

    public override string Key => "memory";
    public double[] Chart { get => _chart; private set => Set(ref _chart, value); }
    public IReadOnlyList<StackSegment> Segments { get => _segments; private set => Set(ref _segments, value); }
    public string Used { get => _used; private set => Set(ref _used, value); }
    public string UsedPercent { get => _usedPercent; private set => Set(ref _usedPercent, value); }
    public string Verdict { get => _verdict; private set => Set(ref _verdict, value); }
    public string VerdictDetail { get => _verdictDetail; private set => Set(ref _verdictDetail, value); }
    public LegendVm Programs { get; }
    public LegendVm SystemPart { get; }
    public LegendVm Compressed { get; }
    public LegendVm Modified { get; }
    public LegendVm Standby { get; }
    public LegendVm Free { get; }
    public IReadOnlyList<LegendVm> Legend { get; }
    public StatVm Available { get; }
    public StatVm Commit { get; }
    public StatVm Faults { get; }
    public StatVm Paged { get; }
    public StatVm NonPaged { get; }
    public StatVm Reserved { get; }
    public IReadOnlyList<StatVm> Stats { get; }
    public AppTableViewModel Table { get; }

    public override void Update(MonitorFrame f)
    {
        var m = f.Raw.Memory;
        var b = f.Memory;
        Chart = f.History.MemPercent;
        Used = Lang.T($"{Ru.Bytes(b.InUse)} из {Ru.Bytes(b.Total)}", $"{Ru.Bytes(b.InUse)} of {Ru.Bytes(b.Total)}");
        UsedPercent = Ru.Percent(m.UsedPercent);
        Segments = new[]
        {
            new StackSegment(b.Programs, Programs.Brush),
            new StackSegment(b.System, SystemPart.Brush),
            new StackSegment(b.Compressed, Compressed.Brush),
            new StackSegment(b.Modified, Modified.Brush),
            new StackSegment(b.Standby, Standby.Brush),
            new StackSegment(b.Free, Free.Brush),
        };
        Programs.Value = Ru.Bytes(b.Programs);
        SystemPart.Value = Ru.Bytes(b.System);
        Compressed.Value = Ru.Bytes(b.Compressed);
        Modified.Value = Ru.Bytes(b.Modified);
        Standby.Value = Ru.Bytes(b.Standby);
        Free.Value = Ru.Bytes(b.Free);

        Available.Value = Ru.Bytes(m.Available);
        Commit.Value = $"{Ru.Bytes(m.CommitTotal)} / {Ru.Bytes(m.CommitLimit)}";
        Faults.Value = m.HardFaultsPerSec is double hf ? Lang.T($"{hf:0} стр./с", $"{hf:0} pages/s") : "—";
        Paged.Value = Ru.Bytes(m.KernelPaged);
        NonPaged.Value = Ru.Bytes(m.KernelNonPaged);
        Reserved.Value = Ru.Bytes(b.HardwareReserved);

        var v = f.Verdicts.FirstOrDefault(x => x.Area == "memory");
        var leak = f.Verdicts.FirstOrDefault(x => x.Area == "leak");
        Verdict = leak?.Title ?? v?.Title ?? "";
        VerdictDetail = leak?.Detail ?? v?.Detail ?? "";
        Table.Update(f.Apps, m.TotalPhysical);
    }
}

public sealed class VolumeVm
{
    public string Name { get; init; } = "";
    public double Fraction { get; init; }
    public string Text { get; init; } = "";
    public bool Low { get; init; }
}

public sealed class DiskCardVm : ObservableObject
{
    private string _title = "", _type = "", _active = "", _latency = "", _read = "", _write = "", _queue = "";
    private double[] _activeChart = [], _readChart = [], _writeChart = [];
    private IReadOnlyList<VolumeVm> _volumes = [];

    public DiskCardVm(int index) => Index = index;

    public int Index { get; }
    public string Title { get => _title; set => Set(ref _title, value); }
    public string Type { get => _type; set => Set(ref _type, value); }
    public string Active { get => _active; set => Set(ref _active, value); }
    public string Latency { get => _latency; set => Set(ref _latency, value); }
    public string Read { get => _read; set => Set(ref _read, value); }
    public string Write { get => _write; set => Set(ref _write, value); }
    public string Queue { get => _queue; set => Set(ref _queue, value); }
    public double[] ActiveChart { get => _activeChart; set => Set(ref _activeChart, value); }
    public double[] ReadChart { get => _readChart; set => Set(ref _readChart, value); }
    public double[] WriteChart { get => _writeChart; set => Set(ref _writeChart, value); }
    public IReadOnlyList<VolumeVm> Volumes { get => _volumes; set => Set(ref _volumes, value); }
}

public sealed class DiskViewModel : PageViewModel
{
    private string _verdict = "", _verdictDetail = "";

    public DiskViewModel() => Table = new AppTableViewModel("disk");

    public override string Key => "disk";
    public ObservableCollection<DiskCardVm> Disks { get; } = new();
    public AppTableViewModel Table { get; }
    public string Verdict { get => _verdict; private set => Set(ref _verdict, value); }
    public string VerdictDetail { get => _verdictDetail; private set => Set(ref _verdictDetail, value); }

    public override void Update(MonitorFrame f)
    {
        CollectionSync.Sync(Disks, f.Raw.Disks, d => d.Index.ToString(), vm => vm.Index.ToString(), d => new DiskCardVm(d.Index), (vm, d) =>
        {
            vm.Title = $"{VerdictRules.DiskName(d)} · {d.Model}";
            vm.Type = d.MediaType switch
            {
                DiskMediaType.Nvme => "NVMe SSD",
                DiskMediaType.Ssd => "SSD" + (d.Bus.Length > 0 ? " · " + d.Bus : ""),
                DiskMediaType.Hdd => Lang.T("Жёсткий диск (HDD)", "Hard disk (HDD)") + (d.Bus.Length > 0 ? " · " + d.Bus : ""),
                _ => d.Bus.Length > 0 ? d.Bus : Lang.T("Тип не определён", "Type unknown"),
            };
            vm.Active = Ru.Percent(d.ActivePercent);
            vm.Latency = Ru.Ms(d.LatencyMs);
            vm.Read = Ru.Rate(d.ReadBps);
            vm.Write = Ru.Rate(d.WriteBps);
            vm.Queue = d.QueueLength.ToString("0", Ru.Culture);
            if (f.History.Disks.TryGetValue(d.Index, out var h))
            {
                vm.ActiveChart = h.Active;
                vm.ReadChart = HistorySnapshot.Tail(h.Read, 120);
                vm.WriteChart = HistorySnapshot.Tail(h.Write, 120);
            }
            vm.Volumes = d.Volumes.Select(vol => new VolumeVm
            {
                Name = vol.Label is null ? vol.Letter : $"{vol.Letter} {vol.Label}",
                Fraction = vol.TotalBytes > 0 ? 1 - (double)vol.FreeBytes / vol.TotalBytes : 0,
                Text = vol.TotalBytes > 0 ? Lang.T($"свободно {Ru.Bytes(vol.FreeBytes)} из {Ru.Bytes(vol.TotalBytes)}", $"{Ru.Bytes(vol.FreeBytes)} free of {Ru.Bytes(vol.TotalBytes)}") : Lang.T("нет данных", "no data"),
                Low = vol.TotalBytes > 0 && vol.FreeBytes < vol.TotalBytes * 0.1,
            }).ToList();
        });

        var v = f.Verdicts.FirstOrDefault(x => x.Area == "disk");
        Verdict = v?.Title ?? "";
        VerdictDetail = v?.Detail ?? "";
        Table.Update(f.Apps, f.Raw.Memory.TotalPhysical);
    }
}

public sealed class EngineVm
{
    public string Name { get; init; } = "";
    public string Value { get; init; } = "";
    public double Fraction { get; init; }
}

public sealed class GpuCardVm : ObservableObject
{
    private string _name = "", _util = "", _vram = "";
    private double _vramFraction;
    private double[] _chart = [];
    private IReadOnlyList<EngineVm> _engines = [];

    public GpuCardVm(string luid) => Luid = luid;

    public string Luid { get; }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Util { get => _util; set => Set(ref _util, value); }
    public string Vram { get => _vram; set => Set(ref _vram, value); }
    public double VramFraction { get => _vramFraction; set => Set(ref _vramFraction, value); }
    public double[] Chart { get => _chart; set => Set(ref _chart, value); }
    public IReadOnlyList<EngineVm> Engines { get => _engines; set => Set(ref _engines, value); }
}

public sealed class GpuViewModel : PageViewModel
{
    private bool _empty;
    private string _verdict = "", _verdictDetail = "";

    public GpuViewModel() => Table = new AppTableViewModel("gpu");

    public override string Key => "gpu";
    public ObservableCollection<GpuCardVm> Gpus { get; } = new();
    public AppTableViewModel Table { get; }
    public bool Empty { get => _empty; private set => Set(ref _empty, value); }
    public string Verdict { get => _verdict; private set => Set(ref _verdict, value); }
    public string VerdictDetail { get => _verdictDetail; private set => Set(ref _verdictDetail, value); }

    private string _sensorText = "";
    /// <summary>«68 °C (горячая точка 80 °C) · 112 Вт · 1845 МГц · вентиляторы 54%».</summary>
    public string SensorText { get => _sensorText; private set => Set(ref _sensorText, value); }

    private static string EngineName(string e) => e switch
    {
        "3D" => "3D",
        "Copy" => Lang.T("Копирование", "Copy"),
        "VideoDecode" => Lang.T("Декодирование видео", "Video decode"),
        "VideoEncode" => Lang.T("Кодирование видео", "Video encode"),
        "VideoProcessing" => Lang.T("Обработка видео", "Video processing"),
        "Compute_0" or "Compute_1" or "Compute" => Lang.T("Вычисления", "Compute"),
        _ => e,
    };

    public override void Update(MonitorFrame f)
    {
        Empty = f.Raw.Gpus.Count == 0;
        CollectionSync.Sync(Gpus, f.Raw.Gpus, g => g.Luid, vm => vm.Luid, g => new GpuCardVm(g.Luid), (vm, g) =>
        {
            vm.Name = g.Name;
            vm.Util = Ru.Percent(g.UtilPercent);
            vm.Vram = g.DedicatedTotal > 0 ? Lang.T($"{Ru.Bytes(g.DedicatedUsed)} из {Ru.Bytes(g.DedicatedTotal)}", $"{Ru.Bytes(g.DedicatedUsed)} of {Ru.Bytes(g.DedicatedTotal)}") : Ru.Bytes(g.DedicatedUsed);
            vm.VramFraction = g.DedicatedTotal > 0 ? (double)g.DedicatedUsed / g.DedicatedTotal : 0;
            vm.Chart = f.History.Gpus.TryGetValue(g.Luid, out var h) ? h.Util : [];
            vm.Engines = g.EngineUtil
                .OrderByDescending(kv => kv.Value)
                .Take(5)
                .Select(kv => new EngineVm { Name = EngineName(kv.Key), Value = Ru.Percent(kv.Value), Fraction = kv.Value / 100 })
                .ToList();
        });

        var sn = f.Sensors;
        var parts = new List<string>();
        if (sn?.GpuTemp is double gt) parts.Add(sn.GpuHotspot is double hs ? Lang.T($"{gt:0} °C (горячая точка {hs:0} °C)", $"{gt:0} °C (hotspot {hs:0} °C)") : $"{gt:0} °C");
        if (sn?.GpuPower is double gp) parts.Add(Lang.T($"{gp:0} Вт", $"{gp:0} W"));
        if (sn?.GpuClockMHz is double gc) parts.Add(Lang.T($"{gc:0} МГц", $"{gc:0} MHz"));
        if (sn?.GpuFanPercent is double fan) parts.Add(Lang.T($"вентиляторы {fan:0}%", $"fans {fan:0}%"));
        SensorText = parts.Count == 0 ? "" : Lang.T("Датчики: ", "Sensors: ") + string.Join(" · ", parts);

        var v = f.Verdicts.FirstOrDefault(x => x.Area == "gpu");
        Verdict = v?.Title ?? "";
        VerdictDetail = v?.Detail ?? "";
        Table.Update(f.Apps, f.Raw.Memory.TotalPhysical);
    }
}

public sealed class NetCardVm : ObservableObject
{
    private string _name = "", _description = "", _rx = "", _tx = "", _speed = "";
    private double[] _rxChart = [], _txChart = [];

    public NetCardVm(string id) => Id = id;

    public string Id { get; }
    public string Name { get => _name; set => Set(ref _name, value); }
    public string Description { get => _description; set => Set(ref _description, value); }
    public string Rx { get => _rx; set => Set(ref _rx, value); }
    public string Tx { get => _tx; set => Set(ref _tx, value); }
    public string Speed { get => _speed; set => Set(ref _speed, value); }
    public double[] RxChart { get => _rxChart; set => Set(ref _rxChart, value); }
    public double[] TxChart { get => _txChart; set => Set(ref _txChart, value); }
}

public sealed class NetworkViewModel : PageViewModel
{
    private bool _empty;

    public override string Key => "network";
    public ObservableCollection<NetCardVm> Adapters { get; } = new();
    public bool Empty { get => _empty; private set => Set(ref _empty, value); }

    public override void Update(MonitorFrame f)
    {
        Empty = f.Raw.Network.Count == 0;
        var adapters = f.Raw.Network.OrderByDescending(a => a.ReceiveBps + a.SendBps).ThenBy(a => a.Name).ToList();
        CollectionSync.Sync(Adapters, adapters, a => a.Id, vm => vm.Id, a => new NetCardVm(a.Id), (vm, a) =>
        {
            vm.Name = $"{a.Name} · {a.Kind}";
            vm.Description = a.Description;
            vm.Rx = Ru.Bits(a.ReceiveBps);
            vm.Tx = Ru.Bits(a.SendBps);
            vm.Speed = a.SpeedBitsPerSec > 0 ? Lang.T("скорость подключения ", "link speed ") + Ru.Bits(a.SpeedBitsPerSec / 8.0) : "";
            if (f.History.Adapters.TryGetValue(a.Id, out var h))
            {
                vm.RxChart = HistorySnapshot.Tail(h.Rx, 120);
                vm.TxChart = HistorySnapshot.Tail(h.Tx, 120);
            }
        });
    }
}

public sealed class PlaceholderViewModel : PageViewModel
{
    public PlaceholderViewModel(string key, string title, string stage, string description, IReadOnlyList<string> bullets)
    {
        PageKey = key;
        Title = title;
        Stage = stage;
        Description = description;
        Bullets = bullets;
    }

    private string PageKey { get; }
    public override string Key => PageKey;
    public string Title { get; }
    public string Stage { get; }
    public string Description { get; }
    public IReadOnlyList<string> Bullets { get; }
}
