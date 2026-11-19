using System.Text.Json;
using System.Text.Json.Serialization;
using WinPulse.Collectors.Display;
using WinPulse.Core;
using WinPulse.Core.Tweaks;
using WinPulse.Optimizer.Platform;

namespace WinPulse.Optimizer.Changes;

/// <summary>Исходное состояние того, что меняли. Хранится до отката.</summary>
public sealed class BackupEntry
{
    public string TargetId { get; set; } = "";
    public string ItemId { get; set; } = "";
    public DateTime SavedAt { get; set; }
    /// <summary>registry | power | display | detach | task</summary>
    public string Kind { get; set; } = "";
    public string Title { get; set; } = "";

    public RegHive Hive { get; set; }
    public string? Key { get; set; }
    public string? ValueName { get; set; }
    public string? SubKey { get; set; }
    /// <summary>Значение (или подзначение) было до изменения. false — «параметра не было».</summary>
    public bool Existed { get; set; }
    public string? ValueKind { get; set; }
    public string? Data { get; set; }

    public string? SchemeGuid { get; set; }
    public string? SchemeName { get; set; }
    public string? CreatedSchemeGuid { get; set; }

    public string? TaskPath { get; set; }

    public string? DeviceName { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public int Frequency { get; set; }
    public int PositionX { get; set; }
    public int PositionY { get; set; }
    public int BitsPerPixel { get; set; }
}

/// <summary>Содержимое файла резервной копии.</summary>
public sealed class BackupFile
{
    public int Version { get; set; } = 1;
    public string Module { get; set; } = "";
    public List<BackupEntry> Entries { get; set; } = new();
}

/// <summary>
/// Файл резервной копии модуля. Правило: исходное значение сохраняется только при ПЕРВОМ изменении цели,
/// повторные «Исправить» его не перезаписывают — «Вернуть как было» всегда возвращает настоящее исходное состояние.
/// </summary>
public sealed class BackupStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly string _module;
    private readonly object _gate = new();
    private List<BackupEntry> _entries = new();

    public BackupStore(string path, string module)
    {
        _path = path;
        _module = module;
        Load();
    }

    public string FilePath => _path;

    public IReadOnlyList<BackupEntry> Entries
    {
        get { lock (_gate) return _entries.ToList(); }
    }

    public bool Has(string targetId)
    {
        lock (_gate) return _entries.Any(e => e.TargetId == targetId);
    }

    public BackupEntry? Get(string targetId)
    {
        lock (_gate) return _entries.FirstOrDefault(e => e.TargetId == targetId);
    }

    public List<BackupEntry> ForItem(string itemId)
    {
        lock (_gate) return _entries.Where(e => e.ItemId == itemId).ToList();
    }

    public void Add(BackupEntry entry)
    {
        lock (_gate)
        {
            if (_entries.Any(e => e.TargetId == entry.TargetId)) return;
            _entries.Add(entry);
            Save();
        }
    }

    public void Update(BackupEntry entry)
    {
        lock (_gate)
        {
            int i = _entries.FindIndex(e => e.TargetId == entry.TargetId);
            if (i >= 0) _entries[i] = entry;
            else _entries.Add(entry);
            Save();
        }
    }

    public void Remove(string targetId)
    {
        lock (_gate)
        {
            if (_entries.RemoveAll(e => e.TargetId == targetId) > 0) Save();
        }
    }

    /// <summary>Запоминает исходное состояние цели изменения. true — запись создана сейчас (её не было).</summary>
    public bool SaveOriginal(Change c)
    {
        string target = c.TargetId;
        if (target.Length == 0 || Has(target)) return false;
        var entry = new BackupEntry
        {
            TargetId = target,
            ItemId = c.ItemId,
            SavedAt = DateTime.Now,
            Title = c.Title,
        };

        switch (c.Kind)
        {
            case ChangeKind.RegistrySet:
            case ChangeKind.RegistryDelete:
            {
                entry.Kind = "registry";
                entry.Hive = c.Hive;
                entry.Key = c.Key;
                entry.ValueName = c.ValueName;
                entry.SubKey = c.SubKey;
                var state = Reg.Read(c.Hive, c.Key, c.ValueName);
                if (c.SubKey is not null)
                {
                    string? sub = state.Exists ? KeyValueSettings.Get(state.Data, c.SubKey) : null;
                    entry.Existed = sub is not null;
                    entry.ValueKind = "String";
                    entry.Data = sub;
                }
                else
                {
                    entry.Existed = state.Exists;
                    entry.ValueKind = state.Kind;
                    entry.Data = state.Data;
                }
                break;
            }
            case ChangeKind.PowerActivate:
            {
                entry.Kind = "power";
                string? active = PowerSchemes.ActiveGuid();
                if (active is null) return false;
                entry.Existed = true;
                entry.SchemeGuid = active;
                entry.SchemeName = PowerSchemes.FriendlyName(active, PowerSchemes.List().FirstOrDefault(s => s.Guid == active)?.Name);
                break;
            }
            case ChangeKind.DisplayRefresh:
            {
                entry.Kind = "display";
                var monitor = Displays.Enumerate().FirstOrDefault(m => m.DeviceName.Equals(c.DeviceName, StringComparison.OrdinalIgnoreCase));
                if (monitor is null) return false;
                entry.Existed = true;
                entry.DeviceName = monitor.DeviceName;
                entry.Width = monitor.Width;
                entry.Height = monitor.Height;
                entry.Frequency = monitor.RefreshHz;
                break;
            }
            case ChangeKind.TaskEnabled:
            {
                entry.Kind = "task";
                bool? enabled = ScheduledTasks.IsEnabled(c.TaskPath ?? "");
                if (enabled is null) return false;
                entry.Existed = true;
                entry.TaskPath = c.TaskPath;
                entry.Data = enabled.Value ? "1" : "0";
                break;
            }
            default:
                return false;
        }

        Add(entry);
        return true;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var model = JsonSerializer.Deserialize<BackupFile>(File.ReadAllText(_path), Json);
            _entries = model?.Entries ?? new List<BackupEntry>();
        }
        catch (Exception ex)
        {
            Diag.Report("backup-load", ex);
            // Повреждённый файл не удаляем — переименовываем, чтобы его можно было посмотреть.
            try
            {
                File.Move(_path, _path + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), overwrite: true);
            }
            catch
            {
            }
            _entries = new List<BackupEntry>();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new BackupFile { Module = _module, Entries = _entries }, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Diag.Report("backup-save", ex);
            throw;
        }
    }
}
