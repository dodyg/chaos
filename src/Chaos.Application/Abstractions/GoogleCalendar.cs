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
/// An event the app writes to the calendar it created (PLAN3 §11). A null <paramref name="Summary"/> is a Busy block;
/// <paramref name="Id"/> is the client event id, so two devices can never make two events for one task (null on a patch).
/// </summary>
public sealed record GoogleEventBody(string? Id, string? Summary, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string TimeZoneId);

/// <summary>
/// What Google answered to an insert or a patch. A 409 on insert means the event already exists (patch it); a 404 on
/// a patch means it is gone (insert it again).
/// </summary>
public sealed record GoogleEventWrite(bool Conflict, bool NotFound, string? ETag, DateTimeOffset? UpdatedUtc);

/// <summary>One entry of the app calendar's change feed (PLAN3 §11). A cancelled event carries no times.</summary>
public sealed record GoogleEventChange(string Id, string? ETag, DateTimeOffset? UpdatedUtc, bool Cancelled, string? Summary, DateTimeOffset? StartUtc, DateTimeOffset? EndUtc, string? TimeZoneId);

/// <summary>A page of the change feed. <paramref name="TokenExpired"/> means Google no longer knows the sync token: start over without one.</summary>
public sealed record GoogleSyncPage(IReadOnlyList<GoogleEventChange> Items, string? NextPageToken, string? NextSyncToken, bool TokenExpired);

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

    /// <summary>
    /// The sign-in that asks for more scopes than the read-only ones (incremental consent, PLAN3 §11): Google prompts for
    /// the new scope only and keeps the earlier grants. Without an implementation the plain sign-in is used.
    /// </summary>
    Task<GoogleSignIn> SignInAsync(IReadOnlyList<string> extraScopes, CancellationToken ct) => SignInAsync(ct);

    Task<IReadOnlyList<GoogleCalendarInfo>> ListCalendarsAsync(string refreshToken, CancellationToken ct);

    /// <summary>Events overlapping the window with recurring events expanded; cancelled and declined ones are left out.</summary>
    Task<IReadOnlyList<ParsedMeeting>> ListEventsAsync(string refreshToken, string calendarId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct);

    /// <summary>Revokes the token at Google. Best effort: false when Google could not be reached.</summary>
    Task<bool> RevokeAsync(string refreshToken, CancellationToken ct);

    // ---- Time blocking (PLAN3 §11): the calendar the app created, and only its events ---------------------------

    /// <summary>Creates a secondary calendar and returns its id.</summary>
    Task<GoogleCalendarInfo> CreateCalendarAsync(string refreshToken, string name, CancellationToken ct);

    /// <summary>Deletes the app calendar. A calendar that is already gone counts as deleted.</summary>
    Task DeleteCalendarAsync(string refreshToken, string calendarId, CancellationToken ct);

    Task<GoogleEventWrite> InsertEventAsync(string refreshToken, string calendarId, GoogleEventBody body, CancellationToken ct);

    Task<GoogleEventWrite> PatchEventAsync(string refreshToken, string calendarId, string eventId, GoogleEventBody body, CancellationToken ct);

    /// <summary>A 404 (already deleted in Google) is not an error.</summary>
    Task DeleteEventAsync(string refreshToken, string calendarId, string eventId, CancellationToken ct);

    /// <summary>
    /// The calendar's change feed with a sync token (incremental), or everything when the token is null. Cancelled events
    /// are included; the first page without a token ends with the next sync token.
    /// </summary>
    Task<GoogleSyncPage> ListEventChangesAsync(string refreshToken, string calendarId, string? syncToken, string? pageToken, CancellationToken ct);
}
