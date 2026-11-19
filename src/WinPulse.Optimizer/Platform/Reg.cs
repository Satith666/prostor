using System.Globalization;
using Microsoft.Win32;
using WinPulse.Core.Tweaks;
using WinPulse.Optimizer.Changes;
using WinPulse.Core;

namespace WinPulse.Optimizer.Platform;

/// <summary>Состояние значения реестра: было ли оно, какого типа и что в нём.</summary>
public sealed record RegState(bool Exists, string Kind, string? Data);

/// <summary>Чтение и запись реестра (всегда 64-битное представление).</summary>
public static class Reg
{
    public static RegistryKey Base(RegHive hive) =>
        RegistryKey.OpenBaseKey(hive == RegHive.LocalMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);

    public static RegState Read(RegHive hive, string key, string name)
    {
        try
        {
            using var root = Base(hive);
            using var k = root.OpenSubKey(key, writable: false);
            if (k is null) return new RegState(false, "DWord", null);
            var value = k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (value is null) return new RegState(false, "DWord", null);
            var kind = k.GetValueKind(name);
            return new RegState(true, kind.ToString(), Serialize(kind, value));
        }
        catch
        {
            return new RegState(false, "DWord", null);
        }
    }

    public static string? GetString(RegHive hive, string key, string name)
    {
        var s = Read(hive, key, name);
        return s.Exists ? s.Data : null;
    }

    public static int? GetDword(RegHive hive, string key, string name)
    {
        var s = Read(hive, key, name);
        return s.Exists && s.Kind == nameof(RegistryValueKind.DWord) && int.TryParse(s.Data, out int v) ? v : null;
    }

    /// <summary>Подзначение внутри строки «A=1;B=2;».</summary>
    public static string? GetSub(RegHive hive, string key, string name, string subKey) =>
        KeyValueSettings.Get(GetString(hive, key, name), subKey);

    public static void Apply(Change c)
    {
        using var root = Base(c.Hive);
        if (c.SubKey is not null)
        {
            using var k = root.CreateSubKey(c.Key, writable: true) ?? throw new IOException(Lang.T("Не удалось открыть раздел ", "Couldn't open key ") + c.Key);
            string? current = k.GetValue(c.ValueName) as string;
            string updated = c.Kind == ChangeKind.RegistryDelete || c.Data is null
                ? KeyValueSettings.Remove(current, c.SubKey)
                : KeyValueSettings.Set(current, c.SubKey, c.Data);
            if (updated.Length == 0)
            {
                if (current is not null) k.DeleteValue(c.ValueName, throwOnMissingValue: false);
            }
            else
            {
                k.SetValue(c.ValueName, updated, RegistryValueKind.String);
            }
            return;
        }

        if (c.Kind == ChangeKind.RegistryDelete)
        {
            using var k = root.OpenSubKey(c.Key, writable: true);
            k?.DeleteValue(c.ValueName, throwOnMissingValue: false);
            return;
        }

        using (var k = root.CreateSubKey(c.Key, writable: true) ?? throw new IOException(Lang.T("Не удалось открыть раздел ", "Couldn't open key ") + c.Key))
        {
            var kind = Enum.TryParse<RegistryValueKind>(c.ValueKind, out var parsed) ? parsed : RegistryValueKind.String;
            k.SetValue(c.ValueName, Deserialize(kind, c.Data ?? ""), kind);
        }
    }

    private static string Serialize(RegistryValueKind kind, object value) => kind switch
    {
        RegistryValueKind.DWord => Convert.ToInt32(value).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => Convert.ToInt64(value).ToString(CultureInfo.InvariantCulture),
        RegistryValueKind.MultiString => string.Join("\n", (string[])value),
        RegistryValueKind.Binary => Convert.ToBase64String((byte[])value),
        _ => value.ToString() ?? "",
    };

    private static object Deserialize(RegistryValueKind kind, string data) => kind switch
    {
        RegistryValueKind.DWord => int.Parse(data, CultureInfo.InvariantCulture),
        RegistryValueKind.QWord => long.Parse(data, CultureInfo.InvariantCulture),
        RegistryValueKind.MultiString => data.Split('\n'),
        RegistryValueKind.Binary => Convert.FromBase64String(data),
        _ => data,
    };
}
