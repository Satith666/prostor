using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media;
using WinPulse.App.Infrastructure;
using WinPulse.Core.Analysis;
using WinPulse.Core.Formatting;
using WinPulse.Core;

namespace WinPulse.App.ViewModels;

public sealed class ProcessRowVm
{
    public int Pid { get; init; }
    public string Name { get; init; } = "";
    public string? Detail { get; init; }
    public string Cpu { get; init; } = "";
    public string Memory { get; init; } = "";
    public string Disk { get; init; } = "";
    public string Gpu { get; init; } = "";
    public string Threads { get; init; } = "";
    public string Handles { get; init; } = "";
}

/// <summary>Строка таблицы «кто нагружает» — одно приложение.</summary>
public sealed class AppRowVm : ObservableObject
{
    private string _name = "";
    private string? _publisher;
    private string? _path;
    private ImageSource? _icon;
    private string? _hint;
    private string _kind = "";
    private string _cpu = "", _memory = "", _disk = "", _gpu = "", _count = "", _secondary = "";
    private double _cpuBar, _memBar, _diskBar, _gpuBar;
    private bool _cpuHot, _memHot, _diskHot, _gpuHot;
    private string? _badge;
    private bool _isExpanded;
    private bool _isCritical;
    private bool _iconLoaded;
    private List<int> _pids = new();

    public AppRowVm(string key)
    {
        Key = key;
        OpenFolderCommand = new RelayCommand(OpenFolder, () => _path is not null);
        EndCommand = new AsyncCommand(EndAsync, () => !_isCritical && _pids.Count > 0);
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public string Key { get; }
    public string Name { get => _name; private set => Set(ref _name, value); }
    public string? Publisher { get => _publisher; private set => Set(ref _publisher, value); }
    public string? Path { get => _path; private set => Set(ref _path, value); }
    public ImageSource? Icon { get => _icon; private set => Set(ref _icon, value); }
    public string? Hint { get => _hint; private set => Set(ref _hint, value); }
    public string Kind { get => _kind; private set => Set(ref _kind, value); }
    public string Cpu { get => _cpu; private set => Set(ref _cpu, value); }
    public string Memory { get => _memory; private set => Set(ref _memory, value); }
    public string Disk { get => _disk; private set => Set(ref _disk, value); }
    public string Gpu { get => _gpu; private set => Set(ref _gpu, value); }
    public string Count { get => _count; private set => Set(ref _count, value); }
    public string Secondary { get => _secondary; private set => Set(ref _secondary, value); }
    public double CpuBar { get => _cpuBar; private set => Set(ref _cpuBar, value); }
    public double MemBar { get => _memBar; private set => Set(ref _memBar, value); }
    public double DiskBar { get => _diskBar; private set => Set(ref _diskBar, value); }
    public double GpuBar { get => _gpuBar; private set => Set(ref _gpuBar, value); }
    public bool CpuHot { get => _cpuHot; private set => Set(ref _cpuHot, value); }
    public bool MemHot { get => _memHot; private set => Set(ref _memHot, value); }
    public bool DiskHot { get => _diskHot; private set => Set(ref _diskHot, value); }
    public bool GpuHot { get => _gpuHot; private set => Set(ref _gpuHot, value); }
    public string? Badge { get => _badge; private set => Set(ref _badge, value); }
    public bool IsCritical { get => _isCritical; private set => Set(ref _isCritical, value); }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (Set(ref _isExpanded, value) && !value) Processes.Clear();
        }
    }

    public ObservableCollection<ProcessRowVm> Processes { get; } = new();
    public ICommand OpenFolderCommand { get; }
    public ICommand EndCommand { get; }
    public ICommand ToggleCommand { get; }

    public void Update(AppGroup g, long totalMemory, double maxIo)
    {
        Name = g.DisplayName;
        Publisher = g.Publisher;
        Path = g.Path;
        Hint = g.Hint;
        IsCritical = g.IsCritical;
        Kind = g.Kind switch
        {
            AppKind.Service => Lang.T("Служба", "Service"),
            AppKind.System => Lang.T("Система", "System"),
            _ => Lang.T("Приложение", "App"),
        };
        if (!_iconLoaded)
        {
            _iconLoaded = true;
            Icon = IconCache.Get(g.Path);
        }

        Cpu = Ru.Percent1(g.CpuPercent);
        Memory = Ru.Bytes(g.PrivateMemory);
        Disk = g.IoBps < 1024 ? Lang.T("0 МБ/с", "0 MB/s") : Ru.Rate(g.IoBps);
        Gpu = g.GpuPercent < 0.5 ? "—" : Ru.Percent(g.GpuPercent);
        Count = g.Processes.Count == 1 ? "" : Ru.Count(g.Processes.Count, "процесс", "процесса", "процессов", "process", "processes");
        Secondary = Lang.T($"в среднем: 1 мин — {Ru.Percent1(g.CpuAvg1m)}, 5 мин — {Ru.Percent1(g.CpuAvg5m)}", $"average: 1 min — {Ru.Percent1(g.CpuAvg1m)}, 5 min — {Ru.Percent1(g.CpuAvg5m)}");

        CpuBar = Math.Clamp(g.CpuPercent / 100, 0, 1);
        MemBar = totalMemory > 0 ? Math.Clamp((double)g.PrivateMemory / totalMemory, 0, 1) : 0;
        DiskBar = maxIo > 0 ? Math.Clamp(g.IoBps / maxIo, 0, 1) : 0;
        GpuBar = Math.Clamp(g.GpuPercent / 100, 0, 1);
        CpuHot = g.CpuPercent >= 25;
        MemHot = totalMemory > 0 && g.PrivateMemory >= totalMemory * 0.15;
        DiskHot = g.IoBps >= 20 * 1024 * 1024;
        GpuHot = g.GpuPercent >= 25;
        Badge = g.LooksLikeLeak ? Lang.T($"растёт +{Ru.Bytes(g.MemorySlopePerHour)}/ч", $"growing +{Ru.Bytes(g.MemorySlopePerHour)}/h") : null;
        _pids = g.Processes.Select(p => p.Pid).ToList();

        if (IsExpanded)
        {
            Processes.Clear();
            foreach (var p in g.Processes.OrderByDescending(p => p.CpuPercent).ThenByDescending(p => p.PrivateMemory))
            {
                Processes.Add(new ProcessRowVm
                {
                    Pid = p.Pid,
                    Name = p.Name,
                    Detail = p.Detail,
                    Cpu = Ru.Percent1(p.CpuPercent),
                    Memory = Ru.Bytes(p.PrivateMemory),
                    Disk = p.IoBps < 1024 ? Lang.T("0 МБ/с", "0 MB/s") : Ru.Rate(p.IoBps),
                    Gpu = p.GpuPercent < 0.5 ? "—" : Ru.Percent(p.GpuPercent),
                    Threads = p.Threads.ToString(),
                    Handles = p.Handles.ToString(),
                });
            }
        }
    }

    private void OpenFolder()
    {
        if (_path is null) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("open-folder", ex);
        }
    }

    private async Task EndAsync()
    {
        string message = _pids.Count == 1
            ? Lang.T($"Процесс «{Name}» будет завершён без сохранения данных.", $"Process “{Name}” will be ended without saving data.")
            : Lang.T($"Все процессы «{Name}» ({_pids.Count}) будут завершены без сохранения данных.", $"All “{Name}” processes ({_pids.Count}) will be ended without saving data.");
        if (!await Dialogs.MessageDialog.ConfirmAsync(Lang.T($"Завершить «{Name}»?", $"End “{Name}”?"), message, Lang.T("Завершить", "End task"), danger: true)) return;
        foreach (int pid in _pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill();
            }
            catch (Exception ex)
            {
                Log.Error("kill", ex);
            }
        }
    }
}

