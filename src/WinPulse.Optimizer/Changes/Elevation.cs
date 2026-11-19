using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using WinPulse.Core;
using WinPulse.Core.Startup;
using WinPulse.Optimizer.Platform;

namespace WinPulse.Optimizer.Changes;

internal static class OpsJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string OpsDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinPulse", "ops");
}

/// <summary>
/// Запускает этот же exe с правами администратора только для изменений, которым они нужны (HKLM, powercfg):
/// WinPulse.exe --elevated-ops &lt;файл&gt;. Один запрос UAC на всю пачку изменений.
/// </summary>
public static class ElevatedRunner
{
    private const int ERROR_CANCELLED = 1223;

    /// <summary>null — пользователь отказал в запросе UAC.</summary>
    public static async Task<List<ChangeResult>?> RunAsync(IReadOnlyList<Change> changes)
    {
        Directory.CreateDirectory(OpsJson.OpsDirectory);
        string file = Path.Combine(OpsJson.OpsDirectory, $"ops-{Guid.NewGuid():N}.json");
        string resultFile = file + ".result.json";
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(changes, OpsJson.Options));
        try
        {
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException("ProcessPath");
            var psi = new ProcessStartInfo(exe, $"--elevated-ops \"{file}\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            Process? process;
            try
            {
                process = Process.Start(psi);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ERROR_CANCELLED)
            {
                return null;
            }
            if (process is null) return null;

            using (process)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    throw new TimeoutException(Lang.T("Вспомогательный процесс не ответил за 2 минуты", "The helper process did not finish in 2 minutes"));
                }
            }

            if (!File.Exists(resultFile))
                throw new IOException(Lang.T("Вспомогательный процесс не вернул результат", "The helper process returned no result"));
            return JsonSerializer.Deserialize<List<ChangeResult>>(await File.ReadAllTextAsync(resultFile), OpsJson.Options)
                   ?? new List<ChangeResult>();
        }
        finally
        {
            TryDelete(file);
            TryDelete(resultFile);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
        }
    }
}

/// <summary>
/// Код вспомогательного процесса с правами администратора. Выполняет только изменения из белого списка:
/// даже если кто-то подменит файл заданий, ничего лишнего с правами администратора сделано не будет.
/// </summary>
public static partial class ElevatedHost
{
    private sealed record Allowed(string Key, Func<string, bool> Name, string ValueKind);

    private static bool Is(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private static readonly Allowed[] HklmWhitelist =
    {
        new(@"SYSTEM\CurrentControlSet\Control\GraphicsDrivers", n => Is(n, "HwSchMode"), "DWord"),
        new(@"SOFTWARE\Microsoft\Windows\Dwm", n => Is(n, "OverlayTestMode"), "DWord"),
        new(@"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers",
            n => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && n.Contains('\\'), "String"),
        new(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", n => Is(n, "Enabled"), "DWord"),
        new(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", n => Is(n, "PagingFiles"), "MultiString"),
        new(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power", n => Is(n, "HiberbootEnabled"), "DWord"),
        new(StartupApproved.MachineRun, n => n.Length > 0, "Binary"),
        new(StartupApproved.MachineRun32, n => n.Length > 0, "Binary"),
        new(StartupApproved.MachineFolder, n => n.Length > 0, "Binary"),
    };

    [GeneratedRegex(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$", RegexOptions.IgnoreCase)]
    private static partial Regex GuidPattern();

    [GeneratedRegex(@"^ops-[0-9a-f]{32}\.json$", RegexOptions.IgnoreCase)]
    private static partial Regex FileNamePattern();

    public static int Run(string file)
    {
        try
        {
            var full = Path.GetFullPath(file);
            var dir = Path.GetDirectoryName(full) ?? "";
            if (!FileNamePattern().IsMatch(Path.GetFileName(full)) ||
                !dir.EndsWith(Path.Combine("WinPulse", "ops"), StringComparison.OrdinalIgnoreCase))
                return 2;

            var changes = JsonSerializer.Deserialize<List<Change>>(File.ReadAllText(full), OpsJson.Options) ?? new List<Change>();
            var results = new List<ChangeResult>();
            for (int i = 0; i < changes.Count; i++)
            {
                var c = changes[i];
                results.Add(IsAllowed(c)
                    ? ChangeApplier.Apply(c, i)
                    : new ChangeResult { Index = i, Success = false, Error = "Изменение не входит в разрешённый список / not allowed" });
            }
            File.WriteAllText(full + ".result.json", JsonSerializer.Serialize(results, OpsJson.Options));
            return 0;
        }
        catch
        {
            return 1;
        }
    }

    internal static bool IsAllowed(Change c)
    {
        switch (c.Kind)
        {
            case ChangeKind.RegistrySet:
            case ChangeKind.RegistryDelete:
                if (c.Hive != RegHive.LocalMachine || c.SubKey is not null) return false;
                foreach (var a in HklmWhitelist)
                {
                    if (!a.Key.Equals(c.Key, StringComparison.OrdinalIgnoreCase)) continue;
                    bool kindOk = c.Kind == ChangeKind.RegistryDelete || a.ValueKind.Equals(c.ValueKind, StringComparison.OrdinalIgnoreCase);
                    if (a.Name(c.ValueName) && kindOk) return true;
                }
                return false;
            case ChangeKind.PowerActivate:
                return c.SchemeGuid is not null && GuidPattern().IsMatch(c.SchemeGuid);
            case ChangeKind.PowerDelete:
                return c.SchemeGuid is not null && GuidPattern().IsMatch(c.SchemeGuid) && !PowerSchemes.IsWellKnown(c.SchemeGuid);
            case ChangeKind.TaskEnabled:
                return c.TaskPath is { Length: > 1 } path && path.StartsWith('\\') && !ScheduledTasks.IsSystemPath(path) && c.Data is "0" or "1";
            default:
                return false;
        }
    }
}
