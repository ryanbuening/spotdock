using SpotDock;
using Xunit;

namespace SpotDock.Tests;

public sealed class WidgetSettingsTests
{
    private static string TempSettingsPath() =>
        Path.Combine(Path.GetTempPath(), "spotdock-tests-" + Guid.NewGuid().ToString("N"), "settings.json");

    [Fact]
    public void LoadFrom_MissingOrCorruptFile_ReturnsSafeDefaults()
    {
        string path = TempSettingsPath();
        var missing = WidgetSettings.LoadFrom(path);
        Assert.Equal(0.5, missing.Opacity);
        Assert.Equal(2, missing.SettingsVersion);
        Assert.True(missing.ShowArt);
        Assert.True(missing.ShowProgress);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            File.WriteAllText(path, "{ broken json");
            var corrupt = WidgetSettings.LoadFrom(path);
            Assert.Equal(0.5, corrupt.Opacity);
            Assert.Equal(2, corrupt.SettingsVersion);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void LoadFrom_MigratesLegacyV1AndClampsOpacity()
    {
        string path = TempSettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            // Legacy v1 with AutoPosition=false, removed StretchText key, and null dictionaries
            File.WriteAllText(path, """{"AutoPosition":false,"X":320,"Opacity":1.0,"StretchText":true,"SettingsVersion":1,"ManualX":null,"ManualGap":null,"WidgetsAnchor":null}""");
            var migrated = WidgetSettings.LoadFrom(path);
            Assert.Equal(2, migrated.SettingsVersion);
            Assert.Equal(0.5, migrated.Opacity);
            Assert.NotNull(migrated.ManualX);
            Assert.Equal(320, migrated.ManualX[0]);

            // v2 with out-of-range opacity gets clamped
            File.WriteAllText(path, """{"Opacity":0.0,"SettingsVersion":2}""");
            Assert.Equal(0.2, WidgetSettings.LoadFrom(path).Opacity);

            File.WriteAllText(path, """{"Opacity":5.0,"SettingsVersion":2}""");
            Assert.Equal(1.0, WidgetSettings.LoadFrom(path).Opacity);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void SaveToAndLoadFrom_RoundTripsSettings()
    {
        string path = TempSettingsPath();
        try
        {
            var saved = new WidgetSettings
            {
                Opacity = 0.75,
                ShowArt = false,
                ShowProgress = false,
                SettingsVersion = 2,
            };
            saved.ManualX[0] = 412;
            saved.SaveTo(path);

            var loaded = WidgetSettings.LoadFrom(path);
            Assert.Equal(0.75, loaded.Opacity);
            Assert.False(loaded.ShowArt);
            Assert.False(loaded.ShowProgress);
            Assert.False(loaded.AutoPosition);
            Assert.Equal(412, loaded.X);
            Assert.Equal(412, loaded.ManualX[0]);
        }
        finally
        {
            string? dir = Path.GetDirectoryName(path);
            if (dir != null && Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
