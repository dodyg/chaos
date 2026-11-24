using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Sync;

namespace Chaos.Application.Calendar;

/// <summary>An account as Settings shows it. <paramref name="SignedInHere"/> is false on a device that has the synced account but no token.</summary>
public sealed record GoogleAccountView(GoogleAccountRecord Account, bool SignedInHere, GoogleAccountState? State, IReadOnlyList<CalendarSourceView> Calendars);

/// <summary>One calendar of an account with the person's choice for it. <paramref name="Source"/> is set once it was ever chosen.</summary>
public sealed record GoogleCalendarChoice(GoogleCalendarInfo Info, CalendarSourceRecord? Source);

public sealed record ChooseGoogleCalendarCommand(string AccountId, string CalendarId, string Name, string? Color, bool Chosen);

/// <summary>
/// Connects Google accounts and chooses their calendars (PLAN2 §10.3). The refresh token goes into
/// <see cref="ISecretStore"/> under <c>chaos/google/{accountId}</c> and nowhere else; the database keeps the
/// account's email, the chosen calendars and this device's last problem. The account row is synced, so every
/// device shows the account and offers a one-click sign-in; the token itself never leaves the device.
/// </summary>
public sealed class GoogleCalendarCommands(IApplicationStore store, ISecretStore secrets, IGoogleCalendarClient google, TimeProvider clock)
{
    public const string SignInAgainMessage = "Google Calendar: sign in again.";
    public const string SignInHereMessage = "Google Calendar: sign in on this device.";

    public static string SecretKey(string accountId) => $"chaos/google/{accountId}";

    public static string AccountId(string subject) => StorageFormats.NameBasedId(NameBasedId.Names.GoogleAccount(subject));

    public static string SourceId(string accountId, string calendarId) => StorageFormats.NameBasedId(NameBasedId.Names.GoogleCalendarSource(accountId, calendarId));

    public bool IsConfigured => google.IsConfigured;

    /// <summary>Where a client pasted into Settings is kept: the protected secret store, as "id\nsecret".</summary>
    public const string ClientSecretKey = "chaos/google/oauth-client";

    /// <summary>Loads a client saved earlier, unless the build already has one from google-oauth.json.</summary>
    public async Task LoadSavedClientAsync(CancellationToken ct = default)
    {
        if (google.IsConfigured || await secrets.GetAsync(ClientSecretKey, ct) is not { } saved)
        {
            return;
        }

        var parts = saved.Split('\n');
        if (parts.Length == 2 && parts[0].Length > 0)
        {
            google.Configure(parts[0], parts[1]);
        }
    }

    /// <summary>Saves the OAuth client the person created in Google Cloud (a Desktop app client). The secret is never shown again.</summary>
    public async Task SaveClientAsync(string? clientId, string? clientSecret, CancellationToken ct = default)
    {
        var id = (clientId ?? string.Empty).Trim();
        var secret = (clientSecret ?? string.Empty).Trim();
        if (id.Length is 0 or > 200 || !id.EndsWith(".apps.googleusercontent.com", StringComparison.Ordinal) || id.Any(char.IsWhiteSpace))
        {
            throw AppException.Validation("clientId", "The client ID ends with .apps.googleusercontent.com. Copy it from Google Cloud.");
        }

        if (secret.Length is 0 or > 200 || secret.Any(char.IsWhiteSpace))
        {
            throw AppException.Validation("clientSecret", "Paste the client secret from Google Cloud.");
        }

        await secrets.SetAsync(ClientSecretKey, id + "\n" + secret, ct);
        google.Configure(id, secret);
    }

