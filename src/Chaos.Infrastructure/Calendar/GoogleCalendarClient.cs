using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chaos.Application.Abstractions;

namespace Chaos.Infrastructure.Calendar;

/// <summary>The OAuth client of this build. Google treats an installed app's secret as non-confidential, but it is still not committed.</summary>
public sealed record GoogleOAuthConfig(string ClientId, string ClientSecret)
{
    /// <summary>Reads <c>google-oauth.json</c> ({"clientId": "...", "clientSecret": "..."}); null when missing or incomplete.</summary>
    public static GoogleOAuthConfig? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var id = root.TryGetProperty("clientId", out var a) ? a.GetString() : null;
            var secret = root.TryGetProperty("clientSecret", out var b) ? b.GetString() : null;
            return string.IsNullOrWhiteSpace(id) || id.StartsWith("YOUR_", StringComparison.Ordinal) ? null : new GoogleOAuthConfig(id, secret ?? string.Empty);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

/// <summary>
/// Google Calendar over REST (PLAN2 §10.3): the installed-app loopback flow with PKCE, then read-only calls with the narrowest scopes
/// (<c>calendar.calendarlist.readonly</c> and <c>calendar.events.readonly</c>, plus <c>openid email</c> to learn the account's address).
/// Written over <see cref="HttpClient"/> rather than the Google client libraries so that no library token store (which writes to a
/// file) is in the picture: a refresh token is returned to the caller and is never written anywhere by this class. No exception
/// or message here contains a token or the client secret.
/// </summary>
public sealed class GoogleCalendarClient : IGoogleCalendarClient, IDisposable
{
    public const string ScopeCalendarList = "https://www.googleapis.com/auth/calendar.calendarlist.readonly";
    public const string ScopeEvents = "https://www.googleapis.com/auth/calendar.events.readonly";
    public const string Scopes = "openid email " + ScopeCalendarList + " " + ScopeEvents;

    public const string AuthorizeEndpoint = "https://accounts.google.com/o/oauth2/v2/auth";
    public const string TokenEndpoint = "https://oauth2.googleapis.com/token";
    public const string RevokeEndpoint = "https://oauth2.googleapis.com/revoke";
    public const string ApiRoot = "https://www.googleapis.com/calendar/v3/";
    public const int MaxEventsPerCalendar = 5000;
    public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly GoogleOAuthConfig? _config;
    private readonly ISystemBrowser _browser;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (string Token, DateTimeOffset Expires)> _accessTokens = [];

    public GoogleCalendarClient(GoogleOAuthConfig? config, ISystemBrowser browser, TimeProvider? clock = null, HttpClient? http = null)
    {
        _config = config;
        _browser = browser;
        _clock = clock ?? TimeProvider.System;
        _ownsClient = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public bool IsConfigured => _config is not null;

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }

    // ---- Sign-in ---------------------------------------------------------------------------------

