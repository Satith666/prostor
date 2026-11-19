using System.Text.Json;
using WinPulse.Core.Updates;

namespace WinPulse.Core.Tests;

public class UpdateTests
{
    [Theory]
    [InlineData("winpulse-v0.9.0", "0.9.0")]
    [InlineData("winpulse-v1.2", "1.2.0")]
    [InlineData("0.9.0-dev.42+abc", "0.9.0")]
    [InlineData("v2.0.1", "2.0.1")]
    public void ParseVersion(string text, string expected) => Assert.Equal(Version.Parse(expected), ReleaseFeed.ParseVersion(text));

    [Theory]
    [InlineData("prostor-v1.0")]
    [InlineData("")]
    [InlineData("winpulse-vX")]
    public void ParseVersion_Invalid(string text) => Assert.Null(ReleaseFeed.ParseVersion(text));

    private const string Feed = """
        [
          { "tag_name": "v3.0.0", "draft": false, "prerelease": false, "name": "Prostor 3", "body": "", "html_url": "https://x/prostor", "assets": [] },
          { "tag_name": "winpulse-v1.1.0", "draft": true, "prerelease": false, "assets": [] },
          { "tag_name": "winpulse-v1.0.0-rc", "draft": false, "prerelease": true, "assets": [] },
          { "tag_name": "winpulse-v0.9.1", "draft": false, "prerelease": false, "name": "WinPulse 0.9.1", "body": "fixes", "html_url": "https://x/091",
            "assets": [
              { "name": "WinPulse-Setup-0.9.1.exe", "browser_download_url": "https://x/dl/WinPulse-Setup-0.9.1.exe", "digest": "sha256:ABCDEF" },
              { "name": "WinPulse-0.9.1-portable.zip", "browser_download_url": "https://x/dl/zip" }
            ] },
          { "tag_name": "winpulse-v0.9.0", "draft": false, "prerelease": false, "assets": [] }
        ]
        """;

    [Fact]
    public void Pick_NewestWinPulseRelease()
    {
        using var doc = JsonDocument.Parse(Feed);
        var u = ReleaseFeed.Pick(doc.RootElement, new Version(0, 9, 0));
        Assert.NotNull(u);
        Assert.Equal(new Version(0, 9, 1), u!.Version);
        Assert.Equal("WinPulse-Setup-0.9.1.exe", u.SetupName);
        Assert.Equal("https://x/dl/WinPulse-Setup-0.9.1.exe", u.SetupUrl);
        Assert.Equal("abcdef", u.Sha256);
        Assert.Equal("https://x/091", u.PageUrl);
    }

    [Fact]
    public void Pick_NothingNewer()
    {
        using var doc = JsonDocument.Parse(Feed);
        Assert.Null(ReleaseFeed.Pick(doc.RootElement, new Version(0, 9, 1)));
        Assert.Null(ReleaseFeed.Pick(doc.RootElement, new Version(2, 0, 0)));
    }

    [Fact]
    public void ShaFromSums()
    {
        string hash = new('a', 64);
        string sums = $"{new string('b', 64)}  WinPulse-0.9.1-portable.zip\r\n{hash}  WinPulse-Setup-0.9.1.exe\r\n";
        Assert.Equal(hash, ReleaseFeed.ShaFromSums(sums, "WinPulse-Setup-0.9.1.exe"));
        Assert.Null(ReleaseFeed.ShaFromSums(sums, "other.exe"));
    }
}
