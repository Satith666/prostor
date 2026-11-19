using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinPulse.Core;
using WinPulse.Core.Gaming;

namespace WinPulse.Collectors.Gaming;

/// <summary>
/// Консольный PresentMon от Intel (github.com/GameTechDev/PresentMon, лицензия MIT): ищем рядом с программой
/// или в папке инструментов, при необходимости скачиваем официальный релиз с GitHub.
/// </summary>
public static partial class PresentMonTool
{
    public static string ToolsDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPulse", "tools");

    public static string CapturesDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPulse", "captures");

    [GeneratedRegex(@"^PresentMon-[\d.]+-x64\.exe$", RegexOptions.IgnoreCase)]
    private static partial Regex AssetName();

    /// <summary>Путь к PresentMon или null. Сначала рядом с WinPulse.exe, потом в папке инструментов (самый новый).</summary>
    public static string? Find()
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, ToolsDir })
        {
            try
            {
                if (!Directory.Exists(dir)) continue;
                var found = Directory.GetFiles(dir, "PresentMon*.exe")
                    .Where(f => !Path.GetFileName(f).Contains("Service", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (found is not null) return found;
            }
            catch
            {
                // Нет доступа к папке — ищем дальше.
            }
        }
        return null;
    }

    /// <summary>Скачивает последний консольный PresentMon x64 из релизов на GitHub. Возвращает путь к файлу.</summary>
    public static async Task<string> DownloadAsync(IProgress<string>? progress, CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WinPulse/0.1 (+https://github.com)");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

        progress?.Report(Lang.T("Ищу последний выпуск PresentMon на GitHub…", "Looking up the latest PresentMon release on GitHub…"));
        string json = await http.GetStringAsync("https://api.github.com/repos/GameTechDev/PresentMon/releases?per_page=20", ct);
        using var doc = JsonDocument.Parse(json);

        string? url = null, name = null;
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                string assetName = asset.GetProperty("name").GetString() ?? "";
                if (!AssetName().IsMatch(assetName)) continue;
                url = asset.GetProperty("browser_download_url").GetString();
                name = assetName;
                break;
            }
            if (url is not null) break;
        }
        if (url is null || name is null)
            throw new InvalidOperationException(Lang.T("В релизах PresentMon не нашлось консольной версии x64.", "No x64 console build found in PresentMon releases."));

        progress?.Report(Lang.T($"Скачиваю {name}…", $"Downloading {name}…"));
        var bytes = await http.GetByteArrayAsync(url, ct);
        if (bytes.Length < 100_000 || bytes[0] != 'M' || bytes[1] != 'Z')
            throw new InvalidOperationException(Lang.T("Скачанный файл не похож на программу — отменено.", "The downloaded file doesn't look like a program — cancelled."));

        Directory.CreateDirectory(ToolsDir);
        string path = Path.Combine(ToolsDir, name);
        string temp = path + ".part";
        await File.WriteAllBytesAsync(temp, bytes, ct);
        File.Move(temp, path, overwrite: true);
        progress?.Report(Lang.T("PresentMon готов.", "PresentMon is ready."));
        return path;
    }
}

/// <summary>Чем закончился замер.</summary>
public sealed class CaptureResult
{
    public required IReadOnlyList<FrameSample> Frames { get; init; }
    public FrameStats? Stats { get; init; }
    public string? CsvPath { get; init; }
    public string? Error { get; init; }
    public bool Cancelled { get; init; }
}

/// <summary>
/// Один замер: PresentMon пишет CSV, а мы читаем файл по мере записи — так видно FPS вживую,
/// даже если PresentMon запущен с правами администратора (его вывод тогда не перехватить).
/// </summary>
public sealed class PresentMonCapture
{
    private readonly string _exe;
    private readonly string _processName;
    private readonly int _seconds;

    public PresentMonCapture(string presentMonExe, string processExeName, int seconds)
    {
        _exe = presentMonExe;
        _processName = processExeName;
        _seconds = Math.Clamp(seconds, 5, 3600);
    }

    /// <summary>FPS за последнюю секунду — раз в секунду во время замера.</summary>
    public event Action<double>? LiveFps;

