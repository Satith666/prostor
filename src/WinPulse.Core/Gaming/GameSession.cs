using WinPulse.Core.Analysis;

namespace WinPulse.Core.Gaming;

/// <summary>Сторонняя программа в секунду записи.</summary>
public sealed class SessionApp
{
    public string Name { get; set; } = "";
    public double Cpu { get; set; }
    public double Gpu { get; set; }
    public long Memory { get; set; }
    /// <summary>Чтение и запись диска, байт/с.</summary>
    public double Io { get; set; }
}

/// <summary>Одна секунда игровой сессии.</summary>
public sealed class SessionSample
{
    /// <summary>Секунды от начала сессии.</summary>
    public double T { get; set; }
    public double Cpu { get; set; }
    public double Gpu { get; set; }
    public double MemPercent { get; set; }
    public long VramUsed { get; set; }
    public double DiskActive { get; set; }
    public double? HardFaults { get; set; }
    public double GameCpu { get; set; }
    public double GameGpu { get; set; }
    public long GameMemory { get; set; }
    public double GameIo { get; set; }
    /// <summary>FPS за эту секунду, если в это время шёл замер.</summary>
    public double? Fps { get; set; }
    /// <summary>Самые заметные сторонние программы в эту секунду.</summary>
    public List<SessionApp> Others { get; set; } = new();
}

/// <summary>Запись одной игровой сессии (сохраняется в JSON).</summary>
public sealed class GameSessionRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Game { get; set; } = "";
    public string? ExePath { get; set; }
    public DateTime StartUtc { get; set; }
    public DateTime EndUtc { get; set; }
    public long RamTotal { get; set; }
    public long VramTotal { get; set; }
    public string GpuName { get; set; } = "";
    public string CpuName { get; set; } = "";
    public List<SessionSample> Samples { get; set; } = new();

    public TimeSpan Duration => EndUtc - StartUtc;
}

/// <summary>
/// Следит за играми по кадрам мониторинга: сам замечает начало игры, пишет каждую секунду и закрывает сессию,
/// когда игра закрыта или свёрнута надолго. Вызывается из фонового потока раз в секунду.
/// </summary>
public sealed class GameSessionTracker
{
    private string? _candidateKey;
    private int _candidateSeconds;
    private string? _gameKey;
    private int _missingSeconds;
    private int _idleSeconds;

    /// <summary>Сколько секунд приложение должно быть «похоже на игру», чтобы начать запись.</summary>
    public int StartAfterSeconds { get; init; } = 20;
    /// <summary>Сколько секунд игра может быть свёрнута без нагрузки, прежде чем сессия закроется.</summary>
    public int EndAfterIdleSeconds { get; init; } = 90;
    /// <summary>Короче этого сессия не сохраняется.</summary>
    public int MinimumSeconds { get; init; } = 60;
    /// <summary>Не больше стольких секунд в одной записи (≈ 6 часов), чтобы файл не рос бесконечно.</summary>
    public int MaximumSamples { get; init; } = 6 * 3600;

    /// <summary>exe, который пользователь указал как игру (например, в «Играх в окне»).</summary>
    public Func<string?, bool>? IsPinned { get; set; }

    public GameSessionRecord? Current { get; private set; }
    public event Action<GameSessionRecord>? Started;
    public event Action<GameSessionRecord>? Finished;

