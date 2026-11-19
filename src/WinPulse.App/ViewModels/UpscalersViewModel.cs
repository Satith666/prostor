using System.Collections.ObjectModel;
using System.Windows.Input;
using WinPulse.App.Infrastructure;
using WinPulse.Collectors.Gaming;
using WinPulse.Collectors.Gpu;
using WinPulse.Core;
using WinPulse.Core.Gaming;

namespace WinPulse.App.ViewModels;

public sealed class UpscaleGameVm
{
    public UpscaleGameVm(GameUpscalers g, GpuAbilities gpu)
    {
        Name = g.Game;
        Path = g.Path;
        // Streamline — только прослойка NVIDIA, игроку её версия ни о чём не говорит: показываем в подробностях.
        Badges = g.Dlls
            .Where(d => d.Tech != UpscalerTech.Streamline)
            .GroupBy(d => d.Tech)
            .Select(grp => UpscalerCatalog.Label(grp.OrderByDescending(d => UpscalerCatalog.ParseVersion(d.Version)).First()))
            .Distinct()
            .ToList();
        AntiCheat = g.AntiCheat;
        Advice = UpscalerCatalog.Advice(g, gpu);
        HasDlls = g.Dlls.Count > 0;
        Technical = string.Join("\n", g.Dlls.Select(d => $"{d.RelativePath} — {d.Version ?? "?"}"));
        string path = g.Path;
        OpenFolderCommand = new RelayCommand(() => DriverRowVm.Open(path));
    }

    public string Name { get; }
    public string Path { get; }
    public IReadOnlyList<string> Badges { get; }
    public string? AntiCheat { get; }
    public IReadOnlyList<string> Advice { get; }
    public bool HasDlls { get; }
    public string Technical { get; }
    public ICommand OpenFolderCommand { get; }
}

/// <summary>
/// «Апскейлинг»: какие игры поддерживают DLSS, FSR и XeSS, какой версии, где можно обновить или получить генерацию кадров.
/// Ничего не подменяет — подмену библиотек делают DLSS Swapper и OptiScaler, и только в одиночных играх.
/// </summary>
public sealed class UpscalersViewModel : PageViewModel
{
    private bool _busy;
    private bool _scanned;
    private string _status = "";
    private string _gpuText = "";

    private readonly bool _demo;

    public UpscalersViewModel(bool demo = false)
    {
        _demo = demo;
        ScanCommand = new AsyncCommand(ScanAsync, () => !_busy);
        OpenDlssSwapperCommand = new RelayCommand(() => DriverRowVm.Open("https://github.com/beeradmoore/dlss-swapper/releases"));
        OpenOptiScalerCommand = new RelayCommand(() => DriverRowVm.Open("https://github.com/optiscaler/OptiScaler/releases"));
    }

    public override string Key => "upscale";
    public ObservableCollection<UpscaleGameVm> Games { get; } = new();
    public ICommand ScanCommand { get; }
    public ICommand OpenDlssSwapperCommand { get; }
    public ICommand OpenOptiScalerCommand { get; }
    public bool IsBusy { get => _busy; private set { if (Set(ref _busy, value)) CommandManager.InvalidateRequerySuggested(); } }
    public string Status { get => _status; private set => Set(ref _status, value); }
    public string GpuText { get => _gpuText; private set => Set(ref _gpuText, value); }

    public override void OnShown()
    {
        if (!_scanned && !_busy) _ = ScanAsync();
    }

    private async Task ScanAsync()
    {
        IsBusy = true;
        _scanned = true;
        Status = Lang.T("Ищу игры и библиотеки апскейлеров…", "Looking for games and upscaler libraries…");
        try
        {
            // Progress доставляет сообщения асинхронно — после конца сканирования поздние не должны затирать итог.
            bool done = false;
            var progress = new Progress<string>(name => { if (!done) Status = Lang.T("Проверяю: ", "Checking: ") + name; });
            var (games, gpuName) = _demo
                ? (WinPulse.Core.Demo.DemoUpscalers.Create(), WinPulse.Core.Demo.DemoUpscalers.GpuName)
                : await Task.Run(() =>
            {
                string name = Dxgi.Enumerate().Where(g => !g.IsSoftware).OrderByDescending(g => g.DedicatedMemory).FirstOrDefault()?.Name ?? "";
                return (UpscalerScanner.Scan(progress), name);
            });
            done = true;
            var gpu = UpscalerCatalog.Abilities(gpuName);
            GpuText = gpuName.Length == 0
                ? Lang.T("Видеокарта не определена.", "GPU not detected.")
                : Lang.T($"Видеокарта: {gpuName}. ", $"GPU: {gpuName}. ") +
                  (gpu.IsRtx
                      ? gpu.HasDlssFrameGen
                          ? Lang.T("Доступны DLSS и генерация кадров DLSS.", "DLSS and DLSS Frame Generation are available.")
                          : Lang.T("Доступен DLSS; генерации кадров DLSS нет (она для RTX 40/50) — её заменяет FSR 3 через OptiScaler.", "DLSS is available; DLSS Frame Generation isn't (RTX 40/50 only) — FSR 3 via OptiScaler replaces it.")
                      : Lang.T("DLSS недоступен (нужна RTX). FSR и XeSS работают на любой видеокарте.", "DLSS isn't available (needs RTX). FSR and XeSS work on any GPU."));

            Games.Clear();
            foreach (var g in games) Games.Add(new UpscaleGameVm(g, gpu));
            int with = games.Count(g => g.Dlls.Count > 0);
            Status = games.Count == 0
                ? Lang.T("Игр Steam и Epic Games не найдено.", "No Steam or Epic Games titles found.")
                : Lang.T($"Игр: {games.Count}, с апскейлерами: {with}.", $"Games: {games.Count}, with upscalers: {with}.");
        }
        catch (Exception ex)
        {
            Log.Error("upscalers", ex);
            Status = Lang.T("Не удалось проверить игры: ", "Couldn't check games: ") + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
