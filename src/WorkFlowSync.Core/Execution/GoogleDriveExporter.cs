using WorkFlowSync.Core.Config;
using WorkFlowSync.Core.OAuth;

namespace WorkFlowSync.Core.Execution;

public sealed record ExportResult(
    bool Success,
    long BytesWritten,
    DateTimeOffset? Mtime,
    string? ErrorMessage = null,
    bool AuthRequired = false);

public interface IGoogleDriveExporter
{
    ExportResult Export(string sourcePath, string targetPath, DateTimeOffset? mtime, CancellationToken ct = default);
}

public sealed class GoogleDriveExporter : IGoogleDriveExporter
{
    private readonly GoogleDocExportMode _mode;
    private readonly GoogleAuthConfig? _auth;
    private readonly CloudAccount? _account;
    private readonly IGoogleOAuthService? _oauthService;
    private readonly HttpClient _httpClient;

    public GoogleDriveExporter(
        GoogleDocExportMode mode,
        GoogleAuthConfig? auth = null,
        CloudAccount? account = null,
        IGoogleOAuthService? oauthService = null,
        HttpClient? httpClient = null)
    {
        _mode = mode;
        _auth = auth;
        _account = account;
        _oauthService = oauthService;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    public ExportResult Export(string sourcePath, string targetPath, DateTimeOffset? mtime, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(sourcePath);
        var isGvid = ext.Equals(".gvid", StringComparison.OrdinalIgnoreCase);

        if (!isGvid && _mode == GoogleDocExportMode.Skip)
            return new ExportResult(false, 0, null, "Export mode is Skip");

        var fileId = GoogleDrive.TryGetFileId(sourcePath);
        if (string.IsNullOrWhiteSpace(fileId))
            return new ExportResult(false, 0, null, $"Cannot determine Google Drive file ID for '{Path.GetFileName(sourcePath)}'");

        if (_account is { IsExpired: true, RefreshToken: not null })
        {
            try
            {
                var service = _oauthService ?? new WorkFlowSync.Core.OAuth.GoogleOAuthService();
                service.RefreshTokenIfNeededAsync(_account, ct).GetAwaiter().GetResult();
            }
            catch
            {
                // Best effort refresh
            }
        }

        var token = _account?.AccessToken
            ?? _auth?.AccessToken
            ?? Environment.GetEnvironmentVariable("WORKFLOWSYNC_GOOGLE_ACCESS_TOKEN")
            ?? Environment.GetEnvironmentVariable("GOOGLE_ACCESS_TOKEN");

        if (string.IsNullOrWhiteSpace(token))
            return new ExportResult(false, 0, null, "Google authorization token is missing", AuthRequired: true);

        if (isGvid)
        {
            return ExportGoogleVid(fileId, targetPath, token, mtime, ct);
        }

        var mimeType = GoogleDrive.GetExportMimeType(sourcePath, _mode);
        var url = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}/export?mimeType={Uri.EscapeDataString(mimeType)}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var response = _httpClient.Send(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return new ExportResult(false, 0, null, $"Authorization error ({response.StatusCode})", AuthRequired: true);

            if (!response.IsSuccessStatusCode)
                return new ExportResult(false, 0, null, $"HTTP {(int)response.StatusCode}: {response.ReasonPhrase}");

            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir))
                Directory.CreateDirectory(targetDir);

            var tmpPath = targetPath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                using (var srcStream = response.Content.ReadAsStream(ct))
                using (var dstStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    srcStream.CopyTo(dstStream);
                }

                if (mtime is { } mt)
                {
                    File.SetLastWriteTimeUtc(tmpPath, mt.UtcDateTime);
                }

                File.Move(tmpPath, targetPath, overwrite: true);

