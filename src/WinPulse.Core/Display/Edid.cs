namespace WinPulse.Core.Display;

/// <summary>Поддержка переменной частоты (G-SYNC / FreeSync / Adaptive-Sync / HDMI VRR).</summary>
public enum VrrSupport
{
    /// <summary>Признаков VRR в EDID нет.</summary>
    No,
    /// <summary>Явных меток нет, но широкий диапазон частот у монитора 75+ Гц — скорее всего поддерживает.</summary>
    Likely,
    /// <summary>Есть явная метка: блок FreeSync, Adaptive-Sync (DisplayID) или HDMI VRR.</summary>
    Yes,
}

public sealed class EdidInfo
{
    public string? ManufacturerId { get; init; }
    public string? MonitorName { get; init; }
    public int? RangeMinHz { get; init; }
    public int? RangeMaxHz { get; init; }
    public int? PreferredRefreshHz { get; init; }
    public bool HasFreeSyncBlock { get; init; }
    public int? FreeSyncMinHz { get; init; }
    public int? FreeSyncMaxHz { get; init; }
    public bool HasAdaptiveSyncBlock { get; init; }
    public bool HasHdmiVrr { get; init; }
    public int? HdmiVrrMinHz { get; init; }
    public int? HdmiVrrMaxHz { get; init; }

    /// <summary>Вывод по EDID. maxModeHz — максимальная частота из списка режимов Windows (если известна).</summary>
    public VrrSupport Vrr(int? maxModeHz = null)
    {
        if (HasFreeSyncBlock || HasAdaptiveSyncBlock || HasHdmiVrr) return VrrSupport.Yes;
        int max = Math.Max(maxModeHz ?? 0, Math.Max(RangeMaxHz ?? 0, PreferredRefreshHz ?? 0));
        if (RangeMinHz is int min && RangeMaxHz is int rmax && max >= 75 && rmax >= 75 && min > 0 && rmax >= min * 2)
            return VrrSupport.Likely;
        return VrrSupport.No;
    }

    /// <summary>Диапазон VRR для подсказки, если известен.</summary>
    public (int Min, int Max)? VrrRange =>
        FreeSyncMinHz is int a && FreeSyncMaxHz is int b && b > a ? (a, b)
        : HdmiVrrMinHz is int c && HdmiVrrMaxHz is int d && d > c ? (c, d)
        : RangeMinHz is int e && RangeMaxHz is int f && f > e ? (e, f)
        : null;
}

