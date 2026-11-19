using System.Globalization;

namespace WinPulse.Core.Gaming;

/// <summary>
/// Разбор CSV консольного PresentMon построчно (файл дописывается во время замера, поэтому читаем «хвост»).
/// Колонки ищутся по заголовку: имена отличаются между версиями 1.x и 2.x.
/// </summary>
public sealed class PresentMonCsv
{
    private static readonly string[] FrameTimeNames = ["MsBetweenPresents", "MsBetweenAppStart", "msBetweenPresents"];
    private static readonly string[] GpuBusyNames = ["MsGPUBusy", "MsGPUActive", "msGPUActive", "GPUBusy"];
    private static readonly string[] CpuBusyNames = ["MsCPUBusy", "CPUBusy"];
    private static readonly string[] CpuWaitNames = ["MsCPUWait", "CPUWait"];

    private int _app = -1, _pid = -1, _frame = -1, _gpuBusy = -1, _sync = -1, _cpuBusy = -1, _cpuWait = -1;
    private int _columns;

    public bool HasHeader { get; private set; }

    /// <summary>Разбирает строку. Возвращает кадр или null (заголовок, пустая или битая строка).</summary>
    public FrameSample? Parse(string line, out string application, out int processId)
    {
        application = "";
        processId = 0;
        if (string.IsNullOrWhiteSpace(line)) return null;

        var cells = line.Split(',');
        if (!HasHeader || cells.Contains("Application"))
        {
            if (Array.IndexOf(cells, "Application") >= 0) ReadHeader(cells);
            return null;
        }
        if (cells.Length < _columns) return null; // строка ещё не дописана

        application = _app >= 0 ? cells[_app] : "";
        if (_pid >= 0) int.TryParse(cells[_pid], NumberStyles.Integer, CultureInfo.InvariantCulture, out processId);

        double? frame = _frame >= 0 ? Number(cells[_frame]) : null;
        if (frame is null && _cpuBusy >= 0 && _cpuWait >= 0 && Number(cells[_cpuBusy]) is double busy && Number(cells[_cpuWait]) is double wait)
            frame = busy + wait;
        if (frame is not double ft || ft <= 0) return null;

        double? gpu = _gpuBusy >= 0 ? Number(cells[_gpuBusy]) : null;
        int? sync = _sync >= 0 && int.TryParse(cells[_sync], NumberStyles.Integer, CultureInfo.InvariantCulture, out int s) ? s : null;
        return new FrameSample(ft, gpu, sync);
    }

    private void ReadHeader(string[] cells)
    {
        _columns = cells.Length;
        _app = Array.IndexOf(cells, "Application");
        _pid = Array.IndexOf(cells, "ProcessID");
        _frame = Find(cells, FrameTimeNames);
        _gpuBusy = Find(cells, GpuBusyNames);
        _cpuBusy = Find(cells, CpuBusyNames);
        _cpuWait = Find(cells, CpuWaitNames);
        _sync = Array.IndexOf(cells, "SyncInterval");
        HasHeader = _frame >= 0 || (_cpuBusy >= 0 && _cpuWait >= 0);
    }

    private static int Find(string[] cells, string[] names)
    {
        foreach (var name in names)
        {
            int i = Array.IndexOf(cells, name);
            if (i >= 0) return i;
        }
        return -1;
    }

    private static double? Number(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && !double.IsNaN(v) ? v : null;
}
