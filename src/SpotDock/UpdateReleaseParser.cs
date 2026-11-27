using System.Text.Json;

namespace SpotDock;

/// <summary>
/// Pure JSON parser for GitHub Releases API responses used by <see cref="UpdateService"/>.
/// </summary>
internal static class UpdateReleaseParser
{
    public static Version NormalizeVersion(Version v)
    {
        int build = v.Build >= 0 ? v.Build : 0;
        return v.Revision > 0
            ? new Version(v.Major, v.Minor, build, v.Revision)
            : new Version(v.Major, v.Minor, build);
    }

    public static (Version Version, string Url)? Parse(string json, Version currentVersion)
    {
        using var doc = JsonDocument.Parse(json);

        string tag = doc.RootElement.TryGetProperty("tag_name", out var tagProp) ? tagProp.GetString() ?? "" : "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var rawLatest))
        {
            // A tag off the pattern (e.g. "v1.3.0-fix") would cut EVERYONE off
            // from updates silently - leave a trace for diagnosis
            Diag.Once("update-tag", "Could not parse the latest release tag as a version: " + tag);
            return null;
        }
        var latest = NormalizeVersion(rawLatest);
        var current = NormalizeVersion(currentVersion);
        if (latest <= current)
            return null;

        if (!doc.RootElement.TryGetProperty("assets", out var assetsProp) ||
            assetsProp.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assetsProp.EnumerateArray())
        {
            string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            // EXACT name: the release also carries the installer (...-Setup.exe)
            // and "first .exe" could grab it - we would replace ourselves with the setup
            if (name.Equals("SpotDock.exe", StringComparison.OrdinalIgnoreCase))
            {
                string url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(url))
                    return (latest, url);
            }
        }
        return null;
    }
}
