namespace Chaos.Application.Abstractions;

/// <summary>Opens a web page in the person's own browser (the Google sign-in page). False when nothing could open it.</summary>
public interface ISystemBrowser
{
    bool Open(Uri url);
}

/// <summary>Why a Google call failed, in the words Settings and the calendar use (PLAN2 §10.3 failure states).</summary>
public enum GoogleFailure
{
    /// <summary>The token expired or was revoked: sign in again.</summary>
    SignInAgain = 0,
    /// <summary>A Workspace admin or policy refuses this app or this account.</summary>
    Blocked = 1,
    /// <summary>The network could not be reached.</summary>
    Offline = 2,
    /// <summary>Anything else; the message says what.</summary>
    Other = 3,
    /// <summary>This build has no Google client id (google-oauth.json is missing).</summary>
    NotConfigured = 4,
}

/// <summary>A Google failure the person can read. The message never contains a token or the client secret.</summary>
public sealed class GoogleCalendarException(GoogleFailure kind, string message, Exception? inner = null) : Exception(message, inner)
{
    public GoogleFailure Kind { get; } = kind;
}

/// <summary>The result of a completed sign-in. <paramref name="RefreshToken"/> goes to <see cref="ISecretStore"/> and nowhere else.</summary>
public sealed record GoogleSignIn(string Subject, string Email, string RefreshToken);

/// <summary>A calendar of an account as Google lists it (own, shared with the person, subscribed).</summary>
public sealed record GoogleCalendarInfo(string Id, string Name, string? Color, bool Primary);

/// <summary>
/// Google Calendar over its REST API with read-only scopes (PLAN2 §10.3). Tokens are handed in and out by
/// the caller; nothing here writes them to disk.
/// </summary>
public interface IGoogleCalendarClient
{
    /// <summary>True when this build has a client id; without one Settings explains instead of offering Connect.</summary>
    bool IsConfigured { get; }

    /// <summary>Sets the OAuth client (id and secret) at run time, for a client the person pasted into Settings.</summary>
    void Configure(string clientId, string clientSecret)
    {
    }

    /// <summary>Installed-app flow: the system browser, a loopback redirect and PKCE.</summary>
    /// <exception cref="GoogleCalendarException">Cancelled, timed out, refused, or not configured.</exception>
    Task<GoogleSignIn> SignInAsync(CancellationToken ct);

    Task<IReadOnlyList<GoogleCalendarInfo>> ListCalendarsAsync(string refreshToken, CancellationToken ct);

    /// <summary>Events overlapping the window with recurring events expanded; cancelled and declined ones are left out.</summary>
    Task<IReadOnlyList<ParsedMeeting>> ListEventsAsync(string refreshToken, string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>Revokes the token at Google. Best effort: false when Google could not be reached.</summary>
    Task<bool> RevokeAsync(string refreshToken, CancellationToken ct);
}