    public async Task<CaptureResult> RunAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(PresentMonTool.CapturesDir);
        string csv = Path.Combine(PresentMonTool.CapturesDir, $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.csv");
        string session = "WinPulse" + Environment.ProcessId;
        string args = $"--process_name \"{_processName}\" --output_file \"{csv}\" --timed {_seconds} --terminate_after_timed " +
                      $"--terminate_on_proc_exit --no_console_stats --stop_existing_session --session_name {session}";

        Process? process;
        try
        {
            bool elevated = OsInfo.IsElevated();
            var psi = new ProcessStartInfo(_exe, args)
            {
                UseShellExecute = !elevated,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            // Без прав администратора PresentMon не видит события видеокарты — просим права только у него, не у WinPulse.
            if (!elevated) psi.Verb = "runas";
            process = Process.Start(psi);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return new CaptureResult { Frames = [], Cancelled = true, Error = Lang.T("Запрос прав администратора отклонён.", "The administrator prompt was declined.") };
        }
        catch (Exception ex)
        {
            return new CaptureResult { Frames = [], Error = ex.Message };
        }
        if (process is null) return new CaptureResult { Frames = [], Error = Lang.T("PresentMon не запустился.", "PresentMon didn't start.") };

        var parser = new PresentMonCsv();
        var frames = new List<FrameSample>();
        var pending = new StringBuilder();
        long position = 0;
        var started = DateTime.UtcNow;
        var lastLive = DateTime.UtcNow;
        int liveFrom = 0;
        bool cancelled = false;

        try
        {
            while (true)
            {
                bool exited;
                try
                {
                    exited = process.HasExited;
                }
                catch
                {
                    exited = false; // процесс с правами администратора: статус можем не прочитать — ждём по времени
                }

                position = ReadNew(csv, position, pending, line =>
                {
                    var frame = parser.Parse(line, out string app, out _);
                    if (frame is not null && (app.Length == 0 || app.Equals(_processName, StringComparison.OrdinalIgnoreCase)))
                        frames.Add(frame.Value);
                });

                if ((DateTime.UtcNow - lastLive).TotalSeconds >= 1)
                {
                    double ms = 0;
                    for (int i = liveFrom; i < frames.Count; i++) ms += frames[i].FrameTimeMs;
                    int count = frames.Count - liveFrom;
                    if (count > 0 && ms > 0) LiveFps?.Invoke(count * 1000.0 / ms);
                    liveFrom = frames.Count;
                    lastLive = DateTime.UtcNow;
                }

                if (exited || ct.IsCancellationRequested) break;
                if ((DateTime.UtcNow - started).TotalSeconds > _seconds + 20) break; // страховка
                await Task.Delay(250, CancellationToken.None);
            }
            cancelled = ct.IsCancellationRequested;
            await Task.Delay(300, CancellationToken.None);
            ReadNew(csv, position, pending, line =>
            {
                var frame = parser.Parse(line, out string app, out _);
                if (frame is not null && (app.Length == 0 || app.Equals(_processName, StringComparison.OrdinalIgnoreCase)))
                    frames.Add(frame.Value);
            });
        }
        finally
        {
            process.Dispose();
        }

        string? error = null;
        if (frames.Count == 0)
            error = !File.Exists(csv)
                ? Lang.T("PresentMon не создал файл замера. Возможно, его заблокировал антивирус.", "PresentMon didn't create a capture file. An antivirus may have blocked it.")
                : Lang.T("Кадров не записано: игра не рисовала кадры через DirectX/Vulkan или была свёрнута.", "No frames recorded: the game didn't present frames via DirectX/Vulkan or was minimized.");

        return new CaptureResult
        {
            Frames = frames,
            Stats = FrameStats.Compute(frames),
            CsvPath = File.Exists(csv) ? csv : null,
            Error = error,
            Cancelled = cancelled,
        };
    }

    /// <summary>Дочитывает файл с позиции; неполную последнюю строку оставляет до следующего раза.</summary>
    private static long ReadNew(string path, long position, StringBuilder pending, Action<string> onLine)
    {
        if (!File.Exists(path)) return position;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length <= position) return position;
            fs.Seek(position, SeekOrigin.Begin);
            var buffer = new byte[fs.Length - position];
            int read = fs.Read(buffer, 0, buffer.Length);
            pending.Append(Encoding.UTF8.GetString(buffer, 0, read));
            position += read;

            string text = pending.ToString();
            int last = text.LastIndexOf('\n');
            if (last < 0) return position;
            foreach (var line in text[..last].Split('\n')) onLine(line.TrimEnd('\r'));
            pending.Clear();
            pending.Append(text[(last + 1)..]);
        }
        catch (IOException)
        {
            // Файл занят — попробуем через четверть секунды.
        }
        return position;
    }
}

/// <summary>Какой процесс сейчас на переднем плане.</summary>
public static class Foreground
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public static int ProcessId()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return 0;
            GetWindowThreadProcessId(hwnd, out uint pid);
            return (int)pid;
        }
        catch
        {
            return 0;
        }
    }
}
