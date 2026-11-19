using LibreHardwareMonitor.Hardware;
using WinPulse.Core;
using WinPulse.Core.Model;

namespace WinPulse.Collectors.Sensors;

/// <summary>
/// Датчики через LibreHardwareMonitor (github.com/LibreHardwareMonitor, MPL-2.0).
/// Видеокарты NVIDIA и AMD читаются без драйверов; датчики процессора — только если установлен PawnIO
/// (замена WinRing0, которую блокирует Защитник). PawnIO сами не ставим: это драйвер ядра, решает пользователь.
/// </summary>
public sealed class SensorReader : IDisposable
{
    private Computer? _computer;
    private Task? _opening;
    private bool _failed;
    private readonly object _lock = new();

    /// <summary>Свежие значения. Первые 1–3 секунды (пока библиотека открывается) — null.</summary>
    public SensorSnapshot? Read()
    {
        lock (_lock)
        {
            if (_failed) return null;
            if (_computer is null)
            {
                _opening ??= Task.Run(Open);
                return null;
            }
        }

        try
        {
            double? cpuTemp = null, cpuPower = null, gpuTemp = null, hotspot = null, gpuPower = null, gpuClock = null, fan = null;
            bool cpuFound = false;
            foreach (var hw in _computer.Hardware)
            {
                hw.Update();
                foreach (var sub in hw.SubHardware) sub.Update();
                var sensors = hw.Sensors.Concat(hw.SubHardware.SelectMany(s => s.Sensors)).Where(s => s.Value is float v && !float.IsNaN(v)).ToList();

                if (hw.HardwareType == HardwareType.Cpu)
                {
                    cpuFound = true;
                    var temps = sensors.Where(s => s.SensorType == SensorType.Temperature).ToList();
                    var main = temps.FirstOrDefault(s => Has(s.Name, "Package") || Has(s.Name, "Tctl") || Has(s.Name, "Tdie") || Has(s.Name, "Core Max"));
                    cpuTemp = Valid(main?.Value ?? temps.Select(s => s.Value).Max(), 5, 125);
                    var power = sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && Has(s.Name, "Package"));
                    cpuPower = Valid(power?.Value, 1, 500);
                }
                else if (hw.HardwareType is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel)
                {
                    // Берём первую мощную видеокарту (у NVIDIA/AMD — обычно единственную).
                    if (gpuTemp is not null && hw.HardwareType == HardwareType.GpuIntel) continue;
                    var temps = sensors.Where(s => s.SensorType == SensorType.Temperature).ToList();
                    gpuTemp = Valid((temps.FirstOrDefault(s => Has(s.Name, "GPU Core")) ?? temps.FirstOrDefault())?.Value, 5, 125);
                    hotspot = Valid(temps.FirstOrDefault(s => Has(s.Name, "Hot Spot") || Has(s.Name, "Hotspot"))?.Value, 5, 130);
                    var powers = sensors.Where(s => s.SensorType == SensorType.Power).ToList();
                    gpuPower = Valid((powers.FirstOrDefault(s => Has(s.Name, "Package") || Has(s.Name, "Board") || Has(s.Name, "Total")) ?? powers.FirstOrDefault())?.Value, 1, 1000);
                    gpuClock = Valid(sensors.FirstOrDefault(s => s.SensorType == SensorType.Clock && Has(s.Name, "Core"))?.Value, 50, 4000);
                    fan = Valid(sensors.FirstOrDefault(s => s.SensorType == SensorType.Control && Has(s.Name, "Fan"))?.Value, 0, 100);
                }
            }

            return new SensorSnapshot
            {
                CpuTemp = cpuTemp,
                CpuPower = cpuPower,
                GpuTemp = gpuTemp,
                GpuHotspot = hotspot,
                GpuPower = gpuPower,
                GpuClockMHz = gpuClock,
                GpuFanPercent = fan,
                CpuNeedsDriver = cpuFound && cpuTemp is null,
            };
        }
        catch (Exception ex)
        {
            Diag.Report("sensors-read", ex);
            return null;
        }
    }

    private void Open()
    {
        try
        {
            var computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = false,
                IsMotherboardEnabled = false,
                IsStorageEnabled = false,
                IsNetworkEnabled = false,
                IsControllerEnabled = false,
            };
            computer.Open();
            lock (_lock) _computer = computer;
        }
        catch (Exception ex)
        {
            Diag.Report("sensors-open", ex);
            lock (_lock) _failed = true;
        }
    }

    private static bool Has(string name, string part) => name.Contains(part, StringComparison.OrdinalIgnoreCase);

    private static double? Valid(float? value, double min, double max) =>
        value is float v && !float.IsNaN(v) && v >= min && v <= max ? Math.Round(v, 1) : null;

    public void Dispose()
    {
        lock (_lock)
        {
            try
            {
                _computer?.Close();
            }
            catch
            {
                // Закрываемся при выходе — ошибки неважны.
            }
            _computer = null;
        }
    }
}
