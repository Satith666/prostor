using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using WinPulse.App.Infrastructure;
using WinPulse.Collectors.Gaming;
using WinPulse.Core;
using WinPulse.Core.Analysis;
using WinPulse.Core.Formatting;
using WinPulse.Core.Gaming;

namespace WinPulse.App.ViewModels;

/// <summary>Сохранённый итог замера FPS.</summary>
public sealed class MeasurementRecord
{
    public string Game { get; set; } = "";
    public string Exe { get; set; } = "";
    public DateTime DateUtc { get; set; }
    public double Seconds { get; set; }
    public double AvgFps { get; set; }
    public double Low1Fps { get; set; }
    public double Low01Fps { get; set; }
    public int Stutters { get; set; }
    public string Bottleneck { get; set; } = "";
    public double? GpuBusyRatio { get; set; }
    public double[] FpsPerSecond { get; set; } = [];
}

/// <summary>Замеры и игровые сессии на диске: %LOCALAPPDATA%\WinPulse\games.</summary>
public static class GameStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    public static string Root { get; } = Path.Combine(AppPaths.Root, "games");
    private static string MeasurementsFile => Path.Combine(Root, "measurements.json");
    private static string SessionsDir => Path.Combine(Root, "sessions");

    public static List<MeasurementRecord> LoadMeasurements()
    {
        try
        {
            if (File.Exists(MeasurementsFile))
                return JsonSerializer.Deserialize<List<MeasurementRecord>>(File.ReadAllText(MeasurementsFile), Json) ?? new();
        }
        catch (Exception ex)
        {
            Log.Error("measurements-load", ex);
        }
        return new();
    }

    public static void SaveMeasurements(IEnumerable<MeasurementRecord> items)
    {
        try
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(MeasurementsFile, JsonSerializer.Serialize(items.OrderByDescending(m => m.DateUtc).Take(200).ToList(), Json));
        }
        catch (Exception ex)
        {
            Log.Error("measurements-save", ex);
        }
    }

    public static void SaveSession(GameSessionRecord record)
    {
        try
        {
            Directory.CreateDirectory(SessionsDir);
            string file = Path.Combine(SessionsDir, $"{record.StartUtc.ToLocalTime():yyyyMMdd-HHmmss}-{record.Id[..8]}.json");
            File.WriteAllText(file, JsonSerializer.Serialize(record, Json));
            // Храним последние 50 сессий.
            foreach (var old in new DirectoryInfo(SessionsDir).GetFiles("*.json").OrderByDescending(f => f.Name).Skip(50))
                old.Delete();
        }
        catch (Exception ex)
        {
            Log.Error("session-save", ex);
        }
    }

    public static List<GameSessionRecord> LoadSessions()
    {
        var list = new List<GameSessionRecord>();
        try
        {
            if (!Directory.Exists(SessionsDir)) return list;
            foreach (var file in new DirectoryInfo(SessionsDir).GetFiles("*.json").OrderByDescending(f => f.Name).Take(50))
            {
                try
                {
                    var r = JsonSerializer.Deserialize<GameSessionRecord>(File.ReadAllText(file.FullName), Json);
                    if (r is not null) list.Add(r);
                }
                catch (Exception ex)
                {
                    Log.Error("session-load " + file.Name, ex);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("sessions-load", ex);
        }
        return list;
    }
}

public sealed class GameTargetVm
{
    public GameTargetVm(string name, string exe, string? path, string detail)
    {
        Name = name;
        Exe = exe;
        Path = path;
        Detail = detail;
    }

    public string Name { get; }
    public string Exe { get; }
    public string? Path { get; }
    public string Detail { get; }
    public override string ToString() => Name;
}

public sealed class MeasurementVm
{
    public MeasurementVm(MeasurementRecord r, MeasurementRecord? previous)
    {
        Record = r;
        Title = r.Game;
        DateText = r.DateUtc.ToLocalTime().ToString("dd.MM HH:mm");
        AvgText = $"{r.AvgFps:0}";
        LowText = $"{r.Low1Fps:0}";
        if (previous is not null && previous.AvgFps > 0)
        {
            double change = FrameStats.ChangePercent(previous.AvgFps, r.AvgFps);
            ChangeText = (change >= 0 ? "+" : "") + change.ToString("0") + "%";
            ChangeLevel = change >= 3 ? "good" : change <= -3 ? "bad" : "same";
        }
    }

    public MeasurementRecord Record { get; }
    public string Title { get; }
    public string DateText { get; }
    public string AvgText { get; }
    public string LowText { get; }
    public string? ChangeText { get; }
    public string ChangeLevel { get; } = "same";
}

public sealed class SessionItemVm : ObservableObject
{
    public SessionItemVm(SessionReport report)
    {
        Report = report;
        var r = report.Record;
        Title = r.Game;
        DateText = r.StartUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm");
        DurationText = Ru.Duration(r.Duration);
        Headline = report.Headline;
        Level = report.Findings.Any(f => f.Level is "bad") ? "bad" : report.Findings.Any(f => f.Level is "warn") ? "warn" : "good";
    }

    public SessionReport Report { get; }
    public string Title { get; }
    public string DateText { get; }
    public string DurationText { get; }
    public string Headline { get; }
    public string Level { get; }
}

public sealed class MomentVm
{
    public MomentVm(SessionMoment m)
    {
        var t = TimeSpan.FromSeconds(m.T);
        TimeText = t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
        Text = m.Text;
    }

    public string TimeText { get; }
    public string Text { get; }
}

public sealed class InterfererVm
{
    public InterfererVm(Interferer i)
    {
        Name = i.Name;
        CpuText = i.AvgCpu >= 0.5 ? $"{i.AvgCpu:0.#}%" : "—";
        GpuText = i.AvgGpu >= 0.5 ? $"{i.AvgGpu:0.#}%" : "—";
        IoText = i.AvgIo >= 100_000 ? Ru.Rate(i.AvgIo) : "—";
        Advice = i.Advice;
        Share = Math.Clamp(i.SharePercent, 0, 100);
    }

    public string Name { get; }
    public string CpuText { get; }
    public string GpuText { get; }
    public string IoText { get; }
    public string? Advice { get; }
    public double Share { get; }
}

/// <summary>
/// «FPS-лаборатория»: замер FPS через PresentMon с выводом «во что упирается игра», история замеров,
/// а также автоматическая запись игровых сессий с разбором, что мешало игре.
/// </summary>
public sealed class GamesViewModel : PageViewModel
{
    private readonly List<MeasurementRecord> _measurements;
    private CancellationTokenSource? _captureCts;
    private string? _presentMon;
    private bool _busy;
    private bool _measuring;
    private string _toolText = "";
    private string _statusText = "";
    private double _liveFps;
    private double _progress;
    private GameTargetVm? _target;
    private int _duration = 60;
    private FrameStats? _result;
    private string _resultTitle = "";
    private string? _compareText;
    private double[] _resultChart = [];
    private string _sessionText = "";
    private bool _sessionActive;
    private SessionItemVm? _selectedSession;
    private DateTime _liveFpsAt;

    public GamesViewModel()
    {
        _measurements = GameStore.LoadMeasurements();
        RebuildHistory();
        foreach (var r in GameStore.LoadSessions()) Sessions.Add(new SessionItemVm(SessionAnalyzer.Analyze(r)));
        SelectedSession = Sessions.FirstOrDefault();

        StartCommand = new AsyncCommand(StartAsync, () => !_busy && !_measuring && _target is not null);
        StopCommand = new RelayCommand(() => _captureCts?.Cancel(), () => _measuring);
        DownloadCommand = new AsyncCommand(DownloadAsync, () => !_busy && !_measuring);
        OpenCapturesCommand = new RelayCommand(() => DriverRowVm.Open(PresentMonTool.CapturesDir));
        SetDurationCommand = new RelayCommand(p =>
        {
            if (p is string s && int.TryParse(s, out int v)) Duration = v;
        });
        RefreshTool();
    }

    public override string Key => "lab";

    public ObservableCollection<GameTargetVm> Targets { get; } = new();
    public ObservableCollection<MeasurementVm> History { get; } = new();
    public ObservableCollection<SessionItemVm> Sessions { get; } = new();
    public ObservableCollection<SessionFinding> Findings { get; } = new();
    public ObservableCollection<InterfererVm> Interferers { get; } = new();
    public ObservableCollection<MomentVm> Moments { get; } = new();

    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand DownloadCommand { get; }
    public ICommand OpenCapturesCommand { get; }
    public ICommand SetDurationCommand { get; }

    public bool HasTool => _presentMon is not null;
    public string ToolText { get => _toolText; private set => Set(ref _toolText, value); }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public bool IsMeasuring
    {
        get => _measuring;
        private set
        {
            if (Set(ref _measuring, value)) CommandManager.InvalidateRequerySuggested();
        }
    }
    public double LiveFps { get => _liveFps; private set { if (Set(ref _liveFps, value)) OnPropertyChanged(nameof(LiveFpsText)); } }
    public string LiveFpsText => _liveFps > 0 ? _liveFps.ToString("0") : "—";
    public double Progress { get => _progress; private set => Set(ref _progress, value); }

    public GameTargetVm? Target
    {
        get => _target;
        set
        {
            if (Set(ref _target, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    public int Duration
    {
        get => _duration;
        set
        {
            if (!Set(ref _duration, value)) return;
            OnPropertyChanged(nameof(Is30));
            OnPropertyChanged(nameof(Is60));
            OnPropertyChanged(nameof(Is120));
            OnPropertyChanged(nameof(Is300));
        }
    }
    public bool Is30 { get => _duration == 30; set { if (value) Duration = 30; } }
    public bool Is60 { get => _duration == 60; set { if (value) Duration = 60; } }
    public bool Is120 { get => _duration == 120; set { if (value) Duration = 120; } }
    public bool Is300 { get => _duration == 300; set { if (value) Duration = 300; } }

    public bool HasResult => _result is not null;
    public string ResultTitle { get => _resultTitle; private set => Set(ref _resultTitle, value); }
    public string AvgText => _result is null ? "—" : _result.AvgFps.ToString("0");
    public string Low1Text => _result is null ? "—" : _result.Low1Fps.ToString("0");
    public string Low01Text => _result is null ? "—" : _result.Low01Fps.ToString("0");
    public string FrameTimeText => _result is null ? "—" : _result.MedianFrameMs.ToString("0.0") + Lang.T(" мс", " ms");
    public string StutterText => _result is null ? "" : _result.Stutters == 0
        ? Lang.T("Подвисаний нет", "No stutters")
        : Lang.T($"Подвисаний: {_result.Stutters}", $"Stutters: {_result.Stutters}");
    public string BottleneckText => _result?.BottleneckText ?? "";
    public string BottleneckAdvice => _result?.BottleneckAdvice ?? "";
    public string? CompareText { get => _compareText; private set => Set(ref _compareText, value); }
    public double[] ResultChart { get => _resultChart; private set => Set(ref _resultChart, value); }

    public string SessionText { get => _sessionText; private set => Set(ref _sessionText, value); }
    public bool SessionActive { get => _sessionActive; private set => Set(ref _sessionActive, value); }
    public bool HasSessions => Sessions.Count > 0;

    public SessionItemVm? SelectedSession
    {
        get => _selectedSession;
        set
        {
            if (!Set(ref _selectedSession, value)) return;
            Findings.Clear();
            Interferers.Clear();
            Moments.Clear();
            if (value is null) return;
            foreach (var f in value.Report.Findings) Findings.Add(f);
            foreach (var i in value.Report.Interferers) Interferers.Add(new InterfererVm(i));
            foreach (var m in value.Report.Moments) Moments.Add(new MomentVm(m));
            OnPropertyChanged(nameof(ReportCpu));
            OnPropertyChanged(nameof(ReportGpu));
            OnPropertyChanged(nameof(ReportFps));
            OnPropertyChanged(nameof(ReportSummary));
            OnPropertyChanged(nameof(HasReportFps));
        }
    }
    public double[] ReportCpu => _selectedSession?.Report.CpuSeries ?? [];
    public double[] ReportGpu => _selectedSession?.Report.GpuSeries ?? [];
    public double[] ReportFps => _selectedSession?.Report.FpsSeries ?? [];
    public bool HasReportFps => _selectedSession?.Report.AvgFps is not null;
    public string ReportSummary
    {
        get
        {
            var r = _selectedSession?.Report;
            if (r is null) return "";
            string text = Lang.T($"Процессор в среднем {r.AvgCpu:0}%, видеокарта {r.AvgGpu:0}%, память до {r.PeakMemPercent:0}%",
                $"CPU averaged {r.AvgCpu:0}%, GPU {r.AvgGpu:0}%, RAM up to {r.PeakMemPercent:0}%");
            if (r.AvgFps is double fps) text += Lang.T($" · {fps:0} FPS в среднем", $" · {fps:0} FPS on average");
            return text;
        }
    }

    /// <summary>FPS последней секунды текущего замера — для записи в игровую сессию. Читается из фонового потока.</summary>
    public double? CurrentFps => _measuring && (DateTime.UtcNow - _liveFpsAt).TotalSeconds < 2 && _liveFps > 0 ? _liveFps : null;

    public override void Update(MonitorFrame frame)
    {
        // Список целей: то, что похоже на игру, плюс заметно работающие с видеокартой приложения.
        var candidates = frame.Apps
            .Where(a => a.Kind == AppKind.Application && a.Path is not null && !GameDetector.IsExcluded(a.Path, a.DisplayName))
            .Where(a => GameDetector.LooksLikeGameFolder(a.Path) || a.GpuPercent >= 3)
            .OrderByDescending(a => GameDetector.LooksLikeGameFolder(a.Path))
            .ThenByDescending(a => a.GpuPercent)
            .Take(12)
            .Select(a => new GameTargetVm(a.DisplayName, System.IO.Path.GetFileName(a.Path!), a.Path,
                Lang.T($"видеокарта {a.GpuPercent:0}%", $"GPU {a.GpuPercent:0}%")))
            .ToList();

        string? keep = _target?.Exe;
        CollectionSync.Sync(Targets, candidates, t => t.Exe, t => t.Exe, t => t, (_, _) => { });
        if (_target is null || Targets.All(t => t.Exe != keep)) Target = Targets.FirstOrDefault();
    }

    /// <summary>Сессия началась или закончилась — вызывается в потоке интерфейса.</summary>
    public void OnSessionState(GameSessionRecord? current)
    {
        SessionActive = current is not null;
        SessionText = current is null
            ? Lang.T("Сейчас игра не идёт. Запись начнётся сама, когда вы запустите игру.", "No game running. Recording starts automatically when you launch a game.")
            : Lang.T($"Идёт игра: {current.Game} · записываю {Ru.Duration(DateTime.UtcNow - current.StartUtc)}",
                $"Playing: {current.Game} · recording for {Ru.Duration(DateTime.UtcNow - current.StartUtc)}");
    }

    public void AddSession(GameSessionRecord record)
    {
        var item = new SessionItemVm(SessionAnalyzer.Analyze(record));
        Sessions.Insert(0, item);
        OnPropertyChanged(nameof(HasSessions));
        SelectedSession = item;
    }

    private void RefreshTool()
    {
        _presentMon = PresentMonTool.Find();
        OnPropertyChanged(nameof(HasTool));
        ToolText = _presentMon is null
            ? Lang.T("Для замера нужен PresentMon — бесплатный открытый инструмент Intel (лицензия MIT, ~1 МБ). WinPulse скачает официальный выпуск с GitHub.",
                "Measuring needs PresentMon — Intel's free open-source tool (MIT license, ~1 MB). WinPulse downloads the official release from GitHub.")
            : Lang.T($"PresentMon: {System.IO.Path.GetFileName(_presentMon)}. Без прав администратора Windows спросит разрешение на каждый замер.",
                $"PresentMon: {System.IO.Path.GetFileName(_presentMon)}. Without admin rights Windows asks permission for each capture.");
        CommandManager.InvalidateRequerySuggested();
    }

    private async Task DownloadAsync()
    {
        _busy = true;
        CommandManager.InvalidateRequerySuggested();
        try
        {
            var progress = new Progress<string>(s => StatusText = s);
            await PresentMonTool.DownloadAsync(progress, CancellationToken.None);
            RefreshTool();
        }
        catch (Exception ex)
        {
            Log.Error("presentmon-download", ex);
            StatusText = Lang.T("Не удалось скачать: ", "Download failed: ") + ex.Message +
                         Lang.T($" Можно скачать вручную с github.com/GameTechDev/PresentMon/releases (файл PresentMon-…-x64.exe) и положить в {PresentMonTool.ToolsDir}.",
                             $" You can download it manually from github.com/GameTechDev/PresentMon/releases (PresentMon-…-x64.exe) into {PresentMonTool.ToolsDir}.");
        }
        finally
        {
            _busy = false;
            CommandManager.InvalidateRequerySuggested();
        }
    }

    private async Task StartAsync()
    {
        if (_target is null) return;
        if (_presentMon is null)
        {
            await DownloadAsync();
            if (_presentMon is null) return;
        }

        var target = _target;
        IsMeasuring = true;
        LiveFps = 0;
        Progress = 0;
        StatusText = Lang.T($"Замер «{target.Name}»: играйте как обычно, окно игры должно быть активным.",
            $"Measuring “{target.Name}”: play as usual with the game window active.");
        _captureCts = new CancellationTokenSource();
        var started = DateTime.UtcNow;
        var dispatcher = Application.Current.Dispatcher;

        var capture = new PresentMonCapture(_presentMon, target.Exe, _duration);
        capture.LiveFps += fps => dispatcher.InvokeAsync(() =>
        {
            LiveFps = fps;
            _liveFpsAt = DateTime.UtcNow;
            Progress = Math.Clamp((DateTime.UtcNow - started).TotalSeconds / _duration, 0, 1);
        });

        try
        {
            var result = await Task.Run(() => capture.RunAsync(_captureCts.Token));
            if (result.Stats is null)
            {
                StatusText = result.Error ?? Lang.T("Слишком мало кадров для выводов — замерьте подольше.", "Too few frames for conclusions — measure longer.");
                return;
            }

            var record = new MeasurementRecord
            {
                Game = target.Name,
                Exe = target.Exe,
                DateUtc = DateTime.UtcNow,
                Seconds = result.Stats.DurationSec,
                AvgFps = result.Stats.AvgFps,
                Low1Fps = result.Stats.Low1Fps,
                Low01Fps = result.Stats.Low01Fps,
                Stutters = result.Stats.Stutters,
                Bottleneck = result.Stats.Bottleneck.ToString(),
                GpuBusyRatio = result.Stats.GpuBusyRatio,
                FpsPerSecond = result.Stats.FpsPerSecond.ToArray(),
            };
            var previous = _measurements.Where(m => m.Exe.Equals(record.Exe, StringComparison.OrdinalIgnoreCase)).OrderByDescending(m => m.DateUtc).FirstOrDefault();
            _measurements.Add(record);
            GameStore.SaveMeasurements(_measurements);
            RebuildHistory();
            ShowResult(result.Stats, record, previous);
            StatusText = result.Cancelled
                ? Lang.T("Замер остановлен — показан итог за записанное время.", "Capture stopped — showing the result for the recorded time.")
                : Lang.T("Замер готов.", "Capture complete.");
        }
        catch (Exception ex)
        {
            Log.Error("capture", ex);
            StatusText = Lang.T("Замер не удался: ", "Capture failed: ") + ex.Message;
        }
        finally
        {
            IsMeasuring = false;
            Progress = 0;
            _captureCts?.Dispose();
            _captureCts = null;
        }
    }

    private void ShowResult(FrameStats stats, MeasurementRecord record, MeasurementRecord? previous)
    {
        _result = stats;
        ResultTitle = Lang.T($"{record.Game} · {stats.DurationSec:0} с · {stats.Frames} кадров",
            $"{record.Game} · {stats.DurationSec:0} s · {stats.Frames} frames");
        ResultChart = stats.FpsPerSecond.ToArray();
        CompareText = previous is null
            ? null
            : Lang.T($"По сравнению с прошлым замером ({previous.DateUtc.ToLocalTime():dd.MM HH:mm}): средний FPS {Signed(FrameStats.ChangePercent(previous.AvgFps, stats.AvgFps))}, 1% low {Signed(FrameStats.ChangePercent(previous.Low1Fps, stats.Low1Fps))}.",
                $"Versus the previous capture ({previous.DateUtc.ToLocalTime():dd.MM HH:mm}): average FPS {Signed(FrameStats.ChangePercent(previous.AvgFps, stats.AvgFps))}, 1% low {Signed(FrameStats.ChangePercent(previous.Low1Fps, stats.Low1Fps))}.");
        foreach (var name in new[] { nameof(HasResult), nameof(AvgText), nameof(Low1Text), nameof(Low01Text), nameof(FrameTimeText),
                     nameof(StutterText), nameof(BottleneckText), nameof(BottleneckAdvice) })
            OnPropertyChanged(name);
    }

    private static string Signed(double percent) => (percent >= 0 ? "+" : "") + percent.ToString("0") + "%";

    private void RebuildHistory()
    {
        History.Clear();
        var ordered = _measurements.OrderByDescending(m => m.DateUtc).Take(20).ToList();
        foreach (var m in ordered)
        {
            var previous = _measurements
                .Where(x => x.Exe.Equals(m.Exe, StringComparison.OrdinalIgnoreCase) && x.DateUtc < m.DateUtc)
                .OrderByDescending(x => x.DateUtc)
                .FirstOrDefault();
            History.Add(new MeasurementVm(m, previous));
        }
    }
}
