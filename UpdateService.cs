using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;

namespace SpotDock;

/// <summary>
/// Updates through GitHub Releases: compares the newest release version with
/// the app version; if there is a newer one, downloads the published .exe
/// asset and replaces the current one (through a script that waits for the
/// app to close).
/// To publish an update:
///  1. raise <Version> in the .csproj and publish;
///  2. create a GitHub release tagged "vX.Y.Z" with SpotDock.exe attached.
/// </summary>
internal static class UpdateService
{
    /// <summary>Automatic updates through GitHub Releases for ryanbuening/spotdock.
    /// Setting this to false disables update checks and downloads.</summary>
    public const bool AutoUpdateEnabled = true;

    // GitHub "owner/repo" holding the releases. With "CHANGEME" the check is disabled.
    public const string GitHubRepo = "ryanbuening/spotdock";

    public static bool IsConfigured => AutoUpdateEnabled && !GitHubRepo.Contains("CHANGEME");

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    public static async Task<(Version Version, string Url)?> CheckAsync()
    {
        if (!IsConfigured) return null;

        using var http = NewClient();
        using var response = await http.GetAsync($"https://api.github.com/repos/{GitHubRepo}/releases/latest");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null; // repository has no releases yet - not an error
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync();
        return UpdateReleaseParser.Parse(json, CurrentVersion);
    }

    private static bool _updating;

    /// <summary>Downloads the new version and quits the app; a script swaps the
    /// exe and restarts it. Hardened to NEVER leave the user without an app:
    /// validates the download (MZ signature + size - a proxy/captive portal can
    /// return 200 with HTML), swaps through copy+move with retries (antivirus
    /// holds files; the old exe is never truncated) and, if the swap fails,
    /// restarts the old exe intact. Waits for the PID with a limit (PIDs get
    /// recycled). The script is written in the OEM codepage - cmd does not read
    /// UTF-8 and profiles with accents ("Joao") produced mangled paths.</summary>
    public static async Task DownloadAndApplyAsync(string url)
    {
#pragma warning disable CS0162
        if (!AutoUpdateEnabled) return; // final latch: never replace the exe
        if (_updating) return; // one menu item per window - only one applies
        _updating = true;
        try
        {
            string target = Environment.ProcessPath!;
            string temp = Path.Combine(Path.GetTempPath(), "SpotDock.update.exe");
            string staged = target + ".new";

            byte[] bytes;
            using (var http = NewClient())
                bytes = await http.GetByteArrayAsync(url);
            if (bytes.Length < 1_000_000 || bytes[0] != (byte)'M' || bytes[1] != (byte)'Z')
            {
                Diag.Once("update-invalid",
                    $"Update download rejected: {bytes.Length} bytes, not a PE executable (proxy/captive portal?)");
                return;
            }
            await File.WriteAllBytesAsync(temp, bytes);

            string script = Path.Combine(Path.GetTempPath(), "SpotDock.update.cmd");
            int pid = Environment.ProcessId;
            string body =
                "@echo off\r\n" +
                "set tries=0\r\n" +
                ":wait\r\n" +
                "set /a tries+=1\r\n" +
                "if %tries% gtr 60 goto apply\r\n" +
                $"tasklist /fi \"PID eq {pid}\" /fo csv /nh 2>nul | find \"\"\"{pid}\"\"\" >nul\r\n" +
                "if not errorlevel 1 (timeout /t 1 /nobreak >nul & goto wait)\r\n" +
                ":apply\r\n" +
                "set ctries=0\r\n" +
                ":copyloop\r\n" +
                "set /a ctries+=1\r\n" +
                "if %ctries% gtr 12 goto fail\r\n" +
                $"copy /y \"{temp}\" \"{staged}\" >nul 2>&1\r\n" +
                "if errorlevel 1 (timeout /t 1 /nobreak >nul & goto copyloop)\r\n" +
                $"move /y \"{staged}\" \"{target}\" >nul 2>&1\r\n" +
                "if errorlevel 1 (timeout /t 1 /nobreak >nul & goto copyloop)\r\n" +
                $"del \"{temp}\" >nul 2>&1\r\n" +
                $"start \"\" \"{target}\"\r\n" +
                "del \"%~f0\"\r\n" +
                "exit /b\r\n" +
                ":fail\r\n" +
                $"del \"{staged}\" >nul 2>&1\r\n" +
                $"del \"{temp}\" >nul 2>&1\r\n" +
                $"start \"\" \"{target}\"\r\n" +
                "del \"%~f0\"\r\n";

            await File.WriteAllBytesAsync(script, Interop.ToOemBytes(body));

            using var _ = Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"")
            {
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true,
            });

            Program.IntentionalExit = true;
            UiThread.Shutdown();
        }
        finally
        {
            _updating = false;
        }
#pragma warning restore CS0162
    }

    private static HttpClient NewClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.ParseAdd("SpotDock");
        return http;
    }
}
