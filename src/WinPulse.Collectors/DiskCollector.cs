using WinPulse.Collectors.Interop;
using WinPulse.Core;
using WinPulse.Core.Model;

namespace WinPulse.Collectors;

/// <summary>
/// Физические диски: загрузка и время ответа (PDH PhysicalDisk), модель и тип (IOCTL_STORAGE_QUERY_PROPERTY),
/// тома с буквами и свободным местом.
/// </summary>
internal sealed unsafe class DiskCollector
{
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;

    private readonly PdhCounter? _idle;
    private readonly PdhCounter? _latency;
    private readonly PdhCounter? _read;
    private readonly PdhCounter? _write;
    private readonly PdhCounter? _queue;
    private readonly Dictionary<int, (string Model, DiskMediaType Type, string Bus)> _static = new();
    private readonly Dictionary<string, VolumeState> _volumes = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _volumesTime = DateTime.MinValue;

    public DiskCollector(PdhQuery query)
    {
        _idle = query.Add(@"\PhysicalDisk(*)\% Idle Time");
        _latency = query.Add(@"\PhysicalDisk(*)\Avg. Disk sec/Transfer");
        _read = query.Add(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
        _write = query.Add(@"\PhysicalDisk(*)\Disk Write Bytes/sec");
        _queue = query.Add(@"\PhysicalDisk(*)\Current Disk Queue Length");
    }

    public List<DiskState> Read()
    {
        var idle = ToMap(_idle);
        var latency = ToMap(_latency);
        var read = ToMap(_read);
        var write = ToMap(_write);
        var queue = ToMap(_queue);

        bool refreshVolumes = (DateTime.UtcNow - _volumesTime).TotalSeconds > 30;
        if (refreshVolumes)
        {
            _volumesTime = DateTime.UtcNow;
            _volumes.Clear();
        }

        var result = new List<DiskState>();
        foreach (var instance in idle.Keys)
        {
            if (!TryParseInstance(instance, out int index, out var letters)) continue;
            if (!_static.TryGetValue(index, out var info))
            {
                info = QueryStatic(index);
                _static[index] = info;
            }

            var volumes = new List<VolumeState>();
            foreach (var letter in letters)
            {
                if (!_volumes.TryGetValue(letter, out var vol))
                {
                    vol = ReadVolume(letter);
                    _volumes[letter] = vol;
                }
                volumes.Add(vol);
            }

            result.Add(new DiskState
            {
                Index = index,
                Model = info.Model,
                MediaType = info.Type,
                Bus = info.Bus,
                Volumes = volumes,
                ActivePercent = Math.Clamp(100 - idle[instance], 0, 100),
                LatencyMs = latency.TryGetValue(instance, out var l) ? l * 1000 : 0,
                ReadBps = read.TryGetValue(instance, out var r) ? r : 0,
                WriteBps = write.TryGetValue(instance, out var w) ? w : 0,
                QueueLength = queue.TryGetValue(instance, out var q) ? q : 0,
            });
        }
        return result.OrderBy(d => d.Index).ToList();
    }

    private static Dictionary<string, double> ToMap(PdhCounter? counter)
    {
        var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        if (counter is null) return map;
        foreach (var kv in counter.Values()) map[kv.Key] = kv.Value;
        return map;
    }

    /// <summary>«0 C: D:» → 0 и [C:, D:]. «_Total» пропускаем.</summary>
    internal static bool TryParseInstance(string instance, out int index, out List<string> letters)
    {
        letters = new List<string>();
        index = -1;
        var parts = instance.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], out index)) return false;
        for (int i = 1; i < parts.Length; i++)
            if (parts[i].Length == 2 && parts[i][1] == ':') letters.Add(parts[i].ToUpperInvariant());
        return true;
    }

    private static VolumeState ReadVolume(string letter)
    {
        try
        {
            var d = new DriveInfo(letter + "\\");
            if (!d.IsReady) return new VolumeState(letter, null, 0, 0);
            string? label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? null : d.VolumeLabel;
            return new VolumeState(letter, label, d.TotalSize, d.TotalFreeSpace);
        }
        catch
        {
            return new VolumeState(letter, null, 0, 0);
        }
    }

    internal static (string Model, DiskMediaType Type, string Bus) QueryStatic(int index)
    {
        string model = $"Диск {index}";
        var type = DiskMediaType.Unknown;
        string bus = "";
        try
        {
            using var handle = Native.CreateFile($@"\\.\PhysicalDrive{index}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (handle.IsInvalid) return (model, type, bus);

            int* query = stackalloc int[3];
            byte* buffer = stackalloc byte[1024];

            query[0] = 0; // StorageDeviceProperty
            query[1] = 0; // PropertyStandardQuery
            query[2] = 0;
            int busType = 0;
            if (Native.DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, query, 12, buffer, 1024, out int returned, IntPtr.Zero)
                && returned >= 36)
            {
                string? vendor = ReadAnsi(buffer, *(int*)(buffer + 12), returned);
                string? product = ReadAnsi(buffer, *(int*)(buffer + 16), returned);
                busType = *(int*)(buffer + 28);
                if (product is not null)
                {
                    bool generic = vendor is null || vendor is "ATA" or "NVMe" or "SCSI" or "USB" ||
                                   product.StartsWith(vendor, StringComparison.OrdinalIgnoreCase);
                    model = generic ? product : vendor + " " + product;
                }
                bus = BusName(busType);
            }

            bool? seekPenalty = null;
            query[0] = 7; // StorageDeviceSeekPenaltyProperty
            if (Native.DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY, query, 12, buffer, 12, out returned, IntPtr.Zero)
                && returned >= 9)
            {
                seekPenalty = buffer[8] != 0;
            }

            type = busType == 17 ? DiskMediaType.Nvme
                : seekPenalty == false ? DiskMediaType.Ssd
                : seekPenalty == true ? DiskMediaType.Hdd
                : DiskMediaType.Unknown;
        }
        catch (Exception ex)
        {
            Diag.Report("disk-info", ex);
        }
        return (model, type, bus);
    }

    private static string? ReadAnsi(byte* buffer, int offset, int length)
    {
        if (offset <= 0 || offset >= length) return null;
        var chars = new List<char>();
        for (int i = offset; i < length && buffer[i] != 0; i++) chars.Add((char)buffer[i]);
        var s = new string(chars.ToArray()).Trim();
        return s.Length > 0 ? s : null;
    }

    private static string BusName(int bus) => bus switch
    {
        1 => "SCSI",
        3 => "ATA",
        7 => "USB",
        8 => "RAID",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        14 => "Virtual",
        16 => "Storage Spaces",
        17 => "NVMe",
        19 => "UFS",
        _ => "",
    };
}
