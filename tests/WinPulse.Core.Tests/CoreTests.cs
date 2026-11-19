using WinPulse.Core.Formatting;
using WinPulse.Core.Tweaks;

namespace WinPulse.Core.Tests;

public class RuFormatTests
{
    [Fact]
    public void Bytes_UsesRussianUnitsAndComma()
    {
        Assert.Equal("1,5 ГБ", Ru.Bytes(1.5 * 1024 * 1024 * 1024));
        Assert.Equal("512 МБ", Ru.Bytes(512.0 * 1024 * 1024));
        Assert.Equal("3,2 МБ/с", Ru.Rate(3.2 * 1024 * 1024));
    }

    [Theory]
    [InlineData(1, "процесс")]
    [InlineData(3, "процесса")]
    [InlineData(5, "процессов")]
    [InlineData(11, "процессов")]
    [InlineData(21, "процесс")]
    public void Plural_FollowsRussianRules(int n, string expected) =>
        Assert.Equal(expected, Ru.Plural(n, "процесс", "процесса", "процессов"));
}

public class KeyValueSettingsTests
{
    [Fact]
    public void Set_ChangesOnlyItsKey_AndKeepsOthers()
    {
        var result = KeyValueSettings.Set("VRROptimizeEnable=0;SwapEffectUpgradeEnable=0;", "SwapEffectUpgradeEnable", "1");
        Assert.Equal("VRROptimizeEnable=0;SwapEffectUpgradeEnable=1;", result);
    }

    [Fact]
    public void Set_AppendsMissingKey()
    {
        Assert.Equal("GpuPreference=2;", KeyValueSettings.Set(null, "GpuPreference", "2"));
        Assert.Equal("VRROptimizeEnable=1;SwapEffectUpgradeEnable=1;",
            KeyValueSettings.Set("VRROptimizeEnable=1;", "SwapEffectUpgradeEnable", "1"));
    }

    [Fact]
    public void Get_IsCaseInsensitive()
    {
        Assert.Equal("1", KeyValueSettings.Get("swapeffectupgradeenable=1;", "SwapEffectUpgradeEnable"));
        Assert.Null(KeyValueSettings.Get("A=1;", "B"));
    }
}

public class CompatLayersTests
{
    [Fact]
    public void Remove_KeepsOtherFlags()
    {
        Assert.Equal("~ HIGHDPIAWARE",
            CompatLayers.Remove("~ DISABLEDXMAXIMIZEDWINDOWEDMODE HIGHDPIAWARE", CompatLayers.DisableFullscreenOptimizations));
    }

    [Fact]
    public void Remove_ReturnsNull_WhenNothingMeaningfulLeft()
    {
        Assert.Null(CompatLayers.Remove("~ DISABLEDXMAXIMIZEDWINDOWEDMODE", CompatLayers.DisableFullscreenOptimizations));
    }

    [Fact]
    public void Has_FindsFlag()
    {
        Assert.True(CompatLayers.Has("~ DISABLEDXMAXIMIZEDWINDOWEDMODE", CompatLayers.DisableFullscreenOptimizations));
        Assert.False(CompatLayers.Has("~ HIGHDPIAWARE", CompatLayers.DisableFullscreenOptimizations));
    }
}
