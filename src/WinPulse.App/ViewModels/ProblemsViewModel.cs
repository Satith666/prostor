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
using WinPulse.Core;

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
            Dialogs.MessageDialog.Show(action.Label, Lang.T("Не удалось открыть: ", "Couldn't open: ") + ex.Message);
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
            Severity.Critical => Lang.T("Серьёзно", "Serious"),
            Severity.Warning => Lang.T("Стоит разобраться", "Worth a look"),
            Severity.Info => Lang.T("К сведению", "For your information"),
            _ => Lang.T("В порядке", "OK"),
        };

        var counts = new List<string>();
        if (f.Count30 > 0 && f.Severity != Severity.Ok)
        {
            counts.Add(f.Count7 > 0
                ? Lang.T($"{Ru.Count(f.Count7, "раз", "раза", "раз")} за неделю", $"{Ru.Count(f.Count7, "", "", "", "time", "times")} this week")
                : Lang.T("на этой неделе не было", "none this week"));
            counts.Add(Lang.T($"всего {f.Count30}", $"{f.Count30} in total"));
        }
        if (f.LastSeen is DateTime last) counts.Add(Lang.T("последний раз ", "last time ") + When(last));
        CountText = string.Join(" · ", counts);

        Advice = f.Advice.Select((a, i) => $"{i + 1}. {a}").ToList();
        var evidence = f.Evidence.Take(15).Select(e => e.When is DateTime w ? $"{w:dd.MM HH:mm}  {e.Text}" : e.Text).ToList();
        if (f.Evidence.Count > 15) evidence.Add(Lang.T($"…и ещё {f.Evidence.Count - 15}", $"…and {f.Evidence.Count - 15} more"));
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
        if (t.Date == today) return Lang.T($"сегодня в {t:HH:mm}", $"today at {t:HH:mm}");
        if (t.Date == today.AddDays(-1)) return Lang.T($"вчера в {t:HH:mm}", $"yesterday at {t:HH:mm}");
        return t.ToString("d MMMM", Ru.Culture);
    }
}

/// <summary>Раздел «Проблемы»: журналы Windows, устройства, диски — простыми словами.</summary>
public sealed class ProblemsViewModel : PageViewModel
{
    private bool _isBusy;
    private string _busyText = "";
    private string _summary = Lang.T("Проверка ещё не запускалась.", "The check hasn't run yet.");
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
        OpenReliabilityCommand = new FindingActionVm(new FindingAction(Lang.T("Монитор стабильности", "Reliability Monitor"), "perfmon.exe", "/rel")).Command;
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
        ? Lang.T("Скрыть пройденные проверки", "Hide passed checks")
        : Lang.T($"Показать пройденные проверки ({Passed.Count})", $"Show passed checks ({Passed.Count})");

    public override void OnShown()
    {
        if (!_scanned && !IsBusy) _ = ScanAsync();
    }

    public async Task ScanAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        BusyText = Lang.T("Собираю данные…", "Collecting data…");
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
            Summary = Lang.T("Проверка не удалась: ", "The check failed: ") + ex.Message;
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
        LastScan = Lang.T($"Проверено {r.ScannedAt:dd.MM.yyyy HH:mm} · журналы за {Ru.Count(r.PeriodDays, "день", "дня", "дней")}",
            $"Checked {r.ScannedAt:dd.MM.yyyy HH:mm} · logs for {Ru.Count(r.PeriodDays, "", "", "", "day", "days")}");

        int critical = r.CriticalCount, warnings = r.WarningCount, info = r.InfoCount;
        Summary = critical + warnings + info == 0
            ? Lang.T("Проблем не найдено", "No problems found")
            : string.Join(" · ", new[]
            {
                critical > 0 ? Lang.T($"серьёзных: {critical}", $"serious: {critical}") : null,
                warnings > 0 ? Lang.T($"стоит разобраться: {warnings}", $"worth a look: {warnings}") : null,
                info > 0 ? Lang.T($"к сведению: {info}", $"FYI: {info}") : null,
            }.Where(s => s is not null));
        Summary = char.ToUpper(Summary[0], Ru.Culture) + Summary[1..];

