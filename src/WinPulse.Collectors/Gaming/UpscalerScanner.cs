using System.Diagnostics;
using WinPulse.Collectors.Hardware;
using WinPulse.Core;
using WinPulse.Core.Gaming;

namespace WinPulse.Collectors.Gaming;

/// <summary>Ищет в папках установленных игр библиотеки DLSS, FSR и XeSS и признаки античита.</summary>
public static class UpscalerScanner
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        MaxRecursionDepth = 7,
        AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
    };

    public static List<GameUpscalers> Scan(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var result = new List<GameUpscalers>();
        foreach (var game in HardwareReader.FindGames().GroupBy(g => g.Path, StringComparer.OrdinalIgnoreCase).Select(g => g.First()))
        {
            if (ct.IsCancellationRequested) break;
            progress?.Report(game.Name);
            try
            {
                result.Add(ScanFolder(game.Name, game.Path, ct));
            }
            catch (Exception ex)
            {
                Diag.Report("upscaler-scan " + game.Name, ex);
            }
        }
        return result.OrderByDescending(g => g.Dlls.Count > 0).ThenBy(g => g.Game, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static GameUpscalers ScanFolder(string name, string root, CancellationToken ct = default)
    {
        var dlls = new List<UpscalerDll>();
        string? antiCheat = UpscalerCatalog.AntiCheatOf(root.Contains("riot games", StringComparison.OrdinalIgnoreCase) ? "vgc.exe" : "");
        if (Directory.Exists(root))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(root, "*", Options))
            {
                if (ct.IsCancellationRequested) break;
                string relative = Path.GetRelativePath(root, entry);
                antiCheat ??= UpscalerCatalog.AntiCheatOf(relative);
                if (!entry.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                if (UpscalerCatalog.Classify(Path.GetFileName(entry)) is not UpscalerTech tech) continue;
                string? version = null;
                try
                {
                    var info = FileVersionInfo.GetVersionInfo(entry);
                    version = info.FileVersion?.Trim();
                    if (string.IsNullOrEmpty(version)) version = null;
                }
                catch
                {
                    // Без версии тоже полезно знать, что библиотека есть.
                }
                dlls.Add(new UpscalerDll(tech, relative, version));
            }
        }
        return new GameUpscalers { Game = name, Path = root, Dlls = dlls, AntiCheat = antiCheat };
    }
}