    public async Task<IReadOnlyList<GoogleAccountView>> ListAsync(CancellationToken ct = default)
    {
        var rows = await store.QueryAsync(async (session, token) =>
        {
            var accounts = await session.Calendars.ListAccountsAsync(token);
            var sources = await session.Calendars.ListSourcesAsync(token);
            var views = new List<(GoogleAccountRecord, GoogleAccountState?, List<CalendarSourceView>)>();
            foreach (var account in accounts)
            {
                var calendars = new List<CalendarSourceView>();
                foreach (var source in sources.Where(item => item.Provider == CalendarProvider.Google && item.AccountRef == account.Id))
                {
                    calendars.Add(new CalendarSourceView(source, await session.Calendars.GetStateAsync(source.Id, token), 0));
                }

                views.Add((account, await session.Calendars.GetAccountStateAsync(account.Id, token), calendars));
            }

            return views;
        }, ct);

        var result = new List<GoogleAccountView>();
        foreach (var (account, state, calendars) in rows)
        {
            result.Add(new GoogleAccountView(account, await secrets.GetAsync(SecretKey(account.Id), ct) is not null, state, calendars));
        }

        return result;
    }

    /// <summary>Signs in through the system browser. A new account is added; an account the person already has (here or from another device) just gets its token.</summary>
    public async Task<GoogleAccountView> ConnectAsync(CancellationToken ct = default)
    {
        GoogleSignIn signIn;
        try
        {
            signIn = await google.SignInAsync(ct);
        }
        catch (GoogleCalendarException error)
        {
            throw Translate(error);
        }

        var id = AccountId(signIn.Subject);
        await secrets.SetAsync(SecretKey(id), signIn.RefreshToken, ct);
        var now = clock.GetUtcNow();
        try
        {
            await store.ExecuteAsync(async (session, token) =>
            {
                var existing = await session.Calendars.GetAccountAsync(id, token);
                if (existing is null)
                {
                    await session.Calendars.AddAccountAsync(new GoogleAccountRecord
                    {
                        Id = id,
                        Email = signIn.Email,
                        Subject = signIn.Subject,
                        Version = 1,
                        CreatedAtUtc = now,
                        UpdatedAtUtc = now,
                    }, token);
                }
                else if (existing.DeletedAtUtc is not null || existing.Email != signIn.Email)
                {
                    // Signing in again after a disconnect brings the account back; a changed address is followed.
                    await session.Calendars.UpdateAccountAsync(existing with { Email = signIn.Email, DeletedAtUtc = null, Version = existing.Version + 1, UpdatedAtUtc = now }, existing.Version, token);
                }

                await session.Calendars.SaveAccountStateAsync(new GoogleAccountState(id, false, now, now, null), token);
                return 0;
            }, ct);
        }
        catch
        {
            await secrets.DeleteAsync(SecretKey(id), CancellationToken.None);
            throw;
        }

        return (await ListAsync(ct)).First(view => view.Account.Id == id);
    }

    /// <summary>Lists every calendar of the account with the person's current choice for each.</summary>
    public async Task<IReadOnlyList<GoogleCalendarChoice>> CalendarsAsync(string accountId, CancellationToken ct = default)
    {
        var account = await RequireAccountAsync(accountId, ct);
        var refreshToken = await secrets.GetAsync(SecretKey(account.Id), ct)
            ?? throw new AppException(AppErrorKind.UnsupportedCapability, SignInHereMessage);
        IReadOnlyList<GoogleCalendarInfo> infos;
        try
        {
            infos = await google.ListCalendarsAsync(refreshToken, ct);
        }
        catch (GoogleCalendarException error)
        {
            await RecordFailureAsync(account.Id, error, ct);
            throw Translate(error);
        }

        var known = await store.QueryAsync(async (session, token) =>
        {
            var result = new Dictionary<string, CalendarSourceRecord>(StringComparer.Ordinal);
            foreach (var info in infos)
            {
                if (await session.Calendars.GetSourceAsync(SourceId(account.Id, info.Id), token) is { DeletedAtUtc: null } source)
                {
                    result[info.Id] = source;
                }
            }

            return result;
        }, ct);
        return [.. infos.Select(info => new GoogleCalendarChoice(info, known.GetValueOrDefault(info.Id)))];
    }