    public async Task<GoogleSignIn> SignInAsync(CancellationToken ct)
    {
        var config = _config ?? throw new GoogleCalendarException(GoogleFailure.NotConfigured, "Google sign-in is not set up in this build of Chaos.");
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var redirect = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/";
        var url = new Uri(AuthorizeEndpoint + "?" + Query(
            ("client_id", config.ClientId), ("redirect_uri", redirect), ("response_type", "code"), ("scope", Scopes),
            ("code_challenge", challenge), ("code_challenge_method", "S256"), ("state", state),
            ("access_type", "offline"), ("prompt", "consent select_account")));
        if (!_browser.Open(url))
        {
            throw new GoogleCalendarException(GoogleFailure.Other, "Chaos could not open your web browser to sign in to Google.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SignInTimeout);
        string code;
        try
        {
            code = await WaitForRedirectAsync(listener, state, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GoogleCalendarException(GoogleFailure.Other, "Google sign-in timed out. Try again.");
        }

        var response = await PostFormAsync(TokenEndpoint,
            [("code", code), ("client_id", config.ClientId), ("client_secret", config.ClientSecret), ("redirect_uri", redirect), ("grant_type", "authorization_code"), ("code_verifier", verifier)], ct);
        using var document = ParseJson(response.Body);
        if (!response.Success)
        {
            throw TokenError(document);
        }

        var root = document.RootElement;
        var refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
        if (string.IsNullOrEmpty(refresh))
        {
            throw new GoogleCalendarException(GoogleFailure.Other, "Google did not allow Chaos to stay signed in. Try again and accept the access request.");
        }

        CacheAccessToken(refresh, root);
        var (subject, email) = ReadIdentity(root.TryGetProperty("id_token", out var idToken) ? idToken.GetString() : null);
        return new GoogleSignIn(subject, email, refresh);
    }

    private static async Task<string> WaitForRedirectAsync(TcpListener listener, string expectedState, CancellationToken ct)
    {
        while (true)
        {
            using var client = await listener.AcceptTcpClientAsync(ct);
            var stream = client.GetStream();
            var head = await ReadRequestHeadAsync(stream, ct);
            var target = head.Split(' ').ElementAtOrDefault(1) ?? "/";
            var queryAt = target.IndexOf('?', StringComparison.Ordinal);
            if (queryAt < 0)
            {
                await RespondAsync(stream, 404, "Not found.", ct); // favicon and probes
                continue;
            }

            var query = ParseQuery(target[(queryAt + 1)..]);
            if (!query.TryGetValue("state", out var state) || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(state), Encoding.ASCII.GetBytes(expectedState)))
            {
                await RespondAsync(stream, 400, "This sign-in link does not belong to Chaos. You can close this tab.", ct);
                continue;
            }

            if (query.ContainsKey("error"))
            {
                await RespondAsync(stream, 200, "Google sign-in was cancelled. You can close this tab and return to Chaos.", ct);
                throw new GoogleCalendarException(query["error"] == "access_denied" ? GoogleFailure.Other : GoogleFailure.Blocked,
                    query["error"] == "access_denied" ? "Google sign-in was cancelled." : "Google refused the sign-in for this account.");
            }

            if (!query.TryGetValue("code", out var code) || code.Length == 0)
            {
                await RespondAsync(stream, 400, "Google sent no sign-in code. You can close this tab.", ct);
                continue;
            }

            await RespondAsync(stream, 200, "You are signed in. You can close this tab and return to Chaos.", ct);
            return code;
        }
    }

    private static async Task<string> ReadRequestHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), ct);
            if (read == 0)
            {
                break;
            }

            length += read;
            if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        var text = Encoding.ASCII.GetString(buffer, 0, length);
        var end = text.IndexOf("\r\n", StringComparison.Ordinal);
        return end < 0 ? text : text[..end];
    }

    private static async Task RespondAsync(NetworkStream stream, int status, string message, CancellationToken ct)
    {
        var body = $"<!doctype html><meta charset=\"utf-8\"><title>Chaos</title><body style=\"font-family:sans-serif;margin:3em\"><p>{WebUtility.HtmlEncode(message)}</p>";
        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head), ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    // ---- Calendar list and events ------------------------------------------------------------------

