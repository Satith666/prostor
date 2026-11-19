using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinPulse.Collectors;
using WinPulse.Collectors.Diagnostics;
using WinPulse.Core;
using WinPulse.Core.Diagnostics;

namespace WinPulse.App.Infrastructure;

/// <summary>
/// Замер задержек DPC/ISR. ETW-сессии ядра нужны права администратора: если их нет, замер делает
/// копия WinPulse с правами (только этот замер, без окна) и кладёт итог в JSON — сам WinPulse остаётся без прав.
/// </summary>
public static partial class LatencyRunner
{
    public static string Dir => Path.Combine(AppPaths.Root, "latency");

    [GeneratedRegex(@"^latency-[0-9a-f]{32}\.json$", RegexOptions.IgnoreCase)]
    private static partial Regex FileNamePattern();

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Режим помощника: WinPulse.exe --latency &lt;секунды&gt; &lt;файл&gt;. Возвращает код выхода.</summary>
    public static int RunHost(string[] args, int index)
    {
        if (index + 2 >= args.Length || !int.TryParse(args[index + 1], out int seconds) || seconds is < 1 or > 600) return 2;
        string file;
        try
        {
            file = Path.GetFullPath(args[index + 2]);
        }
        catch
        {
            return 2;
        }
        // Пишем только «свой» файл с случайным именем и никогда не перезаписываем существующий.
        if (!FileNamePattern().IsMatch(Path.GetFileName(file))) return 2;

        LatencyReport report;
        try
        {
            report = LatencyCapture.Capture(seconds);
        }
        catch (Exception ex)
        {
            report = new LatencyReport { Error = ex.Message };
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write);
            JsonSerializer.Serialize(stream, report, Json);
            return report.Error is null ? 0 : 1;
        }
        catch
        {
            return 3;
        }
    }

    public static async Task<LatencyReport> MeasureAsync(int seconds, IProgress<double> progress, CancellationToken ct)
    {
        if (OsInfo.IsElevated())
            return await Task.Run(() => LatencyCapture.Capture(seconds, ct, progress));

        Directory.CreateDirectory(Dir);
        string file = Path.Combine(Dir, $"latency-{Guid.NewGuid():N}.json");
        Process? process;
        try
        {
            process = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, $"--latency {seconds} \"{file}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new LatencyReport { Error = Lang.T("Запрос прав администратора отклонён — без них Windows не даёт читать события ядра.", "The administrator prompt was declined — Windows doesn't allow reading kernel events without it.") };
        }
        if (process is null) return new LatencyReport { Error = Lang.T("Не удалось запустить замер.", "Couldn't start the measurement.") };

        using (process)
        {
            var watch = Stopwatch.StartNew();
            while (!process.HasExited)
            {
                await Task.Delay(250, CancellationToken.None);
                progress.Report(Math.Min(1, watch.Elapsed.TotalSeconds / seconds));
                if (watch.Elapsed.TotalSeconds > seconds + 60) break; // помощник завис — не ждём вечно
            }
        }

        try
        {
            if (!File.Exists(file)) return new LatencyReport { Error = Lang.T("Замер не вернул результата.", "The measurement returned no result.") };
            await using var stream = File.OpenRead(file);
            return await JsonSerializer.DeserializeAsync<LatencyReport>(stream, Json, ct) ?? new LatencyReport { Error = "empty" };
        }
        finally
        {
            try { File.Delete(file); } catch { }
        }
    }
}
