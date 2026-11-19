using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using WinPulse.Core;
using WinPulse.Core.Diagnostics;

namespace WinPulse.Collectors.Diagnostics;

/// <summary>
/// Чтение журналов Windows напрямую через wevtapi.dll (без сторонних пакетов):
/// EvtQuery с XPath-фильтром → EvtNext → EvtRender в XML → разбор полей.
/// </summary>
public static class EventLogReader
{
    private const int EvtQueryChannelPath = 0x1;
    private const int EvtQueryReverseDirection = 0x200;
    private const int EvtQueryTolerateQueryErrors = 0x1000;
    private const int EvtRenderEventXml = 1;
    private const int ERROR_NO_MORE_ITEMS = 259;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_EVT_CHANNEL_NOT_FOUND = 15007;

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    [DllImport("wevtapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr EvtQuery(IntPtr session, string path, string query, int flags);

    [DllImport("wevtapi.dll", SetLastError = true)]
    private static extern bool EvtNext(IntPtr resultSet, int eventsSize, [Out] IntPtr[] events, int timeout, int flags, out int returned);

    [DllImport("wevtapi.dll", SetLastError = true)]
    private static extern bool EvtRender(IntPtr context, IntPtr fragment, int flags, int bufferSize, IntPtr buffer, out int bufferUsed, out int propertyCount);

    [DllImport("wevtapi.dll")]
    private static extern bool EvtClose(IntPtr handle);

    public sealed record Result(List<EventItem> Items, int Error);

    /// <summary>XPath-условие «за последние N дней».</summary>
    public static string LastDays(int days) =>
        "TimeCreated[timediff(@SystemTime) <= " + ((long)days * 24 * 3600 * 1000).ToString(CultureInfo.InvariantCulture) + "]";

    /// <summary>Читает события канала (новые первыми). Error — код Win32, если запрос не удался (5 — нет доступа).</summary>
    public static Result Query(string channel, string xpath, int max = 2000)
    {
        var items = new List<EventItem>();
        IntPtr query = EvtQuery(IntPtr.Zero, channel, xpath, EvtQueryChannelPath | EvtQueryReverseDirection | EvtQueryTolerateQueryErrors);
        if (query == IntPtr.Zero) return new Result(items, Marshal.GetLastWin32Error());
        try
        {
            var handles = new IntPtr[64];
            while (items.Count < max)
            {
                if (!EvtNext(query, handles.Length, handles, 3000, 0, out int returned))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error != ERROR_NO_MORE_ITEMS && items.Count == 0) return new Result(items, error);
                    break;
                }
                for (int i = 0; i < returned; i++)
                {
                    try
                    {
                        if (items.Count >= max) continue;
                        string? xml = Render(handles[i]);
                        var item = xml is null ? null : Parse(xml, channel);
                        if (item is not null) items.Add(item);
                    }
                    catch (Exception ex)
                    {
                        Diag.Report("event-parse", ex);
                    }
                    finally
                    {
                        EvtClose(handles[i]);
                        handles[i] = IntPtr.Zero;
                    }
                }
                if (returned == 0) break;
            }
        }
        finally
        {
            EvtClose(query);
        }
        return new Result(items, 0);
    }

    private static string? Render(IntPtr handle)
    {
        EvtRender(IntPtr.Zero, handle, EvtRenderEventXml, 0, IntPtr.Zero, out int used, out _);
        if (used <= 0) return null;
        IntPtr buffer = Marshal.AllocHGlobal(used);
        try
        {
            if (!EvtRender(IntPtr.Zero, handle, EvtRenderEventXml, used, buffer, out used, out _)) return null;
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>Разбор XML события. Открыт для тестов.</summary>
    public static EventItem? Parse(string xml, string log)
    {
        var root = XDocument.Parse(xml).Root;
        var system = root?.Element(Ns + "System");
        if (root is null || system is null) return null;

        string provider = system.Element(Ns + "Provider")?.Attribute("Name")?.Value ?? "";
        int.TryParse(system.Element(Ns + "EventID")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id);
        int.TryParse(system.Element(Ns + "Level")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int level);
        string? timeText = system.Element(Ns + "TimeCreated")?.Attribute("SystemTime")?.Value;
        DateTime time = DateTime.TryParse(timeText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var t)
            ? (t.Kind == DateTimeKind.Utc ? t.ToLocalTime() : t)
            : DateTime.MinValue;

        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var data = new List<string>();
        var eventData = root.Element(Ns + "EventData");
        if (eventData is not null)
        {
            foreach (var d in eventData.Elements(Ns + "Data"))
            {
                string value = d.Value;
                data.Add(value);
                string? name = d.Attribute("Name")?.Value;
                if (!string.IsNullOrEmpty(name)) named[name] = value;
            }
        }
        else
        {
            // Часть событий хранит поля в UserData: берём все «листья».
            var userData = root.Element(Ns + "UserData");
            if (userData is not null)
            {
                foreach (var leaf in userData.Descendants().Where(x => !x.HasElements))
                {
                    data.Add(leaf.Value);
                    named.TryAdd(leaf.Name.LocalName, leaf.Value);
                }
            }
        }

        return new EventItem
        {
            Log = log,
            Provider = provider,
            Id = id,
            Level = level == 0 ? 4 : level,
            Time = time,
            Named = named,
            Data = data,
        };
    }
}
