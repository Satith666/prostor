using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinPulse.App.Infrastructure;
using WinPulse.Collectors;
using WinPulse.Core;
using WinPulse.Core.Formatting;

namespace WinPulse.App.ViewModels;

public sealed class NavItem : ObservableObject
{
    private readonly MainViewModel _owner;
    private bool _isSelected;
    private string _badge = "";

    public NavItem(MainViewModel owner, string key, string locKey, Geometry icon, PageViewModel page, string? sectionLocKey, bool soon)
    {
        _owner = owner;
        Key = key;
        LocKey = locKey;
        Icon = icon;
        Page = page;
        SectionLocKey = sectionLocKey;
        Soon = soon;
    }

    public string Key { get; }
    public string LocKey { get; }
    public Geometry Icon { get; }
    public PageViewModel Page { get; }
    public string? SectionLocKey { get; }
    public bool Soon { get; }
    public string Title => Loc.Get(LocKey);
    public string? Section => SectionLocKey is null ? null : Loc.Get(SectionLocKey);

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!Set(ref _isSelected, value)) return;
            if (value) _owner.Select(this);
        }
    }

    public string Badge { get => _badge; set => Set(ref _badge, value); }

    public void RefreshTexts()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Section));
    }
}

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly MonitorEngine _engine;
    private readonly AppSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _engineLock = new();
    private int _pending;
    private bool _disposed;
    private MonitorFrame? _last;
    private NavItem? _selected;
    private PageViewModel? _page;
    private bool _proMode;
    private bool _english;

    public MainViewModel(MonitorEngine engine, AppSettings settings)
    {
        _engine = engine;
        _settings = settings;
        _dispatcher = Application.Current.Dispatcher;
        IsElevated = OsInfo.IsElevated();
        IsDemo = engine.Source.IsDemo;

        Overview = new OverviewViewModel(Navigate);
        Windowed = new FixModuleViewModel("windowed", new WinPulse.Optimizer.WindowedGames.WindowedGamesModule(),
            "wg.title", "wg.subtitle", "wg.summary", usesGame: true, settings, LastGpuByPid);
        Optimization = new FixModuleViewModel("optimize", new WinPulse.Optimizer.Optimization.OptimizationModule(),
            "opt.title", "opt.subtitle", "opt.summary", usesGame: false, settings, LastGpuByPid);
        Startup = new StartupViewModel(() => _last);

        Add("overview", "nav.overview", "Icon.Overview", Overview, "nav.monitoring");
        Add("cpu", "nav.cpu", "Icon.Cpu", new CpuViewModel());
        Add("memory", "nav.memory", "Icon.Memory", new MemoryViewModel());
        Add("disk", "nav.disk", "Icon.Disk", new DiskViewModel());
        Add("gpu", "nav.gpu", "Icon.Gpu", new GpuViewModel());
        Add("network", "nav.network", "Icon.Network", new NetworkViewModel());
        Problems = new ProblemsViewModel();
        Problems.Scanned += (critical, warnings) => SetBadge("problems", critical + warnings > 0 ? (critical + warnings).ToString() : "✓");
        Add("problems", "nav.problems", "Icon.Problems", Problems, "nav.diagnostics");
        Add("windowed", "nav.windowed", "Icon.Game", Windowed, "nav.optimization");
        Add("optimize", "nav.optimize", "Icon.Optimize", Optimization);
        Add("startup", "nav.startup", "Icon.Startup", Startup);
        Add("drivers", "nav.drivers", "Icon.Drivers", new DriversViewModel());
        Add("lab", "nav.lab", "Icon.Lab", new PlaceholderViewModel("lab", "Лаборатория FPS", "Этап 4 и 7",
            "Замер кадров в игре до и после изменений (PresentMon): средний FPS, 1% low, время кадра и задержка.",
            new[]
            {
                "Запись замера одной кнопкой или горячей клавишей",
                "Сравнение «до/после» на одном графике",
                "Понятный вывод: стало лучше или нет",
            }), "nav.soon", soon: true);

        _proMode = settings.ProMode;
        UiState.I.ProMode = _proMode;
        _english = settings.Language == "en";
        Lang.English = _english;

        Lang.Changed += () =>
        {
            foreach (var n in Nav) n.RefreshTexts();
            OnPropertyChanged(nameof(RightsText));
        };

        OpenLogCommand = new RelayCommand(OpenLog);
        RestartAsAdminCommand = new RelayCommand(RestartAsAdmin, () => !IsElevated);
        Nav[0].IsSelected = true;
    }

    public ObservableCollection<NavItem> Nav { get; } = new();
    public OverviewViewModel Overview { get; }
    public ProblemsViewModel Problems { get; }
    public FixModuleViewModel Windowed { get; }
    public FixModuleViewModel Optimization { get; }
    public StartupViewModel Startup { get; }
    public bool IsElevated { get; }
    public bool IsDemo { get; }
    public string RightsText => IsElevated ? Loc.Get("shell.admin") : Loc.Get("shell.user");
    public string VersionText => "WinPulse " + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "");
    public ICommand OpenLogCommand { get; }
    public ICommand RestartAsAdminCommand { get; }

    public PageViewModel? CurrentPage { get => _page; private set => Set(ref _page, value); }

    public bool ProMode
    {
        get => _proMode;
        set
        {
            if (!Set(ref _proMode, value)) return;
            UiState.I.ProMode = value;
            _settings.ProMode = value;
            _settings.Save();
        }
    }

    public bool English
    {
        get => _english;
        set
        {
            if (!Set(ref _english, value)) return;
            _settings.Language = value ? "en" : "ru";
            _settings.Save();
            Lang.English = value;
            OnPropertyChanged(nameof(IsRussian));
        }
    }

    public bool IsRussian
    {
        get => !_english;
        set
        {
            if (value) English = false;
        }
    }

    private void Add(string key, string locKey, string iconKey, PageViewModel page, string? section = null, bool soon = false)
    {
        var icon = Application.Current.TryFindResource(iconKey) as Geometry ?? Geometry.Empty;
        Nav.Add(new NavItem(this, key, locKey, icon, page, section, soon));
    }

    internal void Select(NavItem item)
    {
        if (ReferenceEquals(_selected, item)) return;
        var previous = _selected;
        _selected = item;
        if (previous is not null) previous.IsSelected = false;
        if (!item.IsSelected) item.IsSelected = true;
        CurrentPage = item.Page;
        if (_last is not null)
        {
            try
            {
                item.Page.Update(_last);
            }
            catch (Exception ex)
            {
                Log.Error("page-update", ex);
            }
        }
        item.Page.OnShown();
    }

    public void Navigate(string key)
    {
        var item = Nav.FirstOrDefault(n => n.Key == key);
        if (item is not null) item.IsSelected = true;
    }

    private IReadOnlyDictionary<int, double>? LastGpuByPid() =>
        _last?.Raw.GpuByPid.ToDictionary(kv => kv.Key, kv => kv.Value.UtilPercent);

    public void Start() => _ = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        Tick();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token)) Tick();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Tick()
    {
        MonitorFrame frame;
        lock (_engineLock)
        {
            if (_disposed) return;
            try
            {
                frame = _engine.Tick();
            }
            catch (Exception ex)
            {
                Log.Error("tick", ex);
                return;
            }
        }

        // Если предыдущий кадр ещё рисуется — этот пропускаем, чтобы интерфейс не отставал.
        if (Interlocked.CompareExchange(ref _pending, 1, 0) != 0) return;
        _dispatcher.InvokeAsync(() =>
        {
            try
            {
                Apply(frame);
            }
            catch (Exception ex)
            {
                Log.Error("apply", ex);
            }
            finally
            {
                Volatile.Write(ref _pending, 0);
            }
        }, DispatcherPriority.Background);
    }

    private void Apply(MonitorFrame f)
    {
        _last = f;
        var raw = f.Raw;
        SetBadge("cpu", Ru.Percent(raw.Cpu.UtilityPercent));
        SetBadge("memory", Ru.Percent(raw.Memory.UsedPercent));
        SetBadge("disk", raw.Disks.Count == 0 ? "" : Ru.Percent(raw.Disks.Max(d => d.ActivePercent)));
        SetBadge("gpu", raw.Gpus.Count == 0 ? "" : Ru.Percent(raw.Gpus.Max(g => g.UtilPercent)));
        SetBadge("network", Ru.Bits(raw.Network.Sum(n => n.ReceiveBps + n.SendBps)));

        var page = CurrentPage;
        if (page is not null and not FixModuleViewModel and not PlaceholderViewModel and not ProblemsViewModel and not StartupViewModel and not DriversViewModel) page.Update(f);
    }

    private void SetBadge(string key, string value)
    {
        var item = Nav.FirstOrDefault(n => n.Key == key);
        if (item is not null) item.Badge = value;
    }

    private void OpenLog()
    {
        try
        {
            System.IO.Directory.CreateDirectory(AppPaths.Logs);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.Logs}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("open-log", ex);
        }
    }

    private void RestartAsAdmin()
    {
        try
        {
            var args = Environment.GetCommandLineArgs().Skip(1).Select(a => a.Contains(' ') ? $"\"{a}\"" : a);
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!, string.Join(' ', args)) { UseShellExecute = true, Verb = "runas" });
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // пользователь отказал в запросе UAC — остаёмся как есть
        }
        catch (Exception ex)
        {
            Log.Error("restart-admin", ex);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        lock (_engineLock)
        {
            if (_disposed) return;
            _disposed = true;
            _engine.Dispose();
        }
    }
}
