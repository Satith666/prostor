using System.Text.Json;

namespace WinPulse.Core.Updates;

/// <summary>Найденный новый выпуск WinPulse.</summary>
public sealed record UpdateInfo(Version Version, string Tag, string Title, string Notes, string PageUrl, string? SetupUrl, string? SetupName, string? Sha256);

/// <summary>Разбор выпусков GitHub (теги winpulse-vX.Y.Z) — без сети, чтобы проверялось тестами.</summary>
public static class ReleaseFeed
{
    public const string TagPrefix = "winpulse-v";

    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string s = text.Trim();
        if (s.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase)) s = s[TagPrefix.Length..];
        s = s.TrimStart('v', 'V');
        int cut = s.IndexOfAny(['-', '+', ' ']);
        if (cut >= 0) s = s[..cut];
        if (!Version.TryParse(s, out var v)) return null;
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    /// <summary>Выбор выпуска из ответа API (вынесено для тестов).</summary>
    public static UpdateInfo? Pick(JsonElement releases, Version current)
    {
        UpdateInfo? best = null;
        foreach (var r in releases.EnumerateArray())
        {
            string tag = r.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            if (!tag.StartsWith(TagPrefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (r.TryGetProperty("draft", out var d) && d.GetBoolean()) continue;
            if (r.TryGetProperty("prerelease", out var p) && p.GetBoolean()) continue;
            var version = ParseVersion(tag);
            if (version is null || version <= current || best is not null && version <= best.Version) continue;

            string? setupUrl = null, setupName = null, sha = null;
            if (r.TryGetProperty("assets", out var assets))
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.GetProperty("name").GetString() ?? "";
                    if (!name.StartsWith("WinPulse-Setup-", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    setupName = name;
                    setupUrl = a.GetProperty("browser_download_url").GetString();
                    if (a.TryGetProperty("digest", out var dg) && dg.GetString() is string digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        sha = digest[7..].ToLowerInvariant();
                }
            }
            best = new UpdateInfo(
                version,
                tag,
                r.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag,
                r.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                r.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "",
                setupUrl,
                setupName,
                sha);
        }
        return best;
    }

    /// <summary>Строка «hash  имя» из SHA256SUMS.txt.</summary>
    public static string? ShaFromSums(string sums, string fileName)
    {
        foreach (var line in sums.Split('\n'))
        {
            var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2 && parts[1].Trim().TrimStart('*').Equals(fileName, StringComparison.OrdinalIgnoreCase) && parts[0].Length == 64)
                return parts[0].ToLowerInvariant();
        }
        return null;
    }
}
