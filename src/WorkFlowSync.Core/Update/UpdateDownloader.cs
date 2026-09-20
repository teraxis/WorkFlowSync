using System.Net.Http.Headers;

namespace WorkFlowSync.Core.Update;

/// <summary>
/// Downloads a release asset from GitHub with progress reporting.
/// </summary>
public static class UpdateDownloader
{
    /// <summary>
    /// Downloads <paramref name="url"/> to <paramref name="targetPath"/>, calling <paramref name="onProgress"/>
    /// with a percentage (0–100) whenever a meaningful chunk arrives.
    /// </summary>
    public static async Task DownloadAsync(
        string url,
        string targetPath,
        Action<int>? onProgress = null,
        CancellationToken cancel = default)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("WorkFlowSync", WorkFlowSyncInfo.Version));
        http.Timeout = TimeSpan.FromMinutes(10);

        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? -1;
        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        await using var source = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        await using var dest = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int lastPercent = -1;

        while (true)
        {
            var read = await source.ReadAsync(buffer, cancel).ConfigureAwait(false);
            if (read == 0) break;
            await dest.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
            totalRead += read;

            if (totalBytes > 0 && onProgress is not null)
            {
                var percent = (int)(totalRead * 100 / totalBytes);
                if (percent != lastPercent)
                {
                    lastPercent = percent;
                    onProgress(percent);
                }
            }
        }

        if (totalRead == 0)
            throw new InvalidOperationException("Downloaded file is empty.");
    }
}