    public async Task<IReadOnlyList<GoogleCalendarInfo>> ListCalendarsAsync(string refreshToken, CancellationToken ct)
    {
        var result = new List<GoogleCalendarInfo>();
        string? page = null;
        do
        {
            var query = new List<(string, string)> { ("maxResults", "250"), ("minAccessRole", "freeBusyReader"), ("fields", "nextPageToken,items(id,summary,summaryOverride,backgroundColor,primary,deleted)") };
            if (page is not null)
            {
                query.Add(("pageToken", page));
            }

            using var document = await GetAsync(refreshToken, "users/me/calendarList?" + Query([.. query]), ct);
            var root = document.RootElement;
            if (root.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (item.TryGetProperty("deleted", out var deleted) && deleted.ValueKind == JsonValueKind.True)
                    {
                        continue;
                    }

                    var id = Text(item, "id");
                    if (id is null)
                    {
                        continue;
                    }

                    var name = Text(item, "summaryOverride") ?? Text(item, "summary") ?? id;
                    result.Add(new GoogleCalendarInfo(id, name.Length > 120 ? name[..120] : name, Text(item, "backgroundColor"),
                        item.TryGetProperty("primary", out var primary) && primary.ValueKind == JsonValueKind.True));
                }
            }

            page = Text(root, "nextPageToken");
        }
        while (page is not null && result.Count < 500);
        return result;
    }

    public async Task<IReadOnlyList<ParsedMeeting>> ListEventsAsync(string refreshToken, string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct)
    {
        var result = new List<ParsedMeeting>();
        string? page = null;
        do
        {
            var query = new List<(string, string)>
            {
                ("singleEvents", "true"), ("showDeleted", "false"), ("maxResults", "250"),
                ("timeMin", fromUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
                ("timeMax", toUtc.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
                ("fields", "nextPageToken,items(id,status,summary,start,end,location,hangoutLink,htmlLink,transparency,conferenceData(entryPoints(entryPointType,uri)),attendees(self,responseStatus))"),
            };
            if (page is not null)
            {
                query.Add(("pageToken", page));
            }

            using var document = await GetAsync(refreshToken, $"calendars/{Uri.EscapeDataString(calendarId)}/events?" + Query([.. query]), ct);
            var root = document.RootElement;
            if (root.TryGetProperty("items", out var items))
            {
                foreach (var item in items.EnumerateArray())
                {
                    if (MapEvent(item) is { } meeting)
                    {
                        result.Add(meeting);
                    }
                }
            }

            page = Text(root, "nextPageToken");
        }
        while (page is not null && result.Count < MaxEventsPerCalendar);
        return result;
    }

    /// <summary>PLAN2 §10.3: cancelled and own-declined events are hidden, transparent ones are free time.</summary>
    internal static ParsedMeeting? MapEvent(JsonElement item)
    {
        var id = Text(item, "id");
        if (id is null || Text(item, "status") == "cancelled")
        {
            return null;
        }

        if (item.TryGetProperty("attendees", out var attendees) && attendees.ValueKind == JsonValueKind.Array
            && attendees.EnumerateArray().Any(a => a.TryGetProperty("self", out var self) && self.ValueKind == JsonValueKind.True && Text(a, "responseStatus") == "declined"))
        {
            return null;
        }

        if (!item.TryGetProperty("start", out var start) || !item.TryGetProperty("end", out var end))
        {
            return null;
        }

        var title = Text(item, "summary") is { Length: > 0 } summary ? summary : "(No title)";
        title = title.Length > 300 ? title[..300] : title;
        var video = Text(item, "hangoutLink");
        if (video is null && item.TryGetProperty("conferenceData", out var conference) && conference.TryGetProperty("entryPoints", out var points))
        {
            video = points.EnumerateArray().Where(point => Text(point, "entryPointType") == "video").Select(point => Text(point, "uri")).FirstOrDefault(uri => uri is not null);
        }

        var busy = Text(item, "transparency") != "transparent";
        var location = Text(item, "location");
        if (Text(start, "date") is { } startDay && Text(end, "date") is { } endDay
            && DateOnly.TryParseExact(startDay, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var startDate)
            && DateOnly.TryParseExact(endDay, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var endDate)
            && endDate > startDate)
        {
            return new ParsedMeeting(id, MidnightUtc(startDate), MidnightUtc(endDate), true, startDate, endDate, title, location, Https(video), Https(Text(item, "htmlLink")), busy);
        }

        if (Text(start, "dateTime") is { } startText && Text(end, "dateTime") is { } endText
            && DateTimeOffset.TryParse(startText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var startInstant)
            && DateTimeOffset.TryParse(endText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var endInstant)
            && endInstant > startInstant)
        {
            return new ParsedMeeting(id, startInstant.ToUniversalTime(), endInstant.ToUniversalTime(), false, null, null, title, location, Https(video), Https(Text(item, "htmlLink")), busy);
        }

        return null;
    }

    private static DateTimeOffset MidnightUtc(DateOnly date) => new(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero);

    private static string? Https(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? uri.AbsoluteUri : null;

    // ---- Revoke ------------------------------------------------------------------------------------

    public async Task<bool> RevokeAsync(string refreshToken, CancellationToken ct)
    {
        lock (_gate)
        {
            _accessTokens.Remove(refreshToken);
        }

        try
        {
            var response = await PostFormAsync(RevokeEndpoint, [("token", refreshToken)], ct);

            // 400 means Google no longer knows the token (already revoked or expired): the goal is met.
            return response.Success || response.Status == HttpStatusCode.BadRequest;
        }
        catch (GoogleCalendarException)
        {
            return false;
        }
    }

    // ---- HTTP, tokens and errors -------------------------------------------------------------------

    private async Task<JsonDocument> GetAsync(string refreshToken, string relative, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var access = await AccessTokenAsync(refreshToken, ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, ApiRoot + relative);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
            var response = await SendAsync(request, ct);
            if (response.Status == HttpStatusCode.Unauthorized && attempt == 0)
            {
                lock (_gate)
                {
                    _accessTokens.Remove(refreshToken); // an expired access token: one retry with a fresh one
                }

                continue;
            }

            var document = ParseJson(response.Body);
            if (response.Success)
            {
                return document;
            }

            using (document)
            {
                throw ApiError(response.Status, document);
            }
        }
    }

    private async Task<string> AccessTokenAsync(string refreshToken, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_accessTokens.TryGetValue(refreshToken, out var cached) && cached.Expires > _clock.GetUtcNow().AddMinutes(1))
            {
                return cached.Token;
            }
        }

        var config = _config ?? throw new GoogleCalendarException(GoogleFailure.NotConfigured, "Google sign-in is not set up in this build of Chaos.");
        var response = await PostFormAsync(TokenEndpoint,
            [("client_id", config.ClientId), ("client_secret", config.ClientSecret), ("refresh_token", refreshToken), ("grant_type", "refresh_token")], ct);
        using var document = ParseJson(response.Body);
        if (!response.Success)
        {
            throw TokenError(document);
        }

        CacheAccessToken(refreshToken, document.RootElement);
        lock (_gate)
        {
            return _accessTokens[refreshToken].Token;
        }
    }

    private void CacheAccessToken(string refreshToken, JsonElement tokenResponse)
    {
        var token = Text(tokenResponse, "access_token");
        if (token is null)
        {
            return;
        }

        var seconds = tokenResponse.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var value) ? value : 300;
        lock (_gate)
        {
            _accessTokens[refreshToken] = (token, _clock.GetUtcNow().AddSeconds(seconds));
        }
    }

    private sealed record Reply(HttpStatusCode Status, string Body)
    {
        public bool Success => (int)Status is >= 200 and < 300;
    }

    private async Task<Reply> PostFormAsync(string endpoint, (string Name, string Value)[] fields, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(fields.Select(f => KeyValuePair.Create(f.Name, f.Value))) };
        return await SendAsync(request, ct);
    }

    private async Task<Reply> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CallTimeout);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            return new Reply(response.StatusCode, body.Length > 4_000_000 ? string.Empty : body);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new GoogleCalendarException(GoogleFailure.Offline, "Google Calendar did not answer in time. Chaos will try again.");
        }
        catch (HttpRequestException)
        {
            throw new GoogleCalendarException(GoogleFailure.Offline, "Chaos could not reach Google. It will try again when you are online.");
        }
    }

    private static JsonDocument ParseJson(string body)
    {
        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }
        catch (JsonException)
        {
            return JsonDocument.Parse("{}");
        }
    }

    private static GoogleCalendarException TokenError(JsonDocument document)
    {
        var error = Text(document.RootElement, "error");
        return error switch
        {
            "invalid_grant" => new GoogleCalendarException(GoogleFailure.SignInAgain, GoogleCalendarMessages.SignInAgain),
            "access_denied" or "admin_policy_enforced" or "org_internal" or "disabled_client" =>
                new GoogleCalendarException(GoogleFailure.Blocked, "Google Calendar is blocked for this account by its administrator."),
            "invalid_client" or "unauthorized_client" =>
                new GoogleCalendarException(GoogleFailure.Other, "Google does not accept this build of Chaos. Its Google client settings need checking."),
            _ => new GoogleCalendarException(GoogleFailure.Other, "Google refused the sign-in."),
        };
    }

    private static GoogleCalendarException ApiError(HttpStatusCode status, JsonDocument document)
    {
        var reason = document.RootElement.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object
            && error.TryGetProperty("errors", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0
                ? Text(list[0], "reason")
                : null;
        return status switch
        {
            HttpStatusCode.Unauthorized => new GoogleCalendarException(GoogleFailure.SignInAgain, GoogleCalendarMessages.SignInAgain),
            HttpStatusCode.Forbidden when reason is "rateLimitExceeded" or "userRateLimitExceeded" or "quotaExceeded" =>
                new GoogleCalendarException(GoogleFailure.Other, "Google Calendar is limiting requests. Chaos will try again."),
            HttpStatusCode.Forbidden => new GoogleCalendarException(GoogleFailure.Blocked, "Google Calendar is blocked for this account or calendar. A Google Workspace administrator may need to allow it."),
            HttpStatusCode.NotFound => new GoogleCalendarException(GoogleFailure.Other, "Google no longer shows that calendar to this account."),
            HttpStatusCode.TooManyRequests => new GoogleCalendarException(GoogleFailure.Other, "Google Calendar is limiting requests. Chaos will try again."),
            _ => new GoogleCalendarException(GoogleFailure.Other, "Google Calendar could not be read right now. Chaos will try again."),
        };
    }

    // ---- Small helpers -----------------------------------------------------------------------------

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static (string Subject, string Email) ReadIdentity(string? idToken)
    {
        // The token came straight from Google's token endpoint over TLS, so its payload is read without checking the signature.
        var parts = (idToken ?? string.Empty).Split('.');
        if (parts.Length >= 2)
        {
            try
            {
                using var payload = JsonDocument.Parse(FromBase64Url(parts[1]));
                var subject = Text(payload.RootElement, "sub");
                var email = Text(payload.RootElement, "email");
                if (!string.IsNullOrEmpty(subject) && !string.IsNullOrEmpty(email))
                {
                    return (subject, email);
                }
            }
            catch (Exception error) when (error is FormatException or JsonException)
            {
                throw new GoogleCalendarException(GoogleFailure.Other, "Google did not say which account signed in. Try again.", error);
            }
        }

        throw new GoogleCalendarException(GoogleFailure.Other, "Google did not say which account signed in. Try again.");
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string text)
    {
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        return Convert.FromBase64String(padded);
    }

    private static string Query(params (string Name, string Value)[] pairs) =>
        string.Join('&', pairs.Select(p => $"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(p.Value)}"));

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            var name = Uri.UnescapeDataString((eq < 0 ? part : part[..eq]).Replace('+', ' '));
            result[name] = eq < 0 ? string.Empty : Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
        }

        return result;
    }
}

/// <summary>The one message both layers show when Google says to sign in again.</summary>
internal static class GoogleCalendarMessages
{
    public const string SignInAgain = "Google Calendar: sign in again.";
}
