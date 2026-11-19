using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Input;
using WinPulse.Collectors;
using WinPulse.Collectors.Display;
using WinPulse.Collectors.Gpu;
using WinPulse.App.Infrastructure;
using WinPulse.Collectors.Diagnostics;
using WinPulse.Core.Analysis;
using WinPulse.Core.Diagnostics;
using WinPulse.Core.Formatting;

namespace WinPulse.App.ViewModels;

public sealed class FindingActionVm
{
    public FindingActionVm(FindingAction action)
    {
        Label = action.Label;
        Command = new RelayCommand(() => Open(action));
    }

    public string Label { get; }
    public ICommand Command { get; }

    private static void Open(FindingAction action)
    {
        try
        {
            var psi = new ProcessStartInfo(action.Command) { UseShellExecute = true };
            if (!string.IsNullOrEmpty(action.Arguments)) psi.Arguments = action.Arguments;
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Log.Error("finding-action " + action.Command, ex);
            Dialogs.MessageDialog.Show(action.Label, "Не удалось открыть: " + ex.Message);
        }
    }
}

/// <summary>Карточка находки.</summary>
public sealed class FindingVm : ObservableObject
{
    private bool _isExpanded;

    public FindingVm(Finding f)
    {
        Id = f.Id;
        Title = f.Title;
        Summary = f.Summary;
        Category = f.Category;
        Level = f.Severity switch
        {
            Severity.Critical => "bad",
            Severity.Warning => "warn",
            Severity.Info => "info",
            _ => "ok",
        };
        LevelText = f.Severity switch
        {
            Severity.Critical => "Серьёзно",
            Severity.Warning => "Стоит разобраться",
            Severity.Info => "К сведению",
            _ => "В порядке",
        };

        var counts = new List<string>();
        if (f.Count30 > 0 && f.Severity != Severity.Ok)
        {
            counts.Add(f.Count7 > 0 ? $"{Ru.Count(f.Count7, "раз", "раза", "раз")} за неделю" : "на этой неделе не было");
            counts.Add($"всего {f.Count30}");
        }
        if (f.LastSeen is DateTime last) counts.Add("последний раз " + When(last));
        CountText = string.Join(" · ", counts);

        Advice = f.Advice.Select((a, i) => $"{i + 1}. {a}").ToList();
        var evidence = f.Evidence.Take(15).Select(e => e.When is DateTime w ? $"{w:dd.MM HH:mm}  {e.Text}" : e.Text).ToList();
        if (f.Evidence.Count > 15) evidence.Add($"…и ещё {f.Evidence.Count - 15}");
        Evidence = evidence;
        Technical = f.Technical;
        Actions = f.Actions.Select(a => new FindingActionVm(a)).ToList();
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
        _isExpanded = f.Severity >= Severity.Warning;
    }

    public string Id { get; }
    public string Title { get; }
    public string Summary { get; }
    public string Category { get; }
    /// <summary>ok | info | warn | bad</summary>
    public string Level { get; }
    public string LevelText { get; }
    public string CountText { get; }
    public IReadOnlyList<string> Advice { get; }
    public bool HasAdvice => Advice.Count > 0;
    public IReadOnlyList<string> Evidence { get; }
    public bool HasEvidence => Evidence.Count > 0;
    public string? Technical { get; }
    public IReadOnlyList<FindingActionVm> Actions { get; }
    public ICommand ToggleCommand { get; }

    public bool IsExpanded { get => _isExpanded; set => Set(ref _isExpanded, value); }

    private static string When(DateTime t)
    {
        var today = DateTime.Today;
        if (t.Date == today) return $"сегодня в {t:HH:mm}";
        if (t.Date == today.AddDays(-1)) return $"вчера в {t:HH:mm}";
        return t.ToString("d MMMM", Ru.Culture);
    }
}

