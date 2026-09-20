using System.Net.Http.Headers;
using System.Text.Json;

namespace WorkFlowSync.Core.Update;

/// <summary>
/// Checks the GitHub Releases API for a newer version of WorkFlowSync.
///
/// The check is intentionally lightweight: one unauthenticated GET, no caching, no retries.
/// Rate limit for unauthenticated requests is 60/hour — more than enough for one check per launch.
/// </summary>
public static class UpdateChecker
{
    private const string Owner = "teraxis";
    private const string Repo = "WorkFlowSync";
    private const string AssetName = "WorkFlowSync.exe";

    private static readonly Uri LatestReleaseUri =
        new($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest");

    /// <summary>
    /// Returns information about the latest release if it is newer than <paramref name="currentVersion"/>,
    /// or null when the running version is already the latest (or the check fails silently).
    /// </summary>
    /// <param name="currentVersion">The running version, e.g. "0.1.0".</param>
    /// <param name="skippedVersion">A version the user chose to skip, or null.</param>
    /// <param name="cancel">Cancellation token.</param>
    public static async Task<UpdateInfo?> CheckAsync(string currentVersion, string? skippedVersion, CancellationToken cancel = default)
    {
        try
        {
            using var http = CreateClient();
            using var response = await http.GetAsync(LatestReleaseUri, cancel).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            using var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancel).ConfigureAwait(false);
            var root = doc.RootElement;

            var tagName = root.GetProperty("tag_name").GetString();
            if (string.IsNullOrWhiteSpace(tagName)) return null;

            var remoteVersion = tagName.TrimStart('v', 'V');
            if (!System.Version.TryParse(remoteVersion, out var remote)) return null;
            if (!System.Version.TryParse(currentVersion, out var current)) return null;
            if (remote <= current) return null;

            // The user asked not to be reminded about this particular version.
            if (!string.IsNullOrEmpty(skippedVersion) &&
                string.Equals(skippedVersion, remoteVersion, StringComparison.OrdinalIgnoreCase))
                return null;

            var htmlUrl = root.GetProperty("html_url").GetString() ?? "";
            var body = root.TryGetProperty("body", out var b) ? b.GetString() : null;

            // Find the exe asset among the release assets.
            string? assetUrl = null;
            if (root.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString();
                    if (string.Equals(name, AssetName, StringComparison.OrdinalIgnoreCase))
                    {
                        assetUrl = asset.GetProperty("browser_download_url").GetString();
                        break;
                    }
                }
            }

            if (string.IsNullOrEmpty(assetUrl)) return null;

            return new UpdateInfo(remoteVersion, htmlUrl, assetUrl, body);
        }
        catch (Exception) when (!cancel.IsCancellationRequested)
        {
            // Network failure, DNS, firewall, JSON shape changed — all silently ignored.
            return null;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WorkFlowSync", WorkFlowSyncInfo.Version));
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }
}