                var fi = new FileInfo(targetPath);
                return new ExportResult(true, fi.Length, fi.LastWriteTimeUtc);
            }
            finally
            {
                if (File.Exists(tmpPath))
                {
                    try { File.Delete(tmpPath); } catch { /* best effort */ }
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ExportResult(false, 0, null, ex.Message);
        }
    }

    private ExportResult ExportGoogleVid(string fileId, string targetPath, string token, DateTimeOffset? mtime, CancellationToken ct)
    {
        try
        {
            var postUrl = $"https://www.googleapis.com/drive/v3/files/{Uri.EscapeDataString(fileId)}/download?mimeType=video%2Fmp4";
            using var postReq = new HttpRequestMessage(HttpMethod.Post, postUrl)
            {
                Content = new StringContent("", System.Text.Encoding.UTF8, "application/json")
            };
            postReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var postResp = _httpClient.Send(postReq, HttpCompletionOption.ResponseHeadersRead, ct);
            if (postResp.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                return new ExportResult(false, 0, null, $"Authorization error ({postResp.StatusCode})", AuthRequired: true);

            if (!postResp.IsSuccessStatusCode)
                return new ExportResult(false, 0, null, $"HTTP {(int)postResp.StatusCode}: {postResp.ReasonPhrase}");

            string? downloadUri = null;
            using (var postStream = postResp.Content.ReadAsStream(ct))
            using (var doc = System.Text.Json.JsonDocument.Parse(postStream))
            {
                var root = doc.RootElement;
                if (root.TryGetProperty("done", out var doneProp) && doneProp.GetBoolean() &&
                    root.TryGetProperty("response", out var respProp) &&
                    respProp.TryGetProperty("downloadUri", out var uriProp))
                {
                    downloadUri = uriProp.GetString();
                }
                else if (root.TryGetProperty("name", out var nameProp))
                {
                    var opName = nameProp.GetString();
                    if (!string.IsNullOrWhiteSpace(opName))
                    {
                        downloadUri = PollOperation(opName, token, ct);
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(downloadUri))
                return new ExportResult(false, 0, null, "Could not obtain download URI for Google Vid");

            return DownloadVideoPayload(downloadUri, targetPath, token, mtime, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ExportResult(false, 0, null, ex.Message);
        }
    }

    private string? PollOperation(string opName, string token, CancellationToken ct)
    {
        var opUrl = $"https://www.googleapis.com/drive/v3/operations/{Uri.EscapeDataString(opName)}";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var maxWait = TimeSpan.FromMinutes(10);

        while (sw.Elapsed < maxWait && !ct.IsCancellationRequested)
        {
            Thread.Sleep(2500);
            if (ct.IsCancellationRequested) break;

            using var req = new HttpRequestMessage(HttpMethod.Get, opUrl);
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            using var resp = _httpClient.Send(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode) continue;

            using var stream = resp.Content.ReadAsStream(ct);
            using var doc = System.Text.Json.JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.TryGetProperty("done", out var done) && done.GetBoolean())
            {
                if (root.TryGetProperty("response", out var response) &&
                    response.TryGetProperty("downloadUri", out var uriProp))
                {
                    return uriProp.GetString();
                }
                return null;
            }
        }

        return null;
    }

    private ExportResult DownloadVideoPayload(string downloadUri, string targetPath, string token, DateTimeOffset? mtime, CancellationToken ct)
    {
        var targetDir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(targetDir))
            Directory.CreateDirectory(targetDir);

        var tmpPath = targetPath + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            var noRedirectHandler = new HttpClientHandler { AllowAutoRedirect = false };
            using var dlClient = new HttpClient(noRedirectHandler) { Timeout = TimeSpan.FromMinutes(30) };

            var currentUrl = downloadUri;
            HttpResponseMessage? resp = null;
            const int maxRedirects = 5;

            for (var i = 0; i < maxRedirects; i++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, currentUrl);
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                resp?.Dispose();
                resp = dlClient.Send(req, HttpCompletionOption.ResponseHeadersRead, ct);

                if (resp.StatusCode is System.Net.HttpStatusCode.MovedPermanently or
                    System.Net.HttpStatusCode.Found or
                    System.Net.HttpStatusCode.SeeOther or
                    System.Net.HttpStatusCode.TemporaryRedirect or
                    (System.Net.HttpStatusCode)308)
                {
                    var loc = resp.Headers.Location;
                    if (loc is not null)
                    {
                        currentUrl = loc.IsAbsoluteUri ? loc.ToString() : new Uri(new Uri(currentUrl), loc).ToString();
                        continue;
                    }
                }
                break;
            }

            if (resp is null || !resp.IsSuccessStatusCode)
            {
                var code = resp is not null ? (int)resp.StatusCode : 0;
                resp?.Dispose();
                return new ExportResult(false, 0, null, $"Failed downloading video stream (HTTP {code})");
            }

            using (resp)
            using (var srcStream = resp.Content.ReadAsStream(ct))
            using (var dstStream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: false))
            {
                srcStream.CopyTo(dstStream);
            }

            if (mtime is { } mt)
            {
                File.SetLastWriteTimeUtc(tmpPath, mt.UtcDateTime);
            }

            File.Move(tmpPath, targetPath, overwrite: true);
            var fi = new FileInfo(targetPath);
            return new ExportResult(true, fi.Length, fi.LastWriteTimeUtc);
        }
        finally
        {
            if (File.Exists(tmpPath))
            {
                try { File.Delete(tmpPath); } catch { /* best effort */ }
            }
        }
    }
}