    public void Feed(MonitorFrame frame, int foregroundPid, double? fps)
    {
        var apps = frame.Apps;
        var foreground = foregroundPid > 0 ? apps.FirstOrDefault(a => a.Processes.Any(p => p.Pid == foregroundPid)) : null;

        if (Current is null)
        {
            var candidate = foreground is not null && GameDetector.IsCandidate(foreground, true, IsPinned?.Invoke(foreground.Path) == true)
                ? foreground
                : apps.FirstOrDefault(a => IsPinned?.Invoke(a.Path) == true && GameDetector.IsCandidate(a, false, true) && a.GpuPercent >= 5);
            if (candidate is null)
            {
                _candidateKey = null;
                _candidateSeconds = 0;
                return;
            }
            if (candidate.Key != _candidateKey)
            {
                _candidateKey = candidate.Key;
                _candidateSeconds = 0;
            }
            if (++_candidateSeconds < StartAfterSeconds) return;

            Current = new GameSessionRecord
            {
                Game = candidate.DisplayName,
                ExePath = candidate.Path,
                StartUtc = frame.Raw.Timestamp.ToUniversalTime().AddSeconds(-_candidateSeconds),
                RamTotal = frame.Raw.Memory.TotalPhysical,
                VramTotal = frame.Raw.Gpus.Count == 0 ? 0 : frame.Raw.Gpus.Max(g => g.DedicatedTotal),
                GpuName = frame.Raw.Gpus.OrderByDescending(g => g.DedicatedTotal).FirstOrDefault()?.Name ?? "",
                CpuName = frame.Info.CpuName,
            };
            _gameKey = candidate.Key;
            _missingSeconds = 0;
            _idleSeconds = 0;
            Started?.Invoke(Current);
        }

        var game = apps.FirstOrDefault(a => a.Key == _gameKey);
        if (game is null)
        {
            if (++_missingSeconds >= 5) Finish(frame.Raw.Timestamp);
            return;
        }
        _missingSeconds = 0;

        bool inFocus = foreground is not null && foreground.Key == _gameKey;
        _idleSeconds = !inFocus && game.GpuPercent < 5 ? _idleSeconds + 1 : 0;
        if (_idleSeconds >= EndAfterIdleSeconds)
        {
            Finish(frame.Raw.Timestamp);
            return;
        }

        Current!.Samples.Add(Sample(frame, game, fps));
        Current.EndUtc = frame.Raw.Timestamp.ToUniversalTime();
        if (Current.Samples.Count >= MaximumSamples) Finish(frame.Raw.Timestamp);
    }

    /// <summary>Закрыть текущую сессию (например, при выходе из программы).</summary>
    public void Flush(DateTime now)
    {
        if (Current is not null) Finish(now);
    }

    private void Finish(DateTime now)
    {
        var record = Current;
        Current = null;
        _gameKey = null;
        _candidateKey = null;
        _candidateSeconds = 0;
        if (record is null) return;
        if (record.Samples.Count > 0 && record.EndUtc == default) record.EndUtc = now.ToUniversalTime();
        if (record.Samples.Count >= MinimumSeconds) Finished?.Invoke(record);
    }

    private SessionSample Sample(MonitorFrame f, AppGroup game, double? fps)
    {
        var raw = f.Raw;
        double start = (raw.Timestamp.ToUniversalTime() - Current!.StartUtc).TotalSeconds;
        var others = f.Apps
            .Where(a => a.Key != game.Key)
            .OrderByDescending(a => a.CpuPercent + a.GpuPercent * 1.5)
            .Take(6)
            .ToList();
        var topIo = f.Apps.Where(a => a.Key != game.Key).OrderByDescending(a => a.IoBps).FirstOrDefault();
        if (topIo is not null && topIo.IoBps > 1_000_000 && !others.Contains(topIo)) others.Add(topIo);

        return new SessionSample
        {
            T = Math.Round(start, 1),
            Cpu = Math.Round(raw.Cpu.UtilityPercent, 1),
            Gpu = Math.Round(raw.Gpus.Count == 0 ? 0 : raw.Gpus.Max(g => g.UtilPercent), 1),
            MemPercent = Math.Round(raw.Memory.UsedPercent, 1),
            VramUsed = raw.Gpus.Count == 0 ? 0 : raw.Gpus.Max(g => g.DedicatedUsed),
            DiskActive = Math.Round(raw.Disks.Count == 0 ? 0 : raw.Disks.Max(d => d.ActivePercent), 1),
            HardFaults = raw.Memory.HardFaultsPerSec,
            GameCpu = Math.Round(game.CpuPercent, 1),
            GameGpu = Math.Round(game.GpuPercent, 1),
            GameMemory = game.PrivateMemory,
            GameIo = Math.Round(game.IoBps),
            Fps = fps is double v ? Math.Round(v, 1) : null,
            Others = others.Select(o => new SessionApp
            {
                Name = o.DisplayName,
                Cpu = Math.Round(o.CpuPercent, 1),
                Gpu = Math.Round(o.GpuPercent, 1),
                Memory = o.PrivateMemory,
                Io = Math.Round(o.IoBps),
            }).ToList(),
        };
    }
}
