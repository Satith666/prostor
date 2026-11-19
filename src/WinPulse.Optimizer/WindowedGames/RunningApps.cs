using System.Diagnostics;
using WinPulse.Collectors;

namespace WinPulse.Optimizer.WindowedGames;

public sealed record WindowedApp(int Pid, string Name, string Path, string WindowTitle);

/// <summary>Запущенные программы с окнами — для выбора игры из списка.</summary>
public static class RunningApps
{
    public static List<WindowedApp> ListWithWindows()
    {
        var result = new List<WindowedApp>();
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
        string self = Environment.ProcessPath ?? "";
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.MainWindowHandle == IntPtr.Zero) continue;
                string? path = ProcessPaths.TryGet(p.Id);
                if (path is null) continue;
                if (path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) continue;
                if (path.Equals(self, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(new WindowedApp(p.Id, p.ProcessName + ".exe", path, p.MainWindowTitle));
            }
            catch
            {
                // процесс завершился или нет доступа
            }
            finally
            {
                p.Dispose();
            }
        }
        return result
            .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