/// <summary>Раздел «Проблемы»: журналы Windows, устройства, диски — простыми словами.</summary>
public sealed class ProblemsViewModel : PageViewModel
{
    private bool _isBusy;
    private string _busyText = "";
    private string _summary = "Проверка ещё не запускалась.";
    private string _lastScan = "";
    private bool _showPassed;
    private IReadOnlyList<DayBucket> _days = [];
    private IReadOnlyList<string> _notes = [];
    private bool _scanned;
    private string _timelineText = "";
    private DiagnosticReport? _report;

    public ProblemsViewModel()
    {
        ScanCommand = new AsyncCommand(ScanAsync, () => !IsBusy);
        SaveReportCommand = new AsyncCommand(SaveReportAsync, () => !IsBusy && _report is not null);
        TogglePassedCommand = new RelayCommand(() => ShowPassed = !ShowPassed);
        OpenReliabilityCommand = new FindingActionVm(new FindingAction("Монитор стабильности", "perfmon.exe", "/rel")).Command;
    }

    public override string Key => "problems";
    public ObservableCollection<FindingVm> Problems { get; } = new();
    public ObservableCollection<FindingVm> Passed { get; } = new();
    public ICommand ScanCommand { get; }
    public ICommand SaveReportCommand { get; }
    public ICommand TogglePassedCommand { get; }
    public ICommand OpenReliabilityCommand { get; }

    /// <summary>Вызывается после проверки: (критичных, предупреждений).</summary>
    public event Action<int, int>? Scanned;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    public string BusyText { get => _busyText; private set => Set(ref _busyText, value); }
    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string LastScan { get => _lastScan; private set => Set(ref _lastScan, value); }
    public IReadOnlyList<DayBucket> Days { get => _days; private set => Set(ref _days, value); }
    public string TimelineText { get => _timelineText; private set => Set(ref _timelineText, value); }
    public IReadOnlyList<string> Notes { get => _notes; private set { if (Set(ref _notes, value)) OnPropertyChanged(nameof(HasNotes)); } }
    public bool HasNotes => _notes.Count > 0;
    public bool HasProblems => Problems.Count > 0;
    public bool NoProblems => _scanned && Problems.Count == 0;

    public bool ShowPassed
    {
        get => _showPassed;
        set
        {
            if (Set(ref _showPassed, value)) OnPropertyChanged(nameof(PassedToggleText));
        }
    }

    public string PassedToggleText => _showPassed
        ? "Скрыть пройденные проверки"
        : $"Показать пройденные проверки ({Passed.Count})";

    public override void OnShown()
    {
        if (!_scanned && !IsBusy) _ = ScanAsync();
    }

    public async Task ScanAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        BusyText = "Собираю данные…";
        try
        {
            var report = await Task.Run(() =>
            {
                var input = DiagnosticsCollector.Collect(30, text => System.Windows.Application.Current.Dispatcher.InvokeAsync(() => BusyText = text));
                return ProblemDetectors.Run(input);
            });
            Apply(report);
        }
        catch (Exception ex)
        {
            Log.Error("problems-scan", ex);
            Summary = "Проверка не удалась: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply(DiagnosticReport r)
    {
        _scanned = true;
        _report = r;
        Problems.Clear();
        foreach (var f in r.Problems) Problems.Add(new FindingVm(f));
        Passed.Clear();
        foreach (var f in r.Passed) Passed.Add(new FindingVm(f));
        Days = r.Days;
        Notes = r.Notes;
        LastScan = $"Проверено {r.ScannedAt:dd.MM.yyyy HH:mm} · журналы за {Ru.Count(r.PeriodDays, "день", "дня", "дней")}";

        int critical = r.CriticalCount, warnings = r.WarningCount, info = r.InfoCount;
        Summary = critical + warnings + info == 0
            ? "Проблем не найдено"
            : string.Join(" · ", new[]
            {
                critical > 0 ? $"серьёзных: {critical}" : null,
                warnings > 0 ? $"стоит разобраться: {warnings}" : null,
                info > 0 ? $"к сведению: {info}" : null,
            }.Where(s => s is not null));
        Summary = char.ToUpper(Summary[0], Ru.Culture) + Summary[1..];

        int crit = r.Days.Sum(d => d.Critical), err = r.Days.Sum(d => d.Errors), warn = r.Days.Sum(d => d.Warnings);
        TimelineText = $"Критичных событий: {crit} · ошибок: {err} · предупреждений: {warn}";

        OnPropertyChanged(nameof(HasProblems));
        OnPropertyChanged(nameof(NoProblems));
        OnPropertyChanged(nameof(PassedToggleText));
        Scanned?.Invoke(critical, warnings);
    }

    /// <summary>Сохраняет отчёт (система + находки) в файл на рабочем столе — удобно отправить для разбора.</summary>
    private async Task SaveReportAsync()
    {
        var report = _report;
        if (report is null) return;
        string text = await Task.Run(() => BuildReport(report));
        string dir = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        string path = Path.Combine(dir, $"WinPulse-отчёт-{DateTime.Now:yyyyMMdd-HHmm}.md");
        await File.WriteAllTextAsync(path, text, Encoding.UTF8);
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("report-open", ex);
        }
    }

