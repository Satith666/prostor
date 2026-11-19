using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using WinPulse.Core;

namespace WinPulse.Optimizer.Platform;

public sealed record PowerScheme(string Guid, string Name, bool Active);

/// <summary>Схемы электропитания через powercfg.</summary>
public static partial class PowerSchemes
{
    public const string PowerSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";
    public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    public const string HighPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    public const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    [GeneratedRegex(@"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})(?:\s+\(([^)]*)\))?(\s*\*)?")]
    private static partial Regex SchemeLine();

    static PowerSchemes()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
        }
    }

    public static bool IsWellKnown(string guid) =>
        guid.Equals(PowerSaver, StringComparison.OrdinalIgnoreCase) ||
        guid.Equals(Balanced, StringComparison.OrdinalIgnoreCase) ||
        guid.Equals(HighPerformance, StringComparison.OrdinalIgnoreCase) ||
        guid.Equals(Ultimate, StringComparison.OrdinalIgnoreCase);

    public static string FriendlyName(string guid, string? fallback = null)
    {
        if (guid.Equals(PowerSaver, StringComparison.OrdinalIgnoreCase)) return Lang.T("Экономия энергии", "Power saver");
        if (guid.Equals(Balanced, StringComparison.OrdinalIgnoreCase)) return Lang.T("Сбалансированная", "Balanced");
        if (guid.Equals(HighPerformance, StringComparison.OrdinalIgnoreCase)) return Lang.T("Высокая производительность", "High performance");
        if (guid.Equals(Ultimate, StringComparison.OrdinalIgnoreCase)) return Lang.T("Максимальная производительность", "Ultimate performance");
        return string.IsNullOrWhiteSpace(fallback) ? guid : fallback!;
    }

    public static List<PowerScheme> List()
    {
        var result = new List<PowerScheme>();
        var (ok, output) = Run("/list");
        if (!ok) return result;
        foreach (Match m in SchemeLine().Matches(output))
        {
            string guid = m.Groups[1].Value.ToLowerInvariant();
            if (result.Any(r => r.Guid == guid)) continue;
            result.Add(new PowerScheme(guid, FriendlyName(guid, m.Groups[2].Value.Trim()), m.Groups[3].Success));
        }
        return result;
    }

    public static string? ActiveGuid()
    {
        var (ok, output) = Run("/getactivescheme");
        if (!ok) return null;
        var m = SchemeLine().Match(output);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    /// <summary>null — успех, иначе текст ошибки.</summary>
    public static string? SetActive(string guid)
    {
        var (ok, output) = Run("/setactive " + guid);
        if (!ok) return Clean(output);
        return string.Equals(ActiveGuid(), guid, StringComparison.OrdinalIgnoreCase) ? null : Clean(output);
    }

    /// <summary>Создаёт копию схемы (нужно, если «Высокая производительность» скрыта). Возвращает GUID копии.</summary>
    public static string? Duplicate(string guid, out string? error)
    {
        var (ok, output) = Run("/duplicatescheme " + guid);
        error = null;
        if (ok)
        {
            var m = SchemeLine().Match(output);
            if (m.Success) return m.Groups[1].Value.ToLowerInvariant();
        }
        error = Clean(output);
        return null;
    }

    public static string? Delete(string guid)
    {
        var (ok, output) = Run("/delete " + guid);
        return ok ? null : Clean(output);
    }

    private static string Clean(string output)
    {
        var s = output.Trim();
        return s.Length == 0 ? Lang.T("powercfg завершился с ошибкой", "powercfg failed") : s;
    }

    private static (bool Ok, string Output) Run(string args)
    {
        try
        {
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "powercfg.exe"), args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            try
            {
                var oem = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
                psi.StandardOutputEncoding = oem;
                psi.StandardErrorEncoding = oem;
            }
            catch
            {
            }
            using var p = Process.Start(psi);
            if (p is null) return (false, "");
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15000))
            {
                try { p.Kill(); } catch { }
                return (false, Lang.T("powercfg не ответил за 15 секунд", "powercfg timed out"));
            }
            return (p.ExitCode == 0, (stdout + "\n" + stderr).Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
