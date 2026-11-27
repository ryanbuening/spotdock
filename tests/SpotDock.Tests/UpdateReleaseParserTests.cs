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
}