    private static string BuildReport(DiagnosticReport r)
    {
        var sb = new StringBuilder();
        var os = OsInfo.Read();
        var sys = OsInfo.ReadSystemInfo();
        sb.AppendLine("# Отчёт WinPulse");
        sb.AppendLine();
        sb.AppendLine($"Создан: {DateTime.Now:dd.MM.yyyy HH:mm} · журналы за {r.PeriodDays} дн.");
        sb.AppendLine();
        sb.AppendLine("## Система");
        sb.AppendLine($"- {os.Display}, сборка {os.BuildText} ({os.Edition})");
        sb.AppendLine($"- Процессор: {sys.CpuName}, логических процессоров: {sys.LogicalCores}");
        sb.AppendLine($"- Память: {Ru.Bytes(sys.InstalledMemory)}");
        foreach (var g in Dxgi.Enumerate().Where(g => !g.IsSoftware))
            sb.AppendLine($"- Видеокарта: {g.Name}, видеопамять {Ru.Bytes(g.DedicatedMemory)}");
        foreach (var m in Displays.Enumerate())
            sb.AppendLine($"- Монитор: {m.FriendlyName}{(m.IsPrimary ? " (основной)" : "")}, {m.Width}×{m.Height}, {m.RefreshHz} Гц (макс. {m.MaxRefreshHz}), масштаб {m.ScalePercent}%, VRR: {m.Vrr}");
        sb.AppendLine($"- WinPulse запущен {(OsInfo.IsElevated() ? "от имени администратора" : "с обычными правами")}");
        sb.AppendLine();

        sb.AppendLine("## Найденные проблемы");
        if (r.Problems.Count == 0) sb.AppendLine("Не найдено.");
        foreach (var f in r.Problems)
        {
            sb.AppendLine();
            sb.AppendLine($"### [{f.Severity}] {f.Title}");
            sb.AppendLine(f.Summary);
            if (f.Advice.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Что делать:");
                foreach (var a in f.Advice) sb.AppendLine("- " + a);
            }
            if (f.Evidence.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("Записи:");
                foreach (var e in f.Evidence.Take(20))
                    sb.AppendLine("- " + (e.When is DateTime w ? $"{w:dd.MM HH:mm} " : "") + e.Text);
            }
            if (f.Technical is not null) sb.AppendLine("\nТехнически: " + f.Technical.Replace("\n", "; "));
        }
        sb.AppendLine();
        sb.AppendLine("## Пройденные проверки");
        foreach (var f in r.Passed) sb.AppendLine($"- {f.Title}");
        if (r.Notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Заметки");
            foreach (var n in r.Notes) sb.AppendLine("- " + n);
        }
        return sb.ToString();
    }
}
