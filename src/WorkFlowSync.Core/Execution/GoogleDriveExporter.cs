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
        if (_mode == GoogleDocExportMode.Skip)
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
}
