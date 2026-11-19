using WinPulse.Collectors.Hardware;

namespace WinPulse.Core.Tests;

public class HardwareTests
{
    [Theory]
    [InlineData("CMK16GX4M2B3200C16", 3200)]
    [InlineData("F4-3600C16-8GVKC", 3600)]
    [InlineData("KF432C16BB/16", 3200)]
    [InlineData("KF426C16BB/8", 2666)]
    [InlineData("KF552C40BB-16", 5200)]
    [InlineData("M378A1K43DB2-CTD", null)]
    [InlineData("", null)]
    public void SpeedFromPartNumber(string part, int? expected) => Assert.Equal(expected, HardwareReader.SpeedFromPartNumber(part));

    [Theory]
    [InlineData("DIMM_A2", "BANK 0", "A")]
    [InlineData("ChannelB-DIMM0", "BANK 2", "B")]
    [InlineData("Controller0-ChannelA-DIMM1", "", "A")]
    [InlineData("DIMM 1", "P0 CHANNEL A", "A")]
    [InlineData("B1", "", "B")]
    [InlineData("DIMM 1", "BANK 0", null)]
    public void ChannelOf(string slot, string bank, string? expected) => Assert.Equal(expected, HardwareReader.ChannelOf(slot, bank));

    [Fact]
    public void SteamLibraries_ParsesVdf()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0"
                {
                    "path"		"C:\\Program Files (x86)\\Steam"
                    "label"		""
                }
                "1"
                {
                    "path"		"D:\\SteamLibrary"
                }
            }
            """;
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], HardwareReader.SteamLibraries(vdf));
    }
}
