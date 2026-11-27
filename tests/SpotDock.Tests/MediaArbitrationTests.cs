using SpotDock;
using Xunit;

namespace SpotDock.Tests;

public sealed class MediaArbitrationTests
{
    private static TrackInfo Track(string title, bool playing) =>
        new(title, "Artist", playing, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(180), DateTime.UtcNow);

    private static BrowserState Browser(string title, bool playing) =>
        new(playing, title, "Artist", "", 10, 180, true, true, true, true, false, DateTime.UtcNow);

    [Fact]
    public void PreferBrowser_FalseWhenBrowserMissingOrBlank()
    {
        Assert.False(MediaArbitration.PreferBrowser(Track("Song", true), null));
        Assert.False(MediaArbitration.PreferBrowser(null, null));
        Assert.False(MediaArbitration.PreferBrowser(Track("Song", false), Browser("   ", true)));
    }

    [Fact]
    public void PreferBrowser_TrueWhenOnlyBrowserHasTrack()
    {
        Assert.True(MediaArbitration.PreferBrowser(null, Browser("Web Song", false)));
        Assert.True(MediaArbitration.PreferBrowser(Track("", true), Browser("Web Song", true)));
    }

    [Fact]
    public void PreferBrowser_PlayingBeatsPausedAndTieGoesToSmtc()
    {
        Assert.True(MediaArbitration.PreferBrowser(Track("Desktop", false), Browser("Web", true)));
        Assert.False(MediaArbitration.PreferBrowser(Track("Desktop", true), Browser("Web", false)));
        Assert.False(MediaArbitration.PreferBrowser(Track("Desktop", true), Browser("Web", true)));
        Assert.False(MediaArbitration.PreferBrowser(Track("Desktop", false), Browser("Web", false)));
    }

    [Theory]
    [InlineData("Spotify.exe", true, false)]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", true, false)]
    [InlineData("msedge_open.spotify.com-1234", false, true)]
    [InlineData("chrome_https://open.spotify.com/", false, true)]
    [InlineData("vlc.exe", false, false)]
    [InlineData(null, false, false)]
    public void AppIdClassification_DistinguishesDesktopPwaAndOther(string? appId, bool isDesktop, bool isPwa)
    {
        Assert.Equal(isDesktop, MediaArbitration.IsSpotifyDesktopAppId(appId));
        Assert.Equal(isPwa, MediaArbitration.IsSpotifyPwaAppId(appId));
    }

    [Fact]
    public void SamePlayback_MatchesTrimmedCaseInsensitiveTitles()
    {
        Assert.True(MediaArbitration.SamePlayback(Track("  Hello World ", true), Browser("hello world", true)));
        Assert.False(MediaArbitration.SamePlayback(Track("Song A", true), Browser("Song B", true)));
        Assert.False(MediaArbitration.SamePlayback(null, Browser("Song B", true)));
    }
}