    /// <summary>
    /// Chooses or drops one calendar. The first choice creates the source (colour from Google when the person gave none);
    /// dropping it only switches it off, so the colour and busy choice are remembered. The id is derived from the account and
    /// the calendar, so two devices that choose the same calendar offline end up with one source.
    /// </summary>
    public async Task<CalendarSourceView> ChooseAsync(ChooseGoogleCalendarCommand command, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var account = await RequireAccountAsync(command.AccountId, ct);
        if (string.IsNullOrWhiteSpace(command.CalendarId) || command.CalendarId.Length > 500)
        {
            throw AppException.Validation("calendarId", "Choose a calendar.");
        }

        var name = (command.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            name = command.CalendarId;
        }

        name = name.Length > 120 ? name[..120] : name;
        var color = System.Text.RegularExpressions.Regex.IsMatch(command.Color ?? string.Empty, "^#[0-9a-fA-F]{6}$") ? command.Color!.ToLowerInvariant() : "#3b82f6";
        var id = SourceId(account.Id, command.CalendarId);
        return await store.ExecuteAsync(async (session, token) =>
        {
            var now = clock.GetUtcNow();
            var existing = await session.Calendars.GetSourceAsync(id, token);
            CalendarSourceRecord saved;
            if (existing is null)
            {
                if ((await session.Calendars.ListSourcesAsync(token)).Count >= CalendarCommands.MaxSources)
                {
                    throw AppException.Validation("calendarId", $"Chaos shows at most {CalendarCommands.MaxSources} calendars. Remove one first.");
                }

                var sources = await session.Calendars.ListSourcesAsync(token);
                saved = new CalendarSourceRecord
                {
                    Id = id,
                    Provider = CalendarProvider.Google,
                    AccountRef = account.Id,
                    ExternalId = command.CalendarId,
                    Name = name,
                    Color = color,
                    CountsAsBusy = true,
                    Enabled = command.Chosen,
                    OwnerEmail = null,
                    Position = sources.Count == 0 ? 0 : sources.Max(item => item.Position) + 1,
                    Version = 1,
                    CreatedAtUtc = now,
                    UpdatedAtUtc = now,
                };
                await session.Calendars.AddSourceAsync(saved, token);
            }
            else
            {
                if (existing.DeletedAtUtc is null && existing.Enabled == command.Chosen)
                {
                    return new CalendarSourceView(existing, await session.Calendars.GetStateAsync(id, token), 0);
                }

                saved = await session.Calendars.UpdateSourceAsync(existing with
                {
                    Enabled = command.Chosen,
                    DeletedAtUtc = null,
                    Version = existing.Version + 1,
                    UpdatedAtUtc = now,
                }, existing.Version, token) ?? throw AppException.VersionConflict("That calendar changed elsewhere. Refresh and try again.");
            }

            if (!command.Chosen)
            {
                await session.Calendars.DeleteEventsAsync(id, token);
            }

            return new CalendarSourceView(saved, await session.Calendars.GetStateAsync(id, token), 0);
        }, ct);
    }

