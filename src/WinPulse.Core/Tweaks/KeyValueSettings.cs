namespace WinPulse.Core.Tweaks;

/// <summary>
/// Строки вида «SwapEffectUpgradeEnable=1;VRROptimizeEnable=0;» из
/// HKCU\Software\Microsoft\DirectX\UserGpuPreferences.
/// Меняем только свой ключ, остальные пары и их порядок сохраняем.
/// </summary>
public static class KeyValueSettings
{
    public static List<KeyValuePair<string, string>> Parse(string? text)
    {
        var result = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = part.Trim();
            if (trimmed.Length == 0) continue;
            int eq = trimmed.IndexOf('=');
            if (eq <= 0) result.Add(new(trimmed, ""));
            else result.Add(new(trimmed[..eq].Trim(), trimmed[(eq + 1)..].Trim()));
        }
        return result;
    }

    public static string? Get(string? text, string key)
    {
        foreach (var kv in Parse(text))
            if (kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    public static string Set(string? text, string key, string value)
    {
        var pairs = Parse(text);
        bool found = false;
        for (int i = 0; i < pairs.Count; i++)
        {
            if (!pairs[i].Key.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            pairs[i] = new(pairs[i].Key, value);
            found = true;
        }
        if (!found) pairs.Add(new(key, value));
        return Format(pairs);
    }

    public static string Remove(string? text, string key)
    {
        var pairs = Parse(text);
        pairs.RemoveAll(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        return Format(pairs);
    }

    public static string Format(IEnumerable<KeyValuePair<string, string>> pairs) =>
        string.Concat(pairs.Select(kv => kv.Value.Length == 0 ? kv.Key + ";" : $"{kv.Key}={kv.Value};"));
}
