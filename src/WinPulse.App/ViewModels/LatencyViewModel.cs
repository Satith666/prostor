using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media;
using WinPulse.App.Infrastructure;
using WinPulse.Collectors;
using WinPulse.Collectors.Diagnostics;
using WinPulse.Core;
using WinPulse.Core.Diagnostics;

namespace WinPulse.App.ViewModels;

public sealed class LatencyDriverVm
{
    public LatencyDriverVm(DriverLatency d, double scaleUs, double seconds, Func<string, Brush> res)
    {
        Name = d.Name;
        About = LatencyAdvisor.About(d.Name);
        MaxText = LatencyAdvisor.Us(d.MaxUs);
        Detail = Lang.T(
            $"DPC: {d.DpcCount:N0}, макс. {LatencyAdvisor.Us(d.MaxDpcUs)} · ISR: {d.IsrCount:N0}, макс. {LatencyAdvisor.Us(d.MaxIsrUs)}" + (d.Over1000 > 0 ? $" · дольше 1 мс: {d.Over1000:N0} раз" : ""),
            $"DPC: {d.DpcCount:N0}, max {LatencyAdvisor.Us(d.MaxDpcUs)} · ISR: {d.IsrCount:N0}, max {LatencyAdvisor.Us(d.MaxIsrUs)}" + (d.Over1000 > 0 ? $" · over 1 ms: {d.Over1000:N0} times" : ""));
        BarWidth = 4 + 236 * Math.Min(1, d.MaxUs / scaleUs);
        Brush = res(LatencyAdvisor.Level(d.MaxUs) switch { LatencyLevel.Bad => "Bad", LatencyLevel.Warn => "Warn", _ => "Good" });
    }

    public string Name { get; }
    public string? About { get; }
    public string MaxText { get; }
    public string Detail { get; }
    public double BarWidth { get; }
    public Brush Brush { get; }
}

public sealed record LatencyBucketVm(string Label, string CountText, double Height, Brush Brush);

public sealed class InterruptVm
{
    public InterruptVm(DeviceInterrupt d, Func<string, Brush> res)
    {
        Category = d.Category;
        Name = d.Name;
        Mode = d.Msi
            ? "MSI" + (d.MessageLimit is int n ? Lang.T($" · до {n} сообщ.", $" · up to {n} msgs") : "")
            : Lang.T("линия IRQ", "line-based IRQ");
        Brush = d.Msi ? res("Good") : res("Muted");
        Technical = d.InstanceId;
    }

    public string Category { get; }
    public string Name { get; }
    public string Mode { get; }
    public Brush Brush { get; }
    public string Technical { get; }
}

/// <summary>
/// «Задержки драйверов»: замер DPC/ISR через ETW (как LatencyMon) и режим прерываний устройств (MSI — только показ).
/// Это не про FPS, а про щелчки звука, рывки мыши и микрофризы при нормальном FPS.
/// </summary>
public sealed class LatencyViewModel : PageViewModel
{
    private readonly bool _demo;
    private int _duration = 30;
    private bool _busy;
    private double _progress;
    private string? _status;
    private LatencyReport? _report;
    private bool _devicesLoaded;
    private string? _devicesNote;

    public LatencyViewModel(bool demo = false)
    {
        _demo = demo;
        MeasureCommand = new AsyncCommand(MeasureAsync, () => !_busy);
        OpenLatencyMonCommand = new RelayCommand(() => DriverRowVm.Open("https://www.resplendence.com/latencymon"));
    }

    public override string Key => "latency";
    public ICommand MeasureCommand { get; }
    public ICommand OpenLatencyMonCommand { get; }
    public ObservableCollection<LatencyDriverVm> Drivers { get; } = new();
    public ObservableCollection<LatencyBucketVm> Histogram { get; } = new();
    public ObservableCollection<InterruptVm> Devices { get; } = new();

    public bool Is10 { get => _duration == 10; set { if (value) SetDuration(10); } }
    public bool Is30 { get => _duration == 30; set { if (value) SetDuration(30); } }
    public bool Is60 { get => _duration == 60; set { if (value) SetDuration(60); } }

    private void SetDuration(int seconds)
    {
        _duration = seconds;
        OnPropertyChanged(nameof(Is10));
        OnPropertyChanged(nameof(Is30));
        OnPropertyChanged(nameof(Is60));
    }

