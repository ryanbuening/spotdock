using SpotDock;
using Xunit;

namespace SpotDock.Tests;

public sealed class UpdateReleaseParserTests
{
    [Fact]
    public void Parse_SelectsExactSpotDockExe_IgnoresInstaller()
    {
        const string json = """
        {
          "tag_name": "v2.1.0",
          "assets": [
            { "name": "SpotDock-Setup.exe", "browser_download_url": "https://example.com/SpotDock-Setup.exe" },
            { "name": "SpotDock.exe", "browser_download_url": "https://example.com/SpotDock.exe" }
          ]
        }
        """;

        var result = UpdateReleaseParser.Parse(json, new Version(2, 0, 0));
        Assert.NotNull(result);
        Assert.Equal(new Version(2, 1, 0), result!.Value.Version);
        Assert.Equal("https://example.com/SpotDock.exe", result.Value.Url);
    }

    [Fact]
    public void Parse_ReturnsNullWhenNotNewerOrInvalidTagOrMissingExe()
    {
        const string sameVersion = """
        {
          "tag_name": "v2.0.0",
          "assets": [{ "name": "SpotDock.exe", "browser_download_url": "https://example.com/SpotDock.exe" }]
        }
        """;
        Assert.Null(UpdateReleaseParser.Parse(sameVersion, new Version(2, 0, 0)));

        const string badTag = """
        {
          "tag_name": "v2.1.0-beta1",
          "assets": [{ "name": "SpotDock.exe", "browser_download_url": "https://example.com/SpotDock.exe" }]
        }
        """;
        Assert.Null(UpdateReleaseParser.Parse(badTag, new Version(2, 0, 0)));

        const string installerOnly = """
        {
          "tag_name": "v2.1.0",
          "assets": [{ "name": "SpotDock-Setup.exe", "browser_download_url": "https://example.com/SpotDock-Setup.exe" }]
        }
        """;
        Assert.Null(UpdateReleaseParser.Parse(installerOnly, new Version(2, 0, 0)));
    }

    [Fact]
    public void NormalizeVersion_StripsZeroRevision_AndPadsMissingBuild()
    {
        Version fourPart = UpdateReleaseParser.NormalizeVersion(new Version(2, 0, 0, 0));
        Assert.Equal("2.0.0", fourPart.ToString());
        Assert.Equal(-1, fourPart.Revision);

        Version twoPart = UpdateReleaseParser.NormalizeVersion(new Version(2, 0));
        Assert.Equal("2.0.0", twoPart.ToString());
        Assert.Equal(new Version(2, 0, 0), twoPart);

        Version nonZeroRev = UpdateReleaseParser.NormalizeVersion(new Version(2, 0, 0, 1));
        Assert.Equal("2.0.0.1", nonZeroRev.ToString());
        Assert.Equal(new Version(2, 0, 0, 1), nonZeroRev);
    }

    [Fact]
    public void Parse_NormalizesFourPartCurrentAndTagVersions()
    {
        const string v201 = """
        {
          "tag_name": "v2.0.1",
          "assets": [{ "name": "SpotDock.exe", "browser_download_url": "https://example.com/SpotDock.exe" }]
        }
        """;
        var newer = UpdateReleaseParser.Parse(v201, new Version(2, 0, 0, 0));
        Assert.NotNull(newer);
        Assert.Equal(new Version(2, 0, 1), newer!.Value.Version);
        Assert.Equal("2.0.1", newer.Value.Version.ToString());

        const string v200 = """
        {
          "tag_name": "v2.0.0",
          "assets": [{ "name": "SpotDock.exe", "browser_download_url": "https://example.com/SpotDock.exe" }]
        }
        """;
        Assert.Null(UpdateReleaseParser.Parse(v200, new Version(2, 0, 0, 0)));

        const string v2000 = """
        {
          "tag_name": "v2.0.0.0",
          "assets": [{ "name": "SpotDock.exe", "browser_download_url": "https://example.com/SpotDock.exe" }]
        }
        """;
        Assert.Null(UpdateReleaseParser.Parse(v2000, new Version(2, 0, 0)));
    }

    [Fact]
    public void UiStrings_FormatsFourPartZeroRevisionAsThreePartVersion()
    {
        var fourPart = new Version(2, 0, 0, 0);

        Assert.Equal("SpotDock v2.0.0", UiStrings.MenuHeader(fourPart));
        Assert.Equal("You're on the latest version (v2.0.0).", UiStrings.UpdateLatest(fourPart));
        Assert.Equal("⬤ Update to v2.0.0", UiStrings.UpdateAvailable(fourPart));
        Assert.Equal(
            "Current version: v2.0.0\n\nAutomatic updates are turned off in this build.",
            UiStrings.UpdateNotConfigured(fourPart));
        Assert.Equal(
            "New version v2.0.1 available (current: v2.0.0).\n\nUpdate now? The widget restarts by itself.",
            UiStrings.UpdatePrompt(new Version(2, 0, 1, 0), fourPart));
    }
}