    /// <summary>
    /// Disconnects the account on every device: its calendars and the account row are removed (synced), the cached meetings
    /// go, the token is revoked at Google and removed from the secret store. A revoke that cannot reach Google does not stop
    /// the disconnect; the token is gone from this device either way.
    /// </summary>
    /// <remarks>
    /// When time blocking uses this account it is turned off too (PLAN3 §11): with <paramref name="deleteTimeBlockCalendar"/>
    /// (the default) the Chaos calendar is deleted in Google first while the token still works, and the event rows go.
    /// A delete that cannot reach Google does not stop the disconnect.
    /// </remarks>
    public async Task<bool> DisconnectAsync(string accountId, CancellationToken ct = default, bool deleteTimeBlockCalendar = true)
    {
        ct.ThrowIfCancellationRequested();
        var refreshToken = await secrets.GetAsync(SecretKey(accountId), ct);
        var timeBlockCalendar = await store.QueryAsync(async (session, token) =>
            await session.Profiles.GetPrimaryAsync(token) is { TimeBlockAccountId: { } owner, TimeBlockCalendarId: { } calendar } && owner == accountId ? calendar : null, ct);
        if (deleteTimeBlockCalendar && timeBlockCalendar is not null && refreshToken is not null)
        {
            try
            {
                await google.DeleteCalendarAsync(refreshToken, timeBlockCalendar, ct);
            }
            catch (GoogleCalendarException error)
            {
                // Best effort, like the revoke below: the account is disconnected either way, and the calendar stays in Google.
                System.Diagnostics.Trace.TraceWarning("The Chaos calendar could not be deleted while disconnecting: {0}", error.Kind);
            }
        }

        var found = await store.ExecuteAsync(async (session, token) =>
        {
            var account = await session.Calendars.GetAccountAsync(accountId, token);
            if (account is null || account.DeletedAtUtc is not null)
            {
                return false;
            }

            var now = clock.GetUtcNow();
            if (await session.Profiles.GetPrimaryAsync(token) is { TimeBlockAccountId: { } owner } profile && owner == accountId)
            {
                var off = profile with
                {
                    TimeBlockEnabled = false, TimeBlockRunnerDeviceId = null, TimeBlockSpacesJson = null, TimeBlockAccountId = null, TimeBlockCalendarId = null,
                    Version = profile.Version + 1, UpdatedAtUtc = now,
                };
                if (await session.Profiles.UpdateTimeBlockSettingsAsync(off, profile.Version, token) is null)
                {
                    throw AppException.VersionConflict("That setting changed elsewhere. Refresh and try again.");
                }

                foreach (var row in await session.TimeBlocks.ListEventsAsync(token))
                {
                    await session.TimeBlocks.DeleteEventAsync(row.Id, token);
                }

                await session.TimeBlocks.SaveStateAsync(new TimeBlockState(null, null, null, null, null, false), token);
            }

            foreach (var source in (await session.Calendars.ListSourcesAsync(token)).Where(item => item.Provider == CalendarProvider.Google && item.AccountRef == accountId))
            {
                await session.Calendars.UpdateSourceAsync(source with { DeletedAtUtc = now, UpdatedAtUtc = now, Version = source.Version + 1 }, source.Version, token);
                await session.Calendars.DeleteEventsAsync(source.Id, token);
            }

            await session.Calendars.UpdateAccountAsync(account with { DeletedAtUtc = now, UpdatedAtUtc = now, Version = account.Version + 1 }, account.Version, token);
            return true;
        }, ct);

        var revoked = true;
        if (refreshToken is not null)
        {
            try
            {
                revoked = await google.RevokeAsync(refreshToken, ct);
            }
            catch (GoogleCalendarException)
            {
                revoked = false;
            }
        }

        await secrets.DeleteAsync(SecretKey(accountId), CancellationToken.None);
        return found && revoked;
    }

    /// <summary>Records a failure on the account: "sign in again" is a state of its own, other failures are only a message.</summary>
    internal async Task RecordFailureAsync(string accountId, GoogleCalendarException error, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var message = MessageOf(error);
        await store.ExecuteAsync(async (session, token) =>
        {
            if (await session.Calendars.GetAccountAsync(accountId, token) is not { DeletedAtUtc: null })
            {
                return 0;
            }

            var previous = await session.Calendars.GetAccountStateAsync(accountId, token);
            await session.Calendars.SaveAccountStateAsync(new GoogleAccountState(accountId, error.Kind == GoogleFailure.SignInAgain, now, previous?.LastSuccessUtc, message), token);
            return 0;
        }, ct);
    }

    public static string MessageOf(GoogleCalendarException error) => error.Kind == GoogleFailure.SignInAgain ? SignInAgainMessage : error.Message;

    private static AppException Translate(GoogleCalendarException error) => error.Kind switch
    {
        GoogleFailure.NotConfigured => new AppException(AppErrorKind.UnsupportedCapability, error.Message),
        _ => AppException.Validation("google", MessageOf(error)),
    };

    private async Task<GoogleAccountRecord> RequireAccountAsync(string accountId, CancellationToken ct) =>
        await store.QueryAsync(async (session, token) => await session.Calendars.GetAccountAsync(accountId, token), ct) is { DeletedAtUtc: null } account
            ? account
            : throw AppException.NotFound("That Google account was disconnected. Refresh and try again.");
}
