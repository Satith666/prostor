using WinPulse.Core.Model;

namespace WinPulse.Core.Analysis;

public readonly record struct ProcessRate(double CpuPercent, double ReadBps, double WriteBps);

/// <summary>
/// Превращает абсолютные счётчики процессов в скорости: % процессора, байт/с чтения и записи.
/// Процесс узнаём по паре (PID, время создания), чтобы не спутать с новым процессом, получившим тот же PID.
/// </summary>
public sealed class ProcessRateCalculator
{
    private Dictionary<(int Pid, long Create), ProcessSample> _prev = new();
    private DateTime _prevTime;

    public Dictionary<int, ProcessRate> Compute(IReadOnlyList<ProcessSample> current, DateTime time, int logicalCores)
    {
        var result = new Dictionary<int, ProcessRate>(current.Count);
        double seconds = _prev.Count == 0 ? 0 : (time - _prevTime).TotalSeconds;
        int cores = Math.Max(1, logicalCores);
        var next = new Dictionary<(int Pid, long Create), ProcessSample>(current.Count);

        foreach (var p in current)
        {
            next[(p.Pid, p.CreateTime)] = p;
            if (seconds > 0.05 && _prev.TryGetValue((p.Pid, p.CreateTime), out var old))
            {
                double cpu = (p.CpuTime100ns - old.CpuTime100ns) / (seconds * 1e7 * cores) * 100.0;
                double read = Math.Max(0, p.ReadBytes - old.ReadBytes) / seconds;
                double write = Math.Max(0, p.WriteBytes - old.WriteBytes) / seconds;
                result[p.Pid] = new ProcessRate(Math.Clamp(cpu, 0, 100), read, write);
            }
            else
            {
                result[p.Pid] = default;
            }
        }

        _prev = next;
        _prevTime = time;
        return result;
    }
}