    public bool IsBusy
    {
        get => _busy;
        private set
        {
            if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    public double Progress { get => _progress; private set => Set(ref _progress, value); }
    public string? Status { get => _status; private set => Set(ref _status, value); }
    public string? DevicesNote { get => _devicesNote; private set => Set(ref _devicesNote, value); }

    public string AdminText => OsInfo.IsElevated() || _demo
        ? Lang.T("Замер читает события ядра Windows (ETW) и ничего не меняет.", "The measurement reads Windows kernel events (ETW) and changes nothing.")
        : Lang.T("Windows даёт читать события ядра только администратору — для замера появится запрос прав (только на сам замер).",
            "Windows only lets administrators read kernel events — you'll get a UAC prompt for the measurement itself.");

    public bool HasReport => _report is not null && _report.DpcCount + _report.IsrCount > 0;
    public string VerdictText => _report is null ? "" : LatencyAdvisor.Verdict(_report);
    public Brush VerdictBrush => Res(_report is null ? "Muted" : LatencyAdvisor.Level(_report.MaxUs) switch
    {
        LatencyLevel.Bad => "Bad",
        LatencyLevel.Warn => "Warn",
        _ => "Good",
    });
    public string MaxDpcText => _report is null ? "—" : LatencyAdvisor.Us(_report.MaxDpcUs);
    public string MaxIsrText => _report is null ? "—" : LatencyAdvisor.Us(_report.MaxIsrUs);
    public string RateText => _report is null || _report.Seconds <= 0 ? "—"
        : ((_report.DpcCount + _report.IsrCount) / _report.Seconds).ToString("N0");
    public string ShareText => _report is null || _report.Seconds <= 0 ? "—"
        : (_report.TotalUs / (_report.Seconds * 1_000_000) * 100).ToString("0.0") + "%";
    public string ReportNote
    {
        get
        {
            if (_report is null) return "";
            var parts = new List<string>
            {
                Lang.T($"Замер {_report.Seconds:0} с, {_report.Taken:HH:mm}.", $"{_report.Seconds:0} s measurement at {_report.Taken:HH:mm}."),
            };
            if (_report.EventsLost > 0)
                parts.Add(Lang.T($"Потеряно событий: {_report.EventsLost:N0} — максимум мог быть и выше.", $"Events lost: {_report.EventsLost:N0} — the real maximum may be higher."));
            if (_report.Error is not null) parts.Add(_report.Error);
            return string.Join(" ", parts);
        }
    }

    public override void OnShown()
    {
        if (_devicesLoaded) return;
        _devicesLoaded = true;
        if (_demo)
        {
            Apply(WinPulse.Core.Demo.DemoLatency.Create());
            ShowDevices(WinPulse.Core.Demo.DemoLatency.Devices());
            return;
        }
        _ = LoadDevicesAsync();
    }

    private async Task LoadDevicesAsync()
    {
        var list = await Task.Run(() => InterruptReader.Read());
        if (list is null)
        {
            DevicesNote = Lang.T("Не удалось прочитать список устройств.", "Couldn't read the device list.");
            return;
        }
        ShowDevices(list);
    }

    private void ShowDevices(List<DeviceInterrupt> list)
    {
        Devices.Clear();
        foreach (var d in list) Devices.Add(new InterruptVm(d, Res));
        DevicesNote = list.Count == 0 ? Lang.T("Подходящих устройств PCI не найдено.", "No relevant PCI devices found.") : null;
    }

    private async Task MeasureAsync()
    {
        IsBusy = true;
        Progress = 0;
        Status = Lang.T($"Идёт замер, {_duration} с. Можно поиграть или посмотреть видео — так всплески видны лучше.",
            $"Measuring for {_duration} s. Play or watch a video meanwhile — spikes show up better under load.");
        try
        {
            LatencyReport report;
            if (_demo)
            {
                for (int i = 1; i <= 10; i++)
                {
                    await Task.Delay(150);
                    Progress = i / 10.0;
                }
                report = WinPulse.Core.Demo.DemoLatency.Create();
            }
            else
            {
                report = await LatencyRunner.MeasureAsync(_duration, new Progress<double>(p => Progress = p), CancellationToken.None);
            }
            if (report.DpcCount + report.IsrCount == 0)
            {
                Status = report.Error ?? LatencyAdvisor.Verdict(report);
                return;
            }
            Apply(report);
            Status = null;
        }
        catch (Exception ex)
        {
            Log.Error("latency", ex);
            Status = Lang.T("Замер не удался: ", "Measurement failed: ") + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply(LatencyReport report)
    {
        _report = report;
        Drivers.Clear();
        double scale = Math.Max(LatencyAdvisor.BadUs, report.Drivers.Count > 0 ? report.Drivers.Max(d => d.MaxUs) : 0);
        foreach (var d in report.Drivers.Take(15)) Drivers.Add(new LatencyDriverVm(d, scale, report.Seconds, Res));

        Histogram.Clear();
        long max = report.Histogram.Length > 0 ? Math.Max(1, report.Histogram.Max()) : 1;
        for (int i = 0; i < report.Histogram.Length; i++)
        {
            double upper = i < LatencyAggregator.Buckets.Length ? LatencyAggregator.Buckets[i] : double.PositiveInfinity;
            double lower = i == 0 ? 0 : LatencyAggregator.Buckets[i - 1];
            string label = double.IsPositiveInfinity(upper) ? "> " + LatencyAdvisor.Us(lower) : "≤ " + LatencyAdvisor.Us(upper);
            long count = report.Histogram[i];
            // Логарифмическая высота: редкие длинные всплески тоже должно быть видно.
            double h = count == 0 ? 2 : 6 + 84 * Math.Log10(count + 1) / Math.Log10(max + 1);
            string brush = lower >= LatencyAdvisor.BadUs ? "Bad" : lower >= LatencyAdvisor.WarnUs ? "Warn" : "Accent";
            Histogram.Add(new LatencyBucketVm(label, count.ToString("N0"), h, Res(brush)));
        }

        foreach (var name in new[] { nameof(HasReport), nameof(VerdictText), nameof(VerdictBrush), nameof(MaxDpcText), nameof(MaxIsrText),
                     nameof(RateText), nameof(ShareText), nameof(ReportNote) })
            OnPropertyChanged(name);
    }
}