/// <summary>Таблица «кто нагружает» с сортировкой по любому ресурсу.</summary>
public sealed class AppTableViewModel : ObservableObject
{
    private const int DefaultLimit = 20;
    private string _sortKey;
    private bool _showAll;
    private int _total;
    private IReadOnlyList<AppGroup> _last = [];
    private long _lastTotalMemory;

    public AppTableViewModel(string sortKey)
    {
        _sortKey = sortKey;
        SortCommand = new RelayCommand(p => SortKey = p as string ?? "cpu");
        ToggleShowAllCommand = new RelayCommand(() => ShowAll = !ShowAll);
    }

    public ObservableCollection<AppRowVm> Rows { get; } = new();
    public ICommand SortCommand { get; }
    public ICommand ToggleShowAllCommand { get; }

    public string SortKey
    {
        get => _sortKey;
        set
        {
            if (!Set(ref _sortKey, value)) return;
            OnPropertyChanged(nameof(SortCpu));
            OnPropertyChanged(nameof(SortMemory));
            OnPropertyChanged(nameof(SortDisk));
            OnPropertyChanged(nameof(SortGpu));
            OnPropertyChanged(nameof(SortName));
            Refresh();
        }
    }

    public bool SortCpu => _sortKey == "cpu";
    public bool SortMemory => _sortKey == "memory";
    public bool SortDisk => _sortKey == "disk";
    public bool SortGpu => _sortKey == "gpu";
    public bool SortName => _sortKey == "name";

    public bool ShowAll
    {
        get => _showAll;
        set
        {
            if (!Set(ref _showAll, value)) return;
            OnPropertyChanged(nameof(ShowAllText));
            Refresh();
        }
    }

    public int Total { get => _total; private set { if (Set(ref _total, value)) OnPropertyChanged(nameof(ShowAllText)); } }
    public bool HasMore => _total > DefaultLimit;
    public string ShowAllText => _showAll ? Lang.T($"Показать только первые {DefaultLimit}", $"Show only the first {DefaultLimit}") : Lang.T($"Показать все ({_total})", $"Show all ({_total})");

    public void Update(IReadOnlyList<AppGroup> apps, long totalMemory)
    {
        _last = apps;
        _lastTotalMemory = totalMemory;
        Refresh();
    }

    private void Refresh()
    {
        var apps = _last.Where(a => !a.Key.StartsWith("sys:idle", StringComparison.Ordinal)).ToList();
        IEnumerable<AppGroup> sorted = _sortKey switch
        {
            "memory" => apps.OrderByDescending(a => a.PrivateMemory),
            "disk" => apps.OrderByDescending(a => a.IoBps).ThenByDescending(a => a.IoAvg1m),
            "gpu" => apps.OrderByDescending(a => a.GpuPercent).ThenByDescending(a => a.GpuMemory),
            "name" => apps.OrderBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase),
            _ => apps.OrderByDescending(a => a.CpuPercent).ThenByDescending(a => a.CpuAvg1m),
        };
        var list = sorted.ToList();
        Total = list.Count;
        OnPropertyChanged(nameof(HasMore));
        if (!_showAll && list.Count > DefaultLimit) list = list.Take(DefaultLimit).ToList();
        double maxIo = list.Count == 0 ? 0 : list.Max(a => a.IoBps);
        long totalMemory = _lastTotalMemory;

        CollectionSync.Sync(Rows, list, a => a.Key, r => r.Key, a => new AppRowVm(a.Key), (row, a) => row.Update(a, totalMemory, maxIo));
    }
}
