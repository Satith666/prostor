using WinPulse.Core.Gaming;

namespace WinPulse.Core.Demo;

/// <summary>Пример записанной игровой сессии для демо-режима: показывает, как выглядит разбор.</summary>
public static class DemoSession
{
    public static GameSessionRecord Create()
    {
        var start = DateTime.UtcNow.AddHours(-3);
        var record = new GameSessionRecord
        {
            Id = "demo0000000000000000000000000000",
            Game = "Cyberpunk 2077",
            ExePath = @"D:\SteamLibrary\steamapps\common\Cyberpunk 2077\bin\x64\Cyberpunk2077.exe",
            StartUtc = start,
            EndUtc = start.AddMinutes(42),
            RamTotal = 16L << 30,
            VramTotal = 8L << 30,
            GpuName = "NVIDIA GeForce RTX 3050",
            CpuName = "AMD Ryzen 5 5600X 6-Core Processor",
        };

        var rnd = new Random(7);
        for (int t = 0; t < 42 * 60; t++)
        {
            bool update = t is >= 1510 and < 1580;      // обновление Windows в середине игры
            bool chromeVideo = t is >= 600 and < 1200;  // видео в браузере на втором мониторе
            double fps = 74 + 6 * Math.Sin(t / 37.0) + rnd.NextDouble() * 4 - (update ? 26 : 0);
            var others = new List<SessionApp>
            {
                new() { Name = "Wallpaper Engine", Cpu = 2.5 + rnd.NextDouble(), Gpu = 11 + rnd.NextDouble() * 3, Memory = 240L << 20 },
                new() { Name = "Discord", Cpu = 1.2 + rnd.NextDouble() * 0.6, Gpu = 1.5, Memory = 480L << 20 },
            };
            if (chromeVideo) others.Add(new SessionApp { Name = "Google Chrome", Cpu = 6 + rnd.NextDouble() * 2, Gpu = 4, Memory = 1900L << 20 });
            if (update) others.Add(new SessionApp { Name = "Установщик модулей Windows", Cpu = 24 + rnd.NextDouble() * 6, Gpu = 0, Io = 45_000_000 });

            record.Samples.Add(new SessionSample
            {
                T = t,
                Cpu = 52 + rnd.NextDouble() * 8 + (update ? 25 : 0) + (chromeVideo ? 6 : 0),
                Gpu = 96 + rnd.NextDouble() * 3,
                MemPercent = 71 + (chromeVideo ? 9 : 0) + rnd.NextDouble() * 2,
                VramUsed = (long)((6.9 + rnd.NextDouble() * 0.3) * (1L << 30)),
                DiskActive = update ? 92 + rnd.NextDouble() * 8 : 3 + rnd.NextDouble() * 5,
                HardFaults = 20 + rnd.NextDouble() * 30,
                GameCpu = 38 + rnd.NextDouble() * 6,
                GameGpu = 82 + rnd.NextDouble() * 4,
                GameMemory = 5500L << 20,
                Fps = Math.Round(fps, 1),
                CpuTemp = Math.Round(64 + 5 * Math.Sin(t / 90.0) + rnd.NextDouble() * 2, 1),
                // К середине игры видеокарта прогревается до 86–88 °C и сбрасывает частоту.
                GpuTemp = Math.Round(70 + Math.Min(17, t / 60.0) + rnd.NextDouble() * 1.5, 1),
                GpuClock = Math.Round(t > 1000 ? 1740 + rnd.NextDouble() * 20 : 1860 + rnd.NextDouble() * 20),
                Others = others,
            });
        }
        return record;
    }
}
