using System.Diagnostics;
using WinPulse.Collectors;
using WinPulse.Collectors.Display;
using WinPulse.Collectors.Gpu;
using WinPulse.Collectors.Hardware;
using WinPulse.Core;
using WinPulse.Core.Display;
using WinPulse.Optimizer.Changes;
using WinPulse.Optimizer.Platform;

namespace WinPulse.Optimizer.WindowedGames;

public sealed record RunningProgram(int Pid, string Name, string? Path, double GpuPercent);

/// <summary>Всё, что нужно знать о системе, чтобы проверить пункты. Собирается в фоне (≈0,2–0,5 с).</summary>
public sealed class FixContext
{
    public required OsDetails Os { get; init; }
    public required IReadOnlyList<GpuAdapterInfo> Gpus { get; init; }
    public GpuAdapterInfo? MainGpu { get; init; }
    public GpuVendor Vendor => MainGpu?.Vendor ?? GpuVendor.Other;
    public HagsState? HagsInfo { get; init; }
    /// <summary>Работает ли «Целостность памяти» (HVCI) прямо сейчас.</summary>
    public bool? HvciRunning { get; init; }
    public required IReadOnlyList<MonitorInfo> Monitors { get; init; }
    public string? GameExe { get; init; }
    public required IReadOnlyList<RunningProgram> Running { get; init; }
    public string? ActiveScheme { get; init; }
    public IReadOnlyList<PowerScheme> Schemes { get; init; } = [];
    public bool HasBattery { get; init; }
    public bool IsElevated { get; init; }
    public required BackupStore Backup { get; init; }
    /// <summary>Память, шина видеокарты, диски игр. null — модулю не нужно.</summary>
    public HardwareSnapshot? Hardware { get; init; }
    public Func<string, bool> IsMarked { get; init; } = _ => false;

    public VrrSupport BestVrr => Monitors.Count == 0 ? VrrSupport.No : Monitors.Max(m => m.Vrr);

    /// <summary>Монитор, на котором обычно идёт игра: основной, иначе самый быстрый.</summary>
    public MonitorInfo? GameMonitor =>
        Monitors.OrderByDescending(m => m.IsPrimary).ThenByDescending(m => m.MaxRefreshHz).FirstOrDefault();

    public string GpuText => MainGpu is null
        ? Lang.T("видеокарта не определена", "GPU not detected")
        : MainGpu.Name;

    /// <summary>Программы, которые часто нагружают видеокарту в фоне (п. 9).</summary>
    public static readonly HashSet<string> WatchedPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "wallpaper32.exe", "wallpaper64.exe", "Lively.exe", "livelywpf.exe", "Lively.UI.WinUI.exe",
        "chrome.exe", "msedge.exe", "firefox.exe", "opera.exe", "brave.exe", "browser.exe", "vivaldi.exe",
        "Discord.exe", "NVIDIA Overlay.exe", "NVIDIA Share.exe", "GameBar.exe", "RTSS.exe",
    };

    public static FixContext Build(string? gameExe, IReadOnlyDictionary<int, double>? gpuByPid, BackupStore backup, Func<string, bool> isMarked,
        bool hardware = false)
    {
        HardwareSnapshot? hw = null;
        if (hardware)
        {
            try
            {
                hw = HardwareReader.Read();
            }
            catch (Exception ex)
            {
                Diag.Report("hardware-read", ex);
            }
        }

        var os = OsInfo.Read();
        var gpus = Dxgi.Enumerate().Where(g => !g.IsSoftware).ToList();
        var main = gpus.OrderByDescending(g => g.DedicatedMemory).FirstOrDefault();
        HagsState? hags = main is null ? null : Hags.Query(main);
        var monitors = Displays.Enumerate();
        var running = ListRunning(gpuByPid);
        string? active = null;
        List<PowerScheme> schemes = new();
        try
        {
            schemes = PowerSchemes.List();
            active = schemes.FirstOrDefault(s => s.Active)?.Guid ?? PowerSchemes.ActiveGuid();
        }
        catch (Exception ex)
        {
            Diag.Report("power-read", ex);
        }

        return new FixContext
        {
            Os = os,
            Gpus = gpus,
            MainGpu = main,
            HagsInfo = hags,
            HvciRunning = SecurityInfo.HvciRunning(),
            Monitors = monitors,
            GameExe = string.IsNullOrWhiteSpace(gameExe) ? null : gameExe,
            Running = running,
            ActiveScheme = active,
            Schemes = schemes,
            HasBattery = OsInfo.HasBattery(),
            IsElevated = OsInfo.IsElevated(),
            Backup = backup,
            IsMarked = isMarked,
            Hardware = hw,
        };
    }

    private static List<RunningProgram> ListRunning(IReadOnlyDictionary<int, double>? gpuByPid)
    {
        var list = new List<RunningProgram>();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return list;
        }

        foreach (var p in processes)
        {
            try
            {
                string name = p.ProcessName + ".exe";
                if (!WatchedPrograms.Contains(name)) continue;
                double gpu = gpuByPid is not null && gpuByPid.TryGetValue(p.Id, out var g) ? g : 0;
                list.Add(new RunningProgram(p.Id, name, ProcessPaths.TryGet(p.Id), gpu));
            }
            catch
            {
                // процесс завершился во время перебора
            }
            finally
            {
                p.Dispose();
            }
        }
        return list;
    }
}
