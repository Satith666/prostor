using System.Runtime.InteropServices;

namespace WinPulse.Collectors.Interop;

/// <summary>Запрос PDH. Счётчики добавляются только по английским именам — работают в любой локализации Windows.</summary>
internal sealed class PdhQuery : IDisposable
{
    private IntPtr _query;

    public PdhQuery()
    {
        if (Native.PdhOpenQuery(null, IntPtr.Zero, out _query) != 0) _query = IntPtr.Zero;
    }

    public bool IsOpen => _query != IntPtr.Zero;

    public PdhCounter? Add(string path)
    {
        if (!IsOpen) return null;
        uint status = Native.PdhAddEnglishCounter(_query, path, IntPtr.Zero, out IntPtr counter);
        return status == 0 ? new PdhCounter(counter) : null;
    }

    public bool Collect() => IsOpen && Native.PdhCollectQueryData(_query) == 0;

    public void Dispose()
    {
        if (_query != IntPtr.Zero) Native.PdhCloseQuery(_query);
        _query = IntPtr.Zero;
    }
}

internal sealed unsafe class PdhCounter : IDisposable
{
    private const uint Format = Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100;
    private readonly IntPtr _handle;
    private IntPtr _buffer;
    private uint _bufferSize;

    public PdhCounter(IntPtr handle) => _handle = handle;

    /// <summary>Одно значение (счётчик без «*»). null — данных пока нет (первый замер) или ошибка.</summary>
    public double? Value()
    {
        uint status = Native.PdhGetFormattedCounterValue(_handle, Format, out _, out var v);
        if (status != 0 || v.CStatus > 1) return null;
        return double.IsFinite(v.DoubleValue) ? v.DoubleValue : null;
    }

    /// <summary>Все экземпляры счётчика с «*».</summary>
    public List<KeyValuePair<string, double>> Values()
    {
        var result = new List<KeyValuePair<string, double>>();
        uint size = 0;
        uint status = Native.PdhGetFormattedCounterArray(_handle, Format, ref size, out uint count, IntPtr.Zero);
        if (status != Native.PDH_MORE_DATA || size == 0) return result;

        if (size > _bufferSize)
        {
            if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
            _bufferSize = size + 4096;
            _buffer = Marshal.AllocHGlobal((int)_bufferSize);
        }

        size = _bufferSize;
        status = Native.PdhGetFormattedCounterArray(_handle, Format, ref size, out count, _buffer);
        if (status != 0) return result;

        result.Capacity = (int)count;
        byte* p = (byte*)_buffer;
        for (int i = 0; i < count; i++)
        {
            byte* item = p + i * 24;
            char* name = *(char**)item;
            uint cstatus = *(uint*)(item + 8);
            double value = *(double*)(item + 16);
            if (name == null || cstatus > 1 || !double.IsFinite(value)) continue;
            result.Add(new KeyValuePair<string, double>(new string(name), value));
        }
        return result;
    }

    public void Dispose()
    {
        if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
        _buffer = IntPtr.Zero;
        _bufferSize = 0;
    }
}
