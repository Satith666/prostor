using WinPulse.Core.Gaming;

namespace WinPulse.Core.Demo;

/// <summary>Примеры игр для демонстрационного режима страницы «Апскейлинг».</summary>
public static class DemoUpscalers
{
    public const string GpuName = "NVIDIA GeForce RTX 3050";

    public static List<GameUpscalers> Create() =>
    [
        new()
        {
            Game = "Cyberpunk 2077",
            Path = @"D:\SteamLibrary\steamapps\common\Cyberpunk 2077",
            Dlls =
            [
                new(UpscalerTech.Dlss, @"bin\x64\nvngx_dlss.dll", "3.5.10.0"),
                new(UpscalerTech.DlssFrameGen, @"bin\x64\nvngx_dlssg.dll", "3.5.10.0"),
                new(UpscalerTech.DlssRayReconstruction, @"bin\x64\nvngx_dlssd.dll", "3.5.10.0"),
                new(UpscalerTech.Xess, @"bin\x64\libxess.dll", "1.3.0.0"),
                new(UpscalerTech.Fsr, @"bin\x64\amd_fidelityfx_dx12.dll", "1.1.2.0"),
                new(UpscalerTech.Streamline, @"bin\x64\sl.interposer.dll", "2.4.0.0"),
            ],
        },
        new()
        {
            Game = "Elden Ring",
            Path = @"D:\SteamLibrary\steamapps\common\ELDEN RING",
            Dlls = [],
            AntiCheat = "Easy Anti-Cheat",
        },
        new()
        {
            Game = "Starfield",
            Path = @"C:\Program Files (x86)\Steam\steamapps\common\Starfield",
            Dlls =
            [
                new(UpscalerTech.Fsr, @"ffx_fsr2_api_x64.dll", null),
                new(UpscalerTech.Xess, @"libxess.dll", "1.2.0.0"),
            ],
        },
        new()
        {
            Game = "Fortnite",
            Path = @"C:\Program Files\Epic Games\Fortnite",
            Dlls =
            [
                new(UpscalerTech.Dlss, @"FortniteGame\Binaries\Win64\nvngx_dlss.dll", "3.7.10.0"),
            ],
            AntiCheat = "Easy Anti-Cheat",
        },
    ];
}
