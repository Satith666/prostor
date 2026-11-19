using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using WinPulse.Core;
using WinPulse.Core.Updates;

namespace WinPulse.App.Infrastructure;

/// <summary>
/// Автообновление через выпуски GitHub (теги winpulse-vX.Y.Z в репозитории). Подписи у сборок пока нет,
/// поэтому установщик проверяется по SHA-256 из того же выпуска — это защита от битой загрузки.
/// Установленная версия обновляется тихой установкой поверх; переносная — открывается страница выпуска.
/// </summary>
public static class Updater
{
    private const string Releases = "https://api.github.com/repos/Satith666/prostor/releases?per_page=30";

    public static string Dir => Path.Combine(AppPaths.Root, "update");

    /// <summary>Текущая версия (числовая часть: «0.9.0-dev.12» → 0.9.0).</summary>
    public static Version Current { get; } = ReleaseFeed.ParseVersion(
        typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Updater).Assembly.GetName().Version?.ToString() ?? "0.0.0") ?? new Version(0, 0, 0);

    /// <summary>Строка версии для показа: «0.9.0» или «0.9.0-dev.12».</summary>
    public static string CurrentText { get; } =
        (typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? Current.ToString(3))
        .Split('+')[0];

    /// <summary>Установлен установщиком (рядом лежит деинсталлятор Inno Setup).</summary>
    public static bool IsInstalled => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    private static HttpClient Client()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"WinPulse/{Current.ToString(3)}");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>Самый новый выпуск новее текущей версии или null.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        using var http = Client();
        await using var stream = await http.GetStreamAsync(Releases, ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        return ReleaseFeed.Pick(doc.RootElement, Current);
    }

    /// <summary>Скачивает установщик и проверяет SHA-256. Возвращает путь к файлу.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo update, IProgress<double> progress, CancellationToken ct = default)
    {
        if (update.SetupUrl is null || update.SetupName is null)
            throw new InvalidOperationException(Lang.T("В выпуске нет установщика.", "The release has no installer."));

        using var http = Client();
        string? expected = update.Sha256 ?? await ShaFromSumsAsync(http, update, ct);
        if (expected is null)
            throw new InvalidOperationException(Lang.T("В выпуске нет контрольной суммы — установка отменена.", "The release has no checksum — update cancelled."));

        Directory.CreateDirectory(Dir);
        foreach (var old in Directory.GetFiles(Dir, "WinPulse-Setup-*.exe"))
            try { File.Delete(old); } catch { }
        string path = Path.Combine(Dir, update.SetupName);

        using (var response = await http.GetAsync(update.SetupUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? 0;
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            var buffer = new byte[81920];
            long done = 0;
            int read;
            while ((read = await input.ReadAsync(buffer, ct)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress.Report((double)done / total);
            }
        }

        string actual;
        await using (var file = File.OpenRead(path))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
        if (actual != expected)
        {
            try { File.Delete(path); } catch { }
            throw new InvalidOperationException(Lang.T("Контрольная сумма не совпала — файл повреждён, установка отменена.", "Checksum mismatch — the file is corrupted, update cancelled."));
        }
        return path;
    }

    private static async Task<string?> ShaFromSumsAsync(HttpClient http, UpdateInfo update, CancellationToken ct)
    {
        string sumsUrl = update.SetupUrl![..(update.SetupUrl!.LastIndexOf('/') + 1)] + "SHA256SUMS.txt";
        try
        {
            string sums = await http.GetStringAsync(sumsUrl, ct);
            return ReleaseFeed.ShaFromSums(sums, update.SetupName!);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    /// <summary>Запускает тихую установку поверх. После неё установщик сам запустит WinPulse снова.</summary>
    public static void RunInstaller(string setupPath)
    {
        Process.Start(new ProcessStartInfo(setupPath, $"/SILENT /SUPPRESSMSGBOXES /NORESTART /LANG={(Lang.English ? "en" : "ru")}")
        {
            UseShellExecute = true,
        });
    }
}