/// <summary>
/// Разбор EDID монитора (базовый блок 128 байт + расширения CTA-861 и DisplayID).
/// Ищем признаки VRR: AMD FreeSync VSDB (OUI 00-00-1A), HDMI Forum VSDB с VRRmin/VRRmax,
/// DisplayID 2.x Adaptive-Sync (тег 0x2B), а также диапазон частот (дескриптор 0xFD).
/// </summary>
public static class Edid
{
    private static readonly byte[] Header = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };

    public static EdidInfo? Parse(byte[]? edid)
    {
        if (edid is null || edid.Length < 128) return null;
        for (int i = 0; i < 8; i++)
            if (edid[i] != Header[i]) return null;

        string? manufacturer = ParseManufacturer(edid[8], edid[9]);
        string? name = null;
        int? rangeMin = null, rangeMax = null, preferred = null;

        for (int d = 54; d <= 108; d += 18)
        {
            if (edid[d] == 0 && edid[d + 1] == 0)
            {
                byte tag = edid[d + 3];
                if (tag == 0xFC)
                {
                    name = ReadText(edid, d + 5, 13);
                }
                else if (tag == 0xFD)
                {
                    byte flags = edid[d + 4];
                    int min = edid[d + 5] + ((flags & 0x01) != 0 ? 255 : 0);
                    int max = edid[d + 6] + ((flags & 0x02) != 0 ? 255 : 0);
                    if (min > 0 && max >= min)
                    {
                        rangeMin = min;
                        rangeMax = max;
                    }
                }
            }
            else if (preferred is null)
            {
                preferred = TimingRefresh(edid, d);
            }
        }

        bool freeSync = false, adaptive = false, hdmiVrr = false;
        int? fsMin = null, fsMax = null, hdmiMin = null, hdmiMax = null;

        int extensions = edid[126];
        for (int e = 1; e <= extensions; e++)
        {
            int off = e * 128;
            if (off + 128 > edid.Length) break;
            byte tag = edid[off];
            if (tag == 0x02)
            {
                ParseCta(edid, off, ref freeSync, ref fsMin, ref fsMax, ref hdmiVrr, ref hdmiMin, ref hdmiMax);
            }
            else if (tag == 0x70)
            {
                adaptive |= ParseDisplayId(edid, off);
            }
        }

        return new EdidInfo
        {
            ManufacturerId = manufacturer,
            MonitorName = name,
            RangeMinHz = rangeMin,
            RangeMaxHz = rangeMax,
            PreferredRefreshHz = preferred,
            HasFreeSyncBlock = freeSync,
            FreeSyncMinHz = fsMin,
            FreeSyncMaxHz = fsMax,
            HasAdaptiveSyncBlock = adaptive,
            HasHdmiVrr = hdmiVrr,
            HdmiVrrMinHz = hdmiMin,
            HdmiVrrMaxHz = hdmiMax,
        };
    }

    private static void ParseCta(byte[] b, int off, ref bool freeSync, ref int? fsMin, ref int? fsMax,
        ref bool hdmiVrr, ref int? hdmiMin, ref int? hdmiMax)
    {
        int dtdStart = b[off + 2];
        if (dtdStart < 4 || dtdStart > 127) dtdStart = 127;
        int i = off + 4;
        int end = off + dtdStart;
        while (i < end)
        {
            byte header = b[i];
            int tagCode = (header >> 5) & 0x07;
            int length = header & 0x1F;
            if (length == 0) { i++; continue; }
            if (i + length >= off + 128) break;

            if (tagCode == 3 && length >= 3)
            {
                int oui = b[i + 1] | (b[i + 2] << 8) | (b[i + 3] << 16);
                if (oui == 0x00001A)
                {
                    freeSync = true;
                    // В блоке FreeSync v1/v2 диапазон обычно лежит в байтах 6 и 7 полезной нагрузки.
                    if (length >= 7)
                    {
                        int min = b[i + 6];
                        int max = b[i + 7];
                        if (min > 0 && max > min) { fsMin = min; fsMax = max; }
                    }
                }
                else if (oui == 0xC45DD8 && length >= 10)
                {
                    int vrrMin = b[i + 9] & 0x3F;
                    int vrrMax = ((b[i + 9] & 0xC0) << 2) | b[i + 10];
                    if (vrrMin > 0 || vrrMax > 0)
                    {
                        hdmiVrr = true;
                        if (vrrMin > 0) hdmiMin = vrrMin;
                        if (vrrMax > 0) hdmiMax = vrrMax;
                    }
                }
            }
            i += length + 1;
        }
    }

    private static bool ParseDisplayId(byte[] b, int off)
    {
        // Байт 0 — тег расширения 0x70, далее раздел DisplayID: версия, длина, тип, кол-во расширений, блоки данных.
        int sectionLength = b[off + 2];
        int i = off + 5;
        int end = Math.Min(off + 5 + sectionLength, off + 127);
        while (i + 3 <= end)
        {
            byte tag = b[i];
            int payload = b[i + 2];
            if (tag == 0 && payload == 0) break;
            if (tag == 0x2B) return true;
            i += 3 + payload;
        }
        return false;
    }

    private static int? TimingRefresh(byte[] b, int d)
    {
        int pixelClock10kHz = b[d] | (b[d + 1] << 8);
        if (pixelClock10kHz == 0) return null;
        int hActive = b[d + 2] | ((b[d + 4] & 0xF0) << 4);
        int hBlank = b[d + 3] | ((b[d + 4] & 0x0F) << 8);
        int vActive = b[d + 5] | ((b[d + 7] & 0xF0) << 4);
        int vBlank = b[d + 6] | ((b[d + 7] & 0x0F) << 8);
        long totalPixels = (long)(hActive + hBlank) * (vActive + vBlank);
        if (totalPixels <= 0) return null;
        double hz = pixelClock10kHz * 10_000.0 / totalPixels;
        return (int)Math.Round(hz);
    }

    private static string? ParseManufacturer(byte b0, byte b1)
    {
        int v = (b0 << 8) | b1;
        char c1 = (char)('A' + ((v >> 10) & 0x1F) - 1);
        char c2 = (char)('A' + ((v >> 5) & 0x1F) - 1);
        char c3 = (char)('A' + (v & 0x1F) - 1);
        string id = new(new[] { c1, c2, c3 });
        return id.All(c => c is >= 'A' and <= 'Z') ? id : null;
    }

    private static string? ReadText(byte[] b, int start, int max)
    {
        var chars = new List<char>(max);
        for (int i = start; i < start + max && i < b.Length; i++)
        {
            byte c = b[i];
            if (c == 0x0A || c == 0x00) break;
            chars.Add((char)c);
        }
        var s = new string(chars.ToArray()).Trim();
        return s.Length > 0 ? s : null;
    }
}
