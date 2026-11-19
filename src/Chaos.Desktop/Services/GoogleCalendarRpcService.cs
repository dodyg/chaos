using Chaos.Application.Activity;
using Chaos.Application.Abstractions;
using Chaos.Application.Calendar;
using Chaos.Application.Contracts;
using NeoAstra.Rpc;

namespace Chaos.Desktop.Services;

/// <summary>
/// A connected Google account. Never contains a token. <c>SignedInHere</c> is false on a device that knows the account from
/// sync but has not signed in on it yet; <c>Error</c> is the plain-words state ("Google Calendar: sign in again.").
/// </summary>
public sealed record GoogleAccountDto(
    string Id,
    string Email,
    bool SignedInHere,
    bool NeedsSignIn,
    string? Error,
    string? LastSuccessUtc,
    IReadOnlyList<CalendarSourceDto> Calendars);
public sealed record GoogleStatusRequest();
public sealed record GoogleStatusResponse(bool Ok, bool Configured, IReadOnlyList<GoogleAccountDto> Accounts, TaskErrorResponse? Error);
public sealed record GoogleConnectRequest();
public sealed record GoogleSetupRequest(string ClientId, string ClientSecret);
public sealed record GoogleAccountResponse(bool Ok, GoogleAccountDto? Account, TaskErrorResponse? Error);
public sealed record GoogleCalendarsRequest(string AccountId);
/// <summary>Color is Google's own colour for the calendar; Source is set once the calendar was chosen at least once, and Chosen says whether it is on now.</summary>
public sealed record GoogleCalendarChoiceDto(string CalendarId, string Name, string? Color, bool Primary, bool Chosen, CalendarSourceDto? Source);
public sealed record GoogleCalendarsResponse(bool Ok, IReadOnlyList<GoogleCalendarChoiceDto> Calendars, TaskErrorResponse? Error);
public sealed record GoogleChooseRequest(string AccountId, string CalendarId, string Name, string? Color, bool Chosen);
/// <summary>RefreshError is set when the calendar was chosen but its first fetch failed; the choice is kept.</summary>
public sealed record GoogleChooseResponse(bool Ok, CalendarSourceDto? Source, string? RefreshError, TaskErrorResponse? Error);
public sealed record GoogleDisconnectRequest(string AccountId);
/// <summary>Revoked is false when Google could not be reached to revoke the token; the account is gone from Chaos either way.</summary>
public sealed record GoogleDisconnectResponse(bool Ok, bool Revoked, TaskErrorResponse? Error);

/// <summary>Google Calendar accounts and calendar choices (PLAN2 P6). Errors are returned as data.</summary>
[NeoRpcService("googleCalendar", Version = 1)]
public sealed class GoogleCalendarRpcService(GoogleCalendarCommands commands, CalendarRefresher refresher)
{
    [NeoRpcMethod("status")]
    public ValueTask<GoogleStatusResponse> StatusAsync(GoogleStatusRequest request, CancellationToken ct) =>
        Rpc.Run<GoogleStatusResponse>("googleCalendar.status", async () => { return new(true, commands.IsConfigured, [.. (await commands.ListAsync(ct)).Select(Map)], null); },
            error => new(false, commands.IsConfigured, [], error));

    /// <summary>Saves the pasted OAuth client; the secret is stored protected and never returned.</summary>
    [NeoRpcMethod("setup")]
    public ValueTask<GoogleDisconnectResponse> SetupAsync(GoogleSetupRequest request, CancellationToken ct) =>
        Rpc.Run<GoogleDisconnectResponse>("googleCalendar.setup", async () => { await commands.SaveClientAsync(request.ClientId, request.ClientSecret, ct); return new(true, true, null); },
            error => new(false, false, error));

    [NeoRpcMethod("connect")]
    public ValueTask<GoogleAccountResponse> ConnectAsync(GoogleConnectRequest request, CancellationToken ct) =>
        Rpc.Run<GoogleAccountResponse>("googleCalendar.connect", async () => { return new(true, Map(await commands.ConnectAsync(ct)), null); },
            error => new(false, null, error));

    [NeoRpcMethod("calendars")]
    public ValueTask<GoogleCalendarsResponse> CalendarsAsync(GoogleCalendarsRequest request, CancellationToken ct) =>
        Rpc.Run<GoogleCalendarsResponse>("googleCalendar.calendars", async () => {
            var choices = await commands.CalendarsAsync(request.AccountId, ct);
            return new(true, [.. choices.Select(choice => new GoogleCalendarChoiceDto(choice.Info.Id, choice.Info.Name, choice.Info.Color, choice.Info.Primary,
                choice.Source is { Enabled: true }, choice.Source is null ? null : CalendarsRpcService.Map(new CalendarSourceView(choice.Source, null, 0))))], null);
        },
            error => new(false, [], error));

    [NeoRpcMethod("choose")]
    public ValueTask<GoogleChooseResponse> ChooseAsync(GoogleChooseRequest request, CancellationToken ct) =>
        Rpc.Run<GoogleChooseResponse>("googleCalendar.choose", async () => {
            var chosen = await commands.ChooseAsync(new ChooseGoogleCalendarCommand(request.AccountId, request.CalendarId, request.Name, request.Color, request.Chosen), ct);
            string? refreshError = null;
            var current = chosen;
            if (request.Chosen)
            {
                var outcome = (await refresher.RefreshAsync(chosen.Source.Id, ct)).FirstOrDefault();
                refreshError = outcome is { Ok: false } ? outcome.Error : null;
                current = (await commands.ListAsync(ct)).SelectMany(account => account.Calendars).FirstOrDefault(item => item.Source.Id == chosen.Source.Id) ?? chosen;
            }

            return new(true, CalendarsRpcService.Map(current), refreshError, null);
        },
            error => new(false, null, null, error));

    [NeoRpcMethod("disconnect")]
    public ValueTask<GoogleDisconnectResponse> DisconnectAsync(GoogleDisconnectRequest request, CancellationToken ct) =>
        Rpc.Run<GoogleDisconnectResponse>("googleCalendar.disconnect", async () => { return new(true, await commands.DisconnectAsync(request.AccountId, ct), null); },
            error => new(false, false, error));

    private static GoogleAccountDto Map(GoogleAccountView view) => new(
        view.Account.Id, view.Account.Email, view.SignedInHere, view.State?.NeedsSignIn ?? false,
        view.SignedInHere ? view.State?.LastError : GoogleCalendarCommands.SignInHereMessage,
        view.State?.LastSuccessUtc is { } success ? StorageFormats.Instant(success) : null,
        [.. view.Calendars.Select(CalendarsRpcService.Map)]);
}
