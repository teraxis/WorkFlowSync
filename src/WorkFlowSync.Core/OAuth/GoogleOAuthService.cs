using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using WorkFlowSync.Core.Config;

namespace WorkFlowSync.Core.OAuth;

public sealed record OAuthTokenResponse(
    [property: JsonPropertyName("access_token")] string? AccessToken,
    [property: JsonPropertyName("refresh_token")] string? RefreshToken,
    [property: JsonPropertyName("expires_in")] int? ExpiresIn,
    [property: JsonPropertyName("token_type")] string? TokenType,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("error_description")] string? ErrorDescription);

public sealed record GoogleUserInfo(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("email")] string? Email,
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("picture")] string? Picture);

public sealed record CloudFolderItem(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("modifiedTime")] DateTimeOffset? ModifiedTime);

internal sealed record DriveFileListResponse(
    [property: JsonPropertyName("files")] List<CloudFolderItem>? Files);

public interface IGoogleOAuthService
{
    Task<CloudAccount> AuthorizeAsync(string clientId, string clientSecret, Action<string> openBrowser, CancellationToken ct = default);
    Task<bool> RefreshTokenIfNeededAsync(CloudAccount account, CancellationToken ct = default);
    Task<bool> TestConnectionAsync(CloudAccount account, CancellationToken ct = default);
    Task<IReadOnlyList<CloudFolderItem>> ListFoldersAsync(CloudAccount account, string parentId = "root", CancellationToken ct = default);
}

public sealed class GoogleOAuthService : IGoogleOAuthService
{
    private const string AuthEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    private const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    private const string UserInfoEndpoint = "https://www.googleapis.com/oauth2/v2/userinfo";
    private const string DriveAboutEndpoint = "https://www.googleapis.com/drive/v3/about?fields=user";

    private const string Scopes = "https://www.googleapis.com/auth/drive.readonly https://www.googleapis.com/auth/userinfo.email profile";

    private readonly HttpClient _httpClient;

    public GoogleOAuthService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public static int GetFreeLoopbackPort()
    {
        using var tcp = new TcpListener(IPAddress.Loopback, 0);
        tcp.Start();
        var port = ((IPEndPoint)tcp.LocalEndpoint).Port;
        tcp.Stop();
        return port;
    }

    public async Task<CloudAccount> AuthorizeAsync(string clientId, string clientSecret, Action<string> openBrowser, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new ArgumentException("Client ID cannot be empty", nameof(clientId));

        var port = GetFreeLoopbackPort();
        var redirectUri = $"http://127.0.0.1:{port}/";
        var state = Guid.NewGuid().ToString("N");

        var authUrl = $"{AuthEndpoint}?" +
            $"client_id={Uri.EscapeDataString(clientId.Trim())}&" +
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}&" +
            $"response_type=code&" +
            $"scope={Uri.EscapeDataString(Scopes)}&" +
            $"access_type=offline&" +
            $"prompt=consent&" +
            $"state={Uri.EscapeDataString(state)}";

        using var listener = new HttpListener();
        listener.Prefixes.Add(redirectUri);
        listener.Start();

        try
        {
            openBrowser(authUrl);

            // Wait for incoming callback on the local port with cancellation
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(3));

            HttpListenerContext context;
            try
            {
                var contextTask = listener.GetContextAsync();
                var completedTask = await Task.WhenAny(contextTask, Task.Delay(-1, timeoutCts.Token));
                if (completedTask != contextTask)
                {
                    throw new OperationCanceledException("Authorization timed out or was cancelled by user.");
                }
                context = await contextTask;
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                throw new OperationCanceledException("Authorization was cancelled by user.", ct);
            }

            var request = context.Request;
            var query = request.QueryString;

            var error = query["error"];
            if (!string.IsNullOrEmpty(error))
            {
                SendHtmlResponse(context.Response, false, $"Google returned an error: {error}");
                throw new InvalidOperationException($"Google OAuth error: {error}");
            }

            var returnedState = query["state"];
            if (!string.Equals(state, returnedState, StringComparison.Ordinal))
            {
                SendHtmlResponse(context.Response, false, "Invalid state parameter.");
                throw new InvalidOperationException("OAuth state mismatch; request was rejected for security reasons.");
            }

            var code = query["code"];
            if (string.IsNullOrWhiteSpace(code))
            {
                SendHtmlResponse(context.Response, false, "Authorization code was missing.");
                throw new InvalidOperationException("Authorization code was not returned by Google.");
            }

            SendHtmlResponse(context.Response, true, "Успішно підключено! Можете повернутися до програми.");

            // Exchange code for tokens
            var tokens = await ExchangeCodeForTokensAsync(code, clientId.Trim(), clientSecret.Trim(), redirectUri, ct);
            if (string.IsNullOrWhiteSpace(tokens.AccessToken))
            {
                throw new InvalidOperationException(tokens.ErrorDescription ?? tokens.Error ?? "Failed to retrieve access token.");
            }

            // Fetch user info for email/name
            var userInfo = await FetchUserInfoAsync(tokens.AccessToken, ct);

