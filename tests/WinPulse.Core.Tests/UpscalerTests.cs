using WinPulse.Collectors.Gaming;
using WinPulse.Core.Gaming;

namespace WinPulse.Core.Tests;

public class UpscalerTests
{
    [Theory]
    [InlineData("nvngx_dlss.dll", UpscalerTech.Dlss)]
    [InlineData("NVNGX_DLSS.DLL", UpscalerTech.Dlss)]
    [InlineData("nvngx_dlssg.dll", UpscalerTech.DlssFrameGen)]
    [InlineData("nvngx_dlssd.dll", UpscalerTech.DlssRayReconstruction)]
    [InlineData("libxess.dll", UpscalerTech.Xess)]
    [InlineData("ffx_fsr2_api_x64.dll", UpscalerTech.Fsr)]
    [InlineData("amd_fidelityfx_dx12.dll", UpscalerTech.Fsr)]
    [InlineData("sl.interposer.dll", UpscalerTech.Streamline)]
    public void Classify_KnownFiles(string file, UpscalerTech tech) => Assert.Equal(tech, UpscalerCatalog.Classify(file));

    [Theory]
    [InlineData("nvngx.dll")]
    [InlineData("d3d12.dll")]
    [InlineData("game.exe")]
    public void Classify_UnknownFiles(string file) => Assert.Null(UpscalerCatalog.Classify(file));

    [Theory]
    [InlineData("2.5.1.0", true)]
    [InlineData("3.5.10.0", true)]
    [InlineData("3.7.10.0", false)]
    [InlineData("3.8.1.0", false)]
    [InlineData("310.2.1.0", false)]
    [InlineData("1,0,13,0", true)]
    [InlineData(null, false)]
    [InlineData("abc", false)]
    public void IsOldDlss(string? version, bool old) => Assert.Equal(old, UpscalerCatalog.IsOldDlss(version));

    [Fact]
    public void ParseVersion_ComparesNumerically()
    {
        Assert.True(UpscalerCatalog.ParseVersion("3.10.0.0") > UpscalerCatalog.ParseVersion("3.7.10.0"));
        Assert.True(UpscalerCatalog.ParseVersion("310.1.0.0") > UpscalerCatalog.ParseVersion("3.8.1.0"));
        Assert.Null(UpscalerCatalog.ParseVersion(null));
        Assert.Null(UpscalerCatalog.ParseVersion("x.y"));
    }

    [Theory]
    [InlineData(@"EasyAntiCheat\EasyAntiCheat_EOS_Setup.exe", "Easy Anti-Cheat")]
    [InlineData(@"BattlEye\BEService_x64.exe", "BattlEye")]
    [InlineData(@"bin\x64\nvngx_dlss.dll", null)]
    public void AntiCheatOf(string path, string? expected) => Assert.Equal(expected, UpscalerCatalog.AntiCheatOf(path));

    [Fact]
    public void Abilities_ByName()
    {
        var rtx3050 = UpscalerCatalog.Abilities("NVIDIA GeForce RTX 3050");
        Assert.True(rtx3050.IsRtx);
        Assert.False(rtx3050.HasDlssFrameGen);
        Assert.Equal("RTX 3050", rtx3050.Name);

        Assert.True(UpscalerCatalog.Abilities("NVIDIA GeForce RTX 4070 SUPER").HasDlssFrameGen);
        Assert.True(UpscalerCatalog.Abilities("NVIDIA GeForce RTX 5060 Ti").HasDlssFrameGen);
        Assert.False(UpscalerCatalog.Abilities("AMD Radeon RX 7800 XT").IsRtx);
        Assert.False(UpscalerCatalog.Abilities("NVIDIA GeForce GTX 1660 SUPER").IsRtx);
    }

    [Fact]
    public void Advice_OldDlssOnRtx3050_SuggestsUpdateAndFsrFrameGen()
    {
        Lang.English = false;
        var game = new GameUpscalers
        {
            Game = "Test",
            Dlls =
            [
                new(UpscalerTech.Dlss, "nvngx_dlss.dll", "3.5.10.0"),
                new(UpscalerTech.DlssFrameGen, "nvngx_dlssg.dll", "3.5.10.0"),
            ],
        };
        var advice = UpscalerCatalog.Advice(game, UpscalerCatalog.Abilities("NVIDIA GeForce RTX 3050"));
        Assert.Contains(advice, a => a.Contains("Включите DLSS"));
        Assert.Contains(advice, a => a.Contains("устаревшая"));
        Assert.Contains(advice, a => a.Contains("OptiScaler") && a.Contains("RTX 40"));
    }

    [Fact]
    public void Advice_AntiCheat_NeverSuggestsSwapping()
    {
        Lang.English = false;
        var game = new GameUpscalers
        {
            Game = "Online",
            AntiCheat = "Easy Anti-Cheat",
            Dlls =
            [
                new(UpscalerTech.Dlss, "nvngx_dlss.dll", "2.5.1.0"),
                new(UpscalerTech.DlssFrameGen, "nvngx_dlssg.dll", "3.5.0.0"),
            ],
        };
        var advice = UpscalerCatalog.Advice(game, UpscalerCatalog.Abilities("NVIDIA GeForce RTX 3050"));
        Assert.Contains(advice, a => a.Contains("бан"));
        Assert.DoesNotContain(advice, a => a.Contains("DLSS Swapper") || a.Contains("OptiScaler может"));
    }

    [Fact]
    public void Advice_FsrOnlyOnRtx_MentionsOptiScaler()
    {
        Lang.English = false;
        var game = new GameUpscalers { Game = "Fsr", Dlls = [new(UpscalerTech.Fsr, "ffx_fsr2_api_x64.dll", null)] };
        var advice = UpscalerCatalog.Advice(game, UpscalerCatalog.Abilities("NVIDIA GeForce RTX 3050"));
        Assert.Contains(advice, a => a.Contains("OptiScaler"));
        var amd = UpscalerCatalog.Advice(game, UpscalerCatalog.Abilities("AMD Radeon RX 6600"));
        Assert.Contains(amd, a => a.Contains("Включите FSR"));
    }

    [Fact]
    public void ScanFolder_FindsDllsAndAntiCheat()
    {
        string root = Path.Combine(Path.GetTempPath(), "wp-upscale-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "bin", "x64"));
            Directory.CreateDirectory(Path.Combine(root, "EasyAntiCheat"));
            File.WriteAllBytes(Path.Combine(root, "bin", "x64", "nvngx_dlss.dll"), [0x4D, 0x5A]);
            File.WriteAllBytes(Path.Combine(root, "bin", "x64", "libxess.dll"), [0x4D, 0x5A]);
            File.WriteAllBytes(Path.Combine(root, "bin", "x64", "other.dll"), [0x4D, 0x5A]);
            File.WriteAllBytes(Path.Combine(root, "EasyAntiCheat", "EasyAntiCheat_EOS_Setup.exe"), [0x4D, 0x5A]);

            var result = UpscalerScanner.ScanFolder("Test", root);

            Assert.Equal(2, result.Dlls.Count);
            Assert.True(result.Has(UpscalerTech.Dlss));
            Assert.True(result.Has(UpscalerTech.Xess));
            Assert.Equal("Easy Anti-Cheat", result.AntiCheat);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }
}