        int crit = r.Days.Sum(d => d.Critical), err = r.Days.Sum(d => d.Errors), warn = r.Days.Sum(d => d.Warnings);
        TimelineText = Lang.T($"Критичных событий: {crit} · ошибок: {err} · предупреждений: {warn}", $"Critical events: {crit} · errors: {err} · warnings: {warn}");

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
        string path = Path.Combine(dir, Lang.T($"WinPulse-отчёт-{DateTime.Now:yyyyMMdd-HHmm}.md", $"WinPulse-report-{DateTime.Now:yyyyMMdd-HHmm}.md"));
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
        sb.AppendLine(Lang.T("# Отчёт WinPulse", "# WinPulse report"));
        sb.AppendLine();
        sb.AppendLine(Lang.T($"Создан: {DateTime.Now:dd.MM.yyyy HH:mm} · журналы за {r.PeriodDays} дн.", $"Created: {DateTime.Now:dd.MM.yyyy HH:mm} · logs for {r.PeriodDays} days"));
        sb.AppendLine();
        sb.AppendLine(Lang.T("## Система", "## System"));
        sb.AppendLine(Lang.T($"- {os.Display}, сборка {os.BuildText} ({os.Edition})", $"- {os.Display}, build {os.BuildText} ({os.Edition})"));
        sb.AppendLine(Lang.T($"- Процессор: {sys.CpuName}, логических процессоров: {sys.LogicalCores}", $"- CPU: {sys.CpuName}, logical processors: {sys.LogicalCores}"));
        sb.AppendLine(Lang.T($"- Память: {Ru.Bytes(sys.InstalledMemory)}", $"- Memory: {Ru.Bytes(sys.InstalledMemory)}"));
        foreach (var g in Dxgi.Enumerate().Where(g => !g.IsSoftware))
            sb.AppendLine(Lang.T($"- Видеокарта: {g.Name}, видеопамять {Ru.Bytes(g.DedicatedMemory)}", $"- GPU: {g.Name}, video memory {Ru.Bytes(g.DedicatedMemory)}"));
        foreach (var m in Displays.Enumerate())
            {
            string primary = m.IsPrimary ? Lang.T(" (основной)", " (primary)") : "";
            sb.AppendLine(Lang.T($"- Монитор: {m.FriendlyName}{primary}, {m.Width}×{m.Height}, {m.RefreshHz} Гц (макс. {m.MaxRefreshHz}), масштаб {m.ScalePercent}%, VRR: {m.Vrr}",
                $"- Monitor: {m.FriendlyName}{primary}, {m.Width}×{m.Height}, {m.RefreshHz} Hz (max {m.MaxRefreshHz}), scale {m.ScalePercent}%, VRR: {m.Vrr}"));
        }
        sb.AppendLine(OsInfo.IsElevated() ? Lang.T("- WinPulse запущен от имени администратора", "- WinPulse runs as administrator") : Lang.T("- WinPulse запущен с обычными правами", "- WinPulse runs with standard rights"));
        sb.AppendLine();

        sb.AppendLine(Lang.T("## Найденные проблемы", "## Problems found"));
        if (r.Problems.Count == 0) sb.AppendLine(Lang.T("Не найдено.", "None."));
        foreach (var f in r.Problems)
        {
            sb.AppendLine();
            sb.AppendLine($"### [{f.Severity}] {f.Title}");
            sb.AppendLine(f.Summary);
            if (f.Advice.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(Lang.T("Что делать:", "What to do:"));
                foreach (var a in f.Advice) sb.AppendLine("- " + a);
            }
            if (f.Evidence.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(Lang.T("Записи:", "Entries:"));
                foreach (var e in f.Evidence.Take(20))
                    sb.AppendLine("- " + (e.When is DateTime w ? $"{w:dd.MM HH:mm} " : "") + e.Text);
            }
            if (f.Technical is not null) sb.AppendLine(Lang.T("\nТехнически: ", "\nTechnical: ") + f.Technical.Replace("\n", "; "));
        }
        sb.AppendLine();
        sb.AppendLine(Lang.T("## Пройденные проверки", "## Passed checks"));
        foreach (var f in r.Passed) sb.AppendLine($"- {f.Title}");
        if (r.Notes.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(Lang.T("## Заметки", "## Notes"));
            foreach (var n in r.Notes) sb.AppendLine("- " + n);
        }
        return sb.ToString();
    }
}
