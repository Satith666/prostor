using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Input;
using WinPulse.App.Infrastructure;
using WinPulse.Collectors.Drivers;
using WinPulse.Core.Analysis;
using WinPulse.Core.Drivers;
using WinPulse.Core;

namespace WinPulse.App.ViewModels;

public sealed class DriverRowVm
{
    public DriverRowVm(DriverRow r)
    {
        Name = r.Info.DeviceName;
        Vendor = r.Vendor;
        Provider = r.Info.Provider ?? "";
        Version = r.FriendlyVersion is null ? r.Info.Version ?? "—" : $"{r.FriendlyVersion} ({r.Info.Version})";
        DateText = r.Info.Date is DateTime d ? d.ToString("dd.MM.yyyy") : "—";
        AgeText = r.AgeText;
        Advice = r.Advice;
        Level = r.Severity switch
        {
            Severity.Critical => "bad",
            Severity.Warning => "warn",
            Severity.Info => "info",
            _ => "ok",
        };
        LinkLabel = r.LinkLabel;
        Technical = r.Info.InstanceId + (r.Info.Inf is null ? "" : " · " + r.Info.Inf);
        string? link = r.Link;
        OpenLinkCommand = new RelayCommand(() => Open(link), () => link is not null);
    }

    public string Name { get; }
    public string Vendor { get; }
    public string Provider { get; }
    public string Version { get; }
    public string DateText { get; }
    public string AgeText { get; }
    public string Advice { get; }
    public string Level { get; }
    public string? LinkLabel { get; }
    public string Technical { get; }
    public ICommand OpenLinkCommand { get; }

    internal static void Open(string? target)
    {
        if (target is null) return;
        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("open " + target, ex);
        }
    }
}

public sealed class DriverGroupVm
{
    public DriverGroupVm(string title, IReadOnlyList<DriverRowVm> rows)
    {
        Title = title;
        Rows = rows;
    }

    public string Title { get; }
    public IReadOnlyList<DriverRowVm> Rows { get; }
}

/// <summary>Раздел «Драйверы»: версии и даты драйверов, советы и ссылки только на официальные сайты.</summary>
public sealed class DriversViewModel : PageViewModel
{
    private bool _isBusy;
    private bool _scanned;
    private string _summary = Lang.T("Список ещё не загружен.", "The list is not loaded yet.");
    private string _gpuTitle = "";
    private string _gpuText = "";
    private IReadOnlyList<string> _cleanSteps = [];
    private bool _showSteps;

    public DriversViewModel()
    {
        RefreshCommand = new AsyncCommand(RefreshAsync, () => !IsBusy);
        OptionalUpdatesCommand = new RelayCommand(() => DriverRowVm.Open(DriverAnalysis.OptionalUpdates));
        DeviceManagerCommand = new RelayCommand(() => DriverRowVm.Open("devmgmt.msc"));
        ToggleStepsCommand = new RelayCommand(() => ShowSteps = !ShowSteps);
    }

    public override string Key => "drivers";
    public ObservableCollection<DriverGroupVm> Groups { get; } = new();
    public ICommand RefreshCommand { get; }
    public ICommand OptionalUpdatesCommand { get; }
    public ICommand DeviceManagerCommand { get; }
    public ICommand ToggleStepsCommand { get; }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (Set(ref _isBusy, value)) CommandManager.InvalidateRequerySuggested();
        }
    }

    public string Summary { get => _summary; private set => Set(ref _summary, value); }
    public string GpuTitle { get => _gpuTitle; private set => Set(ref _gpuTitle, value); }
    public string GpuText { get => _gpuText; private set => Set(ref _gpuText, value); }
    public IReadOnlyList<string> CleanSteps { get => _cleanSteps; private set => Set(ref _cleanSteps, value); }
    public bool ShowSteps { get => _showSteps; set => Set(ref _showSteps, value); }

    public override void OnShown()
    {
        if (!_scanned && !IsBusy) _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        try
        {
            var rows = await Task.Run(() => DriverAnalysis.Analyze(DriverInventory.Read(), DateTime.Now));
            _scanned = true;
            Groups.Clear();
            foreach (var g in rows.GroupBy(r => r.Category))
                Groups.Add(new DriverGroupVm(DriverAnalysis.CategoryText(g.Key), g.Select(r => new DriverRowVm(r)).ToList()));

            int attention = rows.Count(r => r.Severity >= Severity.Warning);
            Summary = rows.Count == 0
                ? Lang.T("Не удалось получить список драйверов.", "Couldn't get the driver list.")
                : Lang.T($"Драйверов проверено: {rows.Count}", $"Drivers checked: {rows.Count}") + (attention > 0 ? Lang.T($" · требуют внимания: {attention}", $" · need attention: {attention}") : Lang.T(" · всё в порядке", " · all fine"));

            var gpu = rows.Where(r => r.Category == DriverCategory.Graphics && r.Vendor is "NVIDIA" or "AMD" or "Intel")
                .OrderByDescending(r => r.Vendor == "NVIDIA" || r.Vendor == "AMD").FirstOrDefault();
            if (gpu is not null)
            {
                GpuTitle = Lang.T($"{gpu.Info.DeviceName}: драйвер {(gpu.FriendlyVersion ?? gpu.Info.Version)} от {gpu.Info.Date:dd.MM.yyyy}", $"{gpu.Info.DeviceName}: driver {(gpu.FriendlyVersion ?? gpu.Info.Version)} dated {gpu.Info.Date:dd.MM.yyyy}");
                GpuText = gpu.Advice + Lang.T(" Драйверы берите только с сайта производителя или через Центр обновления Windows — сторонние «обновлялки» драйверов часто ставят не то.", " Get drivers only from the maker's site or Windows Update — third-party “driver updaters” often install the wrong thing.");
                CleanSteps = DriverAnalysis.CleanInstallSteps(gpu.Vendor).Select((s, i) => $"{i + 1}. {s}").ToList();
            }
            else
            {
                GpuTitle = "";
                GpuText = "";
                CleanSteps = [];
            }
        }
        catch (Exception ex)
        {
            Log.Error("drivers", ex);
            Summary = Lang.T("Не удалось получить список драйверов: ", "Couldn't get the driver list: ") + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