            return new CloudAccount
            {
                Id = Guid.NewGuid().ToString("N"),
                Provider = CloudProvider.GoogleDrive,
                Name = userInfo?.Name ?? userInfo?.Email ?? "Google Drive",
                Email = userInfo?.Email ?? "",
                ClientId = clientId.Trim(),
                ClientSecret = clientSecret.Trim(),
                AccessToken = tokens.AccessToken,
                RefreshToken = tokens.RefreshToken,
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn ?? 3600),
                CreatedAtUtc = DateTimeOffset.UtcNow,
                IsActive = true
            };
        }
        finally
        {
            try { listener.Stop(); } catch { /* best effort */ }
        }
    }

    public async Task<OAuthTokenResponse> ExchangeCodeForTokensAsync(string code, string clientId, string clientSecret, string redirectUri, CancellationToken ct = default)
    {
        var dict = new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["grant_type"] = "authorization_code",
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(dict)
        };

        using var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<OAuthTokenResponse>(json)
            ?? throw new InvalidOperationException("Could not parse token response from Google.");
    }

    public async Task<bool> RefreshTokenIfNeededAsync(CloudAccount account, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(account.RefreshToken) ||
            string.IsNullOrWhiteSpace(account.ClientId))
        {
            return false;
        }

        if (!account.IsExpired && !string.IsNullOrWhiteSpace(account.AccessToken))
            return true;

        var dict = new Dictionary<string, string>
        {
            ["client_id"] = account.ClientId,
            ["client_secret"] = account.ClientSecret,
            ["refresh_token"] = account.RefreshToken,
            ["grant_type"] = "refresh_token",
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(dict)
        };

        using var response = await _httpClient.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return false;

        var json = await response.Content.ReadAsStringAsync(ct);
        var tokens = JsonSerializer.Deserialize<OAuthTokenResponse>(json);
        if (tokens is null || string.IsNullOrWhiteSpace(tokens.AccessToken)) return false;

        account.AccessToken = tokens.AccessToken;
        if (!string.IsNullOrWhiteSpace(tokens.RefreshToken))
            account.RefreshToken = tokens.RefreshToken;
        account.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(tokens.ExpiresIn ?? 3600);
        return true;
    }

    public async Task<GoogleUserInfo?> FetchUserInfoAsync(string accessToken, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, UserInfoEndpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            using var res = await _httpClient.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;

            var json = await res.Content.ReadAsStringAsync(ct);
            return JsonSerializer.Deserialize<GoogleUserInfo>(json);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> TestConnectionAsync(CloudAccount account, CancellationToken ct = default)
    {
        try
        {
            var refreshed = await RefreshTokenIfNeededAsync(account, ct);
            if (!refreshed && string.IsNullOrWhiteSpace(account.AccessToken)) return false;

            using var req = new HttpRequestMessage(HttpMethod.Get, DriveAboutEndpoint);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
            using var res = await _httpClient.SendAsync(req, ct);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<CloudFolderItem>> ListFoldersAsync(CloudAccount account, string parentId = "root", CancellationToken ct = default)
    {
        await RefreshTokenIfNeededAsync(account, ct);
        if (string.IsNullOrWhiteSpace(account.AccessToken))
            throw new InvalidOperationException("Account is not authorized.");

        var pid = string.IsNullOrWhiteSpace(parentId) ? "root" : parentId;
        var query = $"'{pid.Replace("'", "\\'")}' in parents and mimeType = 'application/vnd.google-apps.folder' and trashed = false";
        var url = $"https://www.googleapis.com/drive/v3/files?q={Uri.EscapeDataString(query)}&fields=files(id,name,modifiedTime)&orderBy=name&pageSize=100";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.AccessToken);
        using var res = await _httpClient.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
        {
            var err = await res.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Failed to list folders (HTTP {(int)res.StatusCode}): {err}");
        }

        var json = await res.Content.ReadAsStringAsync(ct);
        var parsed = JsonSerializer.Deserialize<DriveFileListResponse>(json);
        return parsed?.Files ?? (IReadOnlyList<CloudFolderItem>)Array.Empty<CloudFolderItem>();
    }

    private static void SendHtmlResponse(HttpListenerResponse response, bool success, string message)
    {
        try
        {
            response.ContentType = "text/html; charset=utf-8";
            response.StatusCode = success ? (int)HttpStatusCode.OK : (int)HttpStatusCode.BadRequest;

            var title = success ? "✓ Авторизація успішна!" : "✕ Помилка авторизації";
            var color = success ? "#16a34a" : "#dc2626";

            var html = $@"<!DOCTYPE html>
<html lang=""uk"">
<head>
    <meta charset=""utf-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1"">
    <title>WorkFlowSync</title>
    <style>
        body {{
            font-family: -apple-system, BlinkMacSystemFont, ""Segoe UI"", Roboto, Helvetica, Arial, sans-serif;
            background: #f8fafc;
            color: #1e293b;
            display: flex;
            align-items: center;
            justify-content: center;
            min-height: 100vh;
            margin: 0;
            padding: 20px;
        }}
        .card {{
            background: #ffffff;
            border-radius: 16px;
            box-shadow: 0 10px 25px -5px rgba(0, 0, 0, 0.05), 0 8px 10px -6px rgba(0, 0, 0, 0.05);
            max-width: 460px;
            width: 100%;
            padding: 36px 32px;
            text-align: center;
            border: 1px solid #e2e8f0;
        }}
        h2 {{
            color: {color};
            margin-top: 0;
            margin-bottom: 12px;
            font-size: 24px;
        }}
        p {{
            color: #64748b;
            font-size: 15px;
            line-height: 1.5;
            margin-bottom: 24px;
        }}
        .badge {{
            display: inline-block;
            background: #e2e8f0;
            color: #334155;
            padding: 6px 14px;
            border-radius: 9999px;
            font-size: 13px;
            font-weight: 500;
        }}
    </style>
</head>
<body>
    <div class=""card"">
        <h2>{title}</h2>
        <p>{WebUtility.HtmlEncode(message)}</p>
        <div class=""badge"">WorkFlowSync</div>
    </div>
</body>
</html>";
            var buffer = System.Text.Encoding.UTF8.GetBytes(html);
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }
        catch
        {
            // Best effort
        }
    }
}
