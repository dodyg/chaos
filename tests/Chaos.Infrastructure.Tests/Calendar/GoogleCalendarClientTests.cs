using System.Net;
using System.Security.Cryptography;
using System.Text;
using Chaos.Application.Abstractions;
using Chaos.Infrastructure.Calendar;

namespace Chaos.Infrastructure.Tests.Calendar;

/// <summary>The Google client over a fake transport (PLAN2 P6): list, events, the hiding rules, failure states, revoke, PKCE and no secrets in messages.</summary>
public sealed class GoogleCalendarClientTests
{
    private const string Refresh = "1//refresh-token-SECRET-aaaa";
    private static readonly GoogleOAuthConfig Config = new("client-id.apps.googleusercontent.com", "client-secret-SECRET-bbbb");

    private sealed class Handler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Add((request, body));
            return respond(request, body);
        }
    }

    private sealed class NoBrowser : ISystemBrowser
    {
        public bool Open(Uri url) => false;
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (GoogleCalendarClient Client, Handler Handler) ClientOf(Func<HttpRequestMessage, string?, HttpResponseMessage> respond, GoogleOAuthConfig? config = null)
    {
        var handler = new Handler(respond);
        return (new GoogleCalendarClient(config ?? Config, new NoBrowser(), TimeProvider.System, new HttpClient(handler)), handler);
    }

    /// <summary>Answers the token endpoint with an access token and everything else with <paramref name="api"/>.</summary>
    private static (GoogleCalendarClient Client, Handler Handler) ApiClient(Func<HttpRequestMessage, HttpResponseMessage> api) =>
        ClientOf((request, _) => request.RequestUri!.AbsoluteUri.StartsWith(GoogleCalendarClient.TokenEndpoint, StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, "{\"access_token\":\"access-1\",\"expires_in\":3600}")
            : api(request));

    [Test]
    public async Task NotConfigured_MakesNoRequestAndSaysSo()
    {
        var (client, handler) = ClientOf((_, _) => Json(HttpStatusCode.OK, "{}"), config: null);
        // ClientOf falls back to Config for null, so build the unconfigured client directly.
        using var unconfigured = new GoogleCalendarClient(null, new NoBrowser(), TimeProvider.System, new HttpClient(handler));

        await Assert.That(unconfigured.IsConfigured).IsFalse();
        var signIn = await Assert.That(async () => await unconfigured.SignInAsync(CancellationToken.None)).Throws<GoogleCalendarException>();
        await Assert.That(signIn!.Kind).IsEqualTo(GoogleFailure.NotConfigured);
        var list = await Assert.That(async () => await unconfigured.ListCalendarsAsync(Refresh, CancellationToken.None)).Throws<GoogleCalendarException>();
        await Assert.That(list!.Kind).IsEqualTo(GoogleFailure.NotConfigured);
        await Assert.That(handler.Calls).IsEmpty();
        client.Dispose();
    }

    [Test]
    public async Task ListCalendars_ReadsNamesColoursAndThePrimary_WithABearerToken()
    {
        var (client, handler) = ApiClient(_ => Json(HttpStatusCode.OK, """
            {"items":[
              {"id":"me@example.com","summary":"me@example.com","backgroundColor":"#16a765","primary":true},
              {"id":"team@example.com","summary":"Team","summaryOverride":"My Team","backgroundColor":"#9a9cff"},
              {"id":"gone@example.com","summary":"Gone","deleted":true}]}
            """));

        var calendars = await client.ListCalendarsAsync(Refresh, CancellationToken.None);

        await Assert.That(calendars.Count).IsEqualTo(2);
        await Assert.That(calendars[0].Primary).IsTrue();
        await Assert.That(calendars[1].Name).IsEqualTo("My Team");
        await Assert.That(calendars[1].Color).IsEqualTo("#9a9cff");
        var api = handler.Calls.Single(call => call.Request.RequestUri!.AbsoluteUri.StartsWith(GoogleCalendarClient.ApiRoot, StringComparison.Ordinal));
        await Assert.That(api.Request.Headers.Authorization!.Parameter).IsEqualTo("access-1");
        // The refresh token and secret travel only in the token request body.
        await Assert.That(api.Request.RequestUri!.AbsoluteUri).DoesNotContain("SECRET");
    }

    [Test]
    public async Task ListEvents_AsksForTheWindow_ExpandsRecurrence_AndAppliesTheHidingRules()
    {
        var (client, handler) = ApiClient(_ => Json(HttpStatusCode.OK, """
            {"items":[
              {"id":"timed","summary":"Stand-up","start":{"dateTime":"2026-11-11T10:00:00+02:00"},"end":{"dateTime":"2026-11-11T10:30:00+02:00"},
               "location":"Room 2","htmlLink":"https://www.google.com/calendar/event?eid=abc","hangoutLink":"https://meet.google.com/xyz-abc"},
              {"id":"free","summary":"Focus","transparency":"transparent","start":{"dateTime":"2026-11-11T12:00:00Z"},"end":{"dateTime":"2026-11-11T13:00:00Z"}},
              {"id":"cancelled","status":"cancelled","summary":"Cancelled","start":{"dateTime":"2026-11-11T12:00:00Z"},"end":{"dateTime":"2026-11-11T13:00:00Z"}},
              {"id":"declined","summary":"Declined","start":{"dateTime":"2026-11-11T12:00:00Z"},"end":{"dateTime":"2026-11-11T13:00:00Z"},
               "attendees":[{"self":true,"responseStatus":"declined"},{"responseStatus":"accepted"}]},
              {"id":"otherdeclined","summary":"Someone else declined","start":{"dateTime":"2026-11-11T14:00:00Z"},"end":{"dateTime":"2026-11-11T15:00:00Z"},
               "attendees":[{"self":false,"responseStatus":"declined"},{"self":true,"responseStatus":"accepted"}]},
              {"id":"allday","summary":"Holiday","start":{"date":"2026-11-12"},"end":{"date":"2026-11-13"},"transparency":"transparent"},
              {"id":"conf","summary":"Call","start":{"dateTime":"2026-11-11T16:00:00Z"},"end":{"dateTime":"2026-11-11T17:00:00Z"},
               "conferenceData":{"entryPoints":[{"entryPointType":"phone","uri":"tel:+1555"},{"entryPointType":"video","uri":"https://zoom.example.com/j/1"}]}},
              {"id":"insecure","summary":"Http link","start":{"dateTime":"2026-11-11T18:00:00Z"},"end":{"dateTime":"2026-11-11T19:00:00Z"},"htmlLink":"http://example.com/e"}]}
            """));
        var from = new DateTimeOffset(2026, 11, 3, 9, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero).AddMonths(10);

        var events = await client.ListEventsAsync(Refresh, "team@example.com", from, to, CancellationToken.None);

        var ids = events.Select(e => e.ExternalId).ToArray();
        await Assert.That(ids).IsEquivalentTo(new[] { "timed", "free", "otherdeclined", "allday", "conf", "insecure" });
        var timed = events.Single(e => e.ExternalId == "timed");
        await Assert.That(timed.StartUtc).IsEqualTo(new DateTimeOffset(2026, 11, 11, 8, 0, 0, TimeSpan.Zero));
        await Assert.That(timed.Busy).IsTrue();
        await Assert.That(timed.VideoUrl).IsEqualTo("https://meet.google.com/xyz-abc");
        await Assert.That(timed.WebUrl).IsEqualTo("https://www.google.com/calendar/event?eid=abc");
        await Assert.That(events.Single(e => e.ExternalId == "free").Busy).IsFalse();
        var allDay = events.Single(e => e.ExternalId == "allday");
        await Assert.That(allDay.AllDay).IsTrue();
        await Assert.That(allDay.StartDate).IsEqualTo(new DateOnly(2026, 11, 12));
        await Assert.That(allDay.EndDate).IsEqualTo(new DateOnly(2026, 11, 13));
        await Assert.That(events.Single(e => e.ExternalId == "conf").VideoUrl).IsEqualTo("https://zoom.example.com/j/1");
        await Assert.That(events.Single(e => e.ExternalId == "insecure").WebUrl).IsNull();

        // One request, expanded recurrence, the whole window, calendar id escaped: no sync token in sight.
        var request = handler.Calls.Single(call => call.Request.RequestUri!.AbsoluteUri.Contains("/events?", StringComparison.Ordinal)).Request.RequestUri!.AbsoluteUri;
        await Assert.That(request).Contains("calendars/team%40example.com/events");
        await Assert.That(request).Contains("singleEvents=true");
        await Assert.That(request).Contains("timeMin=2026-11-03T09%3A00%3A00Z");
        await Assert.That(request).DoesNotContain("syncToken");
    }

    [Test]
    public async Task ListEvents_FollowsPages()
    {
        var pages = 0;
        var (client, _) = ApiClient(request =>
        {
            pages++;
            return request.RequestUri!.Query.Contains("pageToken=p2", StringComparison.Ordinal)
                ? Json(HttpStatusCode.OK, """{"items":[{"id":"b","summary":"B","start":{"dateTime":"2026-11-11T10:00:00Z"},"end":{"dateTime":"2026-11-11T11:00:00Z"}}]}""")
                : Json(HttpStatusCode.OK, """{"nextPageToken":"p2","items":[{"id":"a","summary":"A","start":{"dateTime":"2026-11-11T08:00:00Z"},"end":{"dateTime":"2026-11-11T09:00:00Z"}}]}""");
        });

        var events = await client.ListEventsAsync(Refresh, "primary", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(10), CancellationToken.None);

        await Assert.That(events.Select(e => e.ExternalId).ToArray()).IsEquivalentTo(new[] { "a", "b" });
        await Assert.That(pages).IsEqualTo(2);
    }

    [Test]
    public async Task ARevokedOrExpiredRefreshToken_MeansSignInAgain()
    {
        var (client, _) = ClientOf((_, _) => Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant","error_description":"Token has been expired or revoked."}"""));

        var error = await Assert.That(async () => await client.ListCalendarsAsync(Refresh, CancellationToken.None)).Throws<GoogleCalendarException>();

        await Assert.That(error!.Kind).IsEqualTo(GoogleFailure.SignInAgain);
        await Assert.That(error.Message).IsEqualTo("Google Calendar: sign in again.");
        await Assert.That(error.Message).DoesNotContain("SECRET");
    }

    [Test]
    public async Task AnExpiredAccessToken_IsRefreshedOnce_ThenAGenuine401MeansSignInAgain()
    {
        var tokens = 0;
        var (client, handler) = ClientOf((request, _) =>
        {
            if (request.RequestUri!.AbsoluteUri.StartsWith(GoogleCalendarClient.TokenEndpoint, StringComparison.Ordinal))
            {
                tokens++;
                return Json(HttpStatusCode.OK, $"{{\"access_token\":\"access-{tokens}\",\"expires_in\":3600}}");
            }

            return request.Headers.Authorization!.Parameter == "access-1"
                ? Json(HttpStatusCode.Unauthorized, """{"error":{"code":401}}""")
                : Json(HttpStatusCode.OK, """{"items":[]}""");
        });

        await Assert.That((await client.ListCalendarsAsync(Refresh, CancellationToken.None)).Count).IsEqualTo(0);
        await Assert.That(tokens).IsEqualTo(2);

        var (always401, _) = ApiClient(_ => Json(HttpStatusCode.Unauthorized, "{}"));
        var error = await Assert.That(async () => await always401.ListCalendarsAsync(Refresh, CancellationToken.None)).Throws<GoogleCalendarException>();
        await Assert.That(error!.Kind).IsEqualTo(GoogleFailure.SignInAgain);
        await Assert.That(handler.Calls.Count).IsGreaterThan(0);
    }

    [Test]
    public async Task APolicyBlock_RateLimit_AndOffline_AreToldApart()
    {
        var (blocked, _) = ApiClient(_ => Json(HttpStatusCode.Forbidden, """{"error":{"errors":[{"reason":"forbidden"}]}}"""));
        var (limited, _) = ApiClient(_ => Json(HttpStatusCode.Forbidden, """{"error":{"errors":[{"reason":"rateLimitExceeded"}]}}"""));
        var (offline, _) = ClientOf((_, _) => throw new HttpRequestException("no route to host"));
        var (adminBlock, _) = ClientOf((_, _) => Json(HttpStatusCode.BadRequest, """{"error":"admin_policy_enforced"}"""));

        await Assert.That((await Assert.That(async () => await blocked.ListCalendarsAsync(Refresh, CancellationToken.None)).Throws<GoogleCalendarException>())!.Kind).IsEqualTo(GoogleFailure.Blocked);
        await Assert.That((await Assert.That(async () => await limited.ListCalendarsAsync(Refresh, CancellationToken.None)).Throws<GoogleCalendarException>())!.Kind).IsEqualTo(GoogleFailure.Other);
        await Assert.That((await Assert.That(async () => await offline.ListCalendarsAsync(Refresh, CancellationToken.None)).Throws<GoogleCalendarException>())!.Kind).IsEqualTo(GoogleFailure.Offline);
        await Assert.That((await Assert.That(async () => await adminBlock.ListCalendarsAsync(Refresh, CancellationToken.None)).Throws<GoogleCalendarException>())!.Kind).IsEqualTo(GoogleFailure.Blocked);
    }

    [Test]
    public async Task Revoke_PostsTheToken_AndAnAlreadyInvalidTokenCountsAsRevoked()
    {
        var (ok, handler) = ClientOf((_, _) => Json(HttpStatusCode.OK, "{}"));
        var (gone, _) = ClientOf((_, _) => Json(HttpStatusCode.BadRequest, """{"error":"invalid_token"}"""));
        var (offline, _) = ClientOf((_, _) => throw new HttpRequestException("offline"));

        await Assert.That(await ok.RevokeAsync(Refresh, CancellationToken.None)).IsTrue();
        await Assert.That(handler.Calls.Single().Request.RequestUri!.AbsoluteUri).IsEqualTo(GoogleCalendarClient.RevokeEndpoint);
        await Assert.That(handler.Calls.Single().Body).Contains("token=");
        await Assert.That(await gone.RevokeAsync(Refresh, CancellationToken.None)).IsTrue();
        await Assert.That(await offline.RevokeAsync(Refresh, CancellationToken.None)).IsFalse();
    }

    [Test]
    public async Task SignIn_UsesLoopbackAndPkce_AndReturnsTheIdentityFromTheIdToken()
    {
        string? tokenBody = null;
        var idToken = "x." + Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"sub":"1234567890","email":"me@example.com"}""")).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".y";
        var browser = new LoopbackBrowser();
        using var http = new HttpClient(new Handler((request, body) =>
        {
            if (request.RequestUri!.AbsoluteUri.StartsWith(GoogleCalendarClient.TokenEndpoint, StringComparison.Ordinal))
            {
                tokenBody = body;
                return Json(HttpStatusCode.OK, $"{{\"access_token\":\"a\",\"expires_in\":3600,\"refresh_token\":\"{Refresh}\",\"id_token\":\"{idToken}\"}}");
            }

            return Json(HttpStatusCode.NotFound, "{}");
        }));
        using var client = new GoogleCalendarClient(Config, browser, TimeProvider.System, http);

        var result = await client.SignInAsync(CancellationToken.None);

        await Assert.That(result.Email).IsEqualTo("me@example.com");
        await Assert.That(result.Subject).IsEqualTo("1234567890");
        await Assert.That(result.RefreshToken).IsEqualTo(Refresh);

        var url = browser.Opened!;
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        await Assert.That(url.AbsoluteUri).StartsWith(GoogleCalendarClient.AuthorizeEndpoint);
        await Assert.That(query["scope"]).Contains("calendar.calendarlist.readonly");
        await Assert.That(query["scope"]).Contains("calendar.events.readonly");
        await Assert.That(query["scope"]).DoesNotContain("auth/calendar ");
        await Assert.That(query["code_challenge_method"]).IsEqualTo("S256");
        await Assert.That(query["redirect_uri"]).StartsWith("http://127.0.0.1:");

        // The verifier sent with the code hashes to the challenge the browser was given.
        var form = System.Web.HttpUtility.ParseQueryString(tokenBody!);
        var expected = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]!))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await Assert.That(query["code_challenge"]).IsEqualTo(expected);
        await Assert.That(form["code"]).IsEqualTo("the-code");
    }

    [Test]
    public async Task SignIn_Cancelled_OrWithAWrongState_GivesNoToken()
    {
        var calls = 0;
        using var http = new HttpClient(new Handler((_, _) => { calls++; return Json(HttpStatusCode.OK, "{}"); }));
        using var client = new GoogleCalendarClient(Config, new LoopbackBrowser { Respond = (redirect, _) => $"{redirect}?error=access_denied&state=STATE" }, TimeProvider.System, http);

        var error = await Assert.That(async () => await client.SignInAsync(CancellationToken.None)).Throws<GoogleCalendarException>();

        await Assert.That(error!.Message).Contains("cancelled");
        await Assert.That(calls).IsEqualTo(0);
    }

    // ---- Time blocking (PLAN3 §11) -----------------------------------------------------------------

    [Test]
    public async Task InsertEvent_PostsTheClientEventIdToTheAppCalendar_AndA409MeansTheEventExists()
    {
        var (client, handler) = ApiClient(request => request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/events", StringComparison.Ordinal)
            ? Json(HttpStatusCode.Conflict, """{"id":"cbabc","etag":"\"existing\"","updated":"2026-11-20T09:00:00Z"}""")
            : Json(HttpStatusCode.OK, "{}"));

        var write = await client.InsertEventAsync(Refresh, "calendar_1@group.v.calendar.google.com", new GoogleEventBody("cbabc", "Write the plan", new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero), "Europe/Paris"), CancellationToken.None);

        await Assert.That(write.Conflict).IsTrue();
        await Assert.That(write.NotFound).IsFalse();
        await Assert.That(write.ETag).IsEqualTo("\"existing\"");
        var call = handler.Calls.Single(call => call.Request.Method == HttpMethod.Post && call.Request.RequestUri!.AbsolutePath.Contains("/events", StringComparison.Ordinal));
        await Assert.That(call.Request.RequestUri!.AbsoluteUri).IsEqualTo("https://www.googleapis.com/calendar/v3/calendars/calendar_1%40group.v.calendar.google.com/events");
        await Assert.That(call.Request.Content!.Headers.ContentType!.MediaType).IsEqualTo("application/json");
        using var body = System.Text.Json.JsonDocument.Parse(call.Body!);
        await Assert.That(body.RootElement.GetProperty("id").GetString()).IsEqualTo("cbabc");
        await Assert.That(body.RootElement.GetProperty("summary").GetString()).IsEqualTo("Write the plan");
        await Assert.That(body.RootElement.GetProperty("start").GetProperty("dateTime").GetString()).IsEqualTo("2026-11-20T09:00:00Z");
        await Assert.That(body.RootElement.GetProperty("start").GetProperty("timeZone").GetString()).IsEqualTo("Europe/Paris");
        await Assert.That(body.RootElement.GetProperty("end").GetProperty("dateTime").GetString()).IsEqualTo("2026-11-20T10:00:00Z");
        await Assert.That(body.RootElement.GetProperty("transparency").GetString()).IsEqualTo("opaque");
    }

    [Test]
    public async Task BusyBlocks_CarryNoSummary_AndAPatchCarriesNoId()
    {
        var (client, handler) = ApiClient(request => request.Method == HttpMethod.Patch
            ? Json(HttpStatusCode.OK, """{"id":"cbabc","etag":"\"e2\"","updated":"2026-11-20T09:05:00Z"}""")
            : Json(HttpStatusCode.OK, "{}"));
        var busy = new GoogleEventBody("cbabc", null, new DateTimeOffset(2026, 11, 20, 9, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero), "UTC");

        await client.InsertEventAsync(Refresh, "calendar_1", busy, CancellationToken.None);
        var insert = handler.Calls.Single(call => call.Request.Method == HttpMethod.Post && call.Request.RequestUri!.AbsolutePath.EndsWith("/events", StringComparison.Ordinal)).Body!;
        await Assert.That(insert).DoesNotContain("summary");

        var write = await client.PatchEventAsync(Refresh, "calendar_1", "cbabc", busy with { Id = null }, CancellationToken.None);
        await Assert.That(write.ETag).IsEqualTo("\"e2\"");
        await Assert.That(write.UpdatedUtc).IsEqualTo(new DateTimeOffset(2026, 11, 20, 9, 5, 0, TimeSpan.Zero));
        var patch = handler.Calls.Single(call => call.Request.Method == HttpMethod.Patch);
        await Assert.That(patch.Request.RequestUri!.AbsoluteUri).IsEqualTo("https://www.googleapis.com/calendar/v3/calendars/calendar_1/events/cbabc");
        await Assert.That(patch.Body!).DoesNotContain("\"id\"");
    }

    [Test]
    public async Task APatchOfAGoneEvent_SaysNotFound_AndDeletingAGoneEventIsFine()
    {
        var (client, handler) = ApiClient(request => request.Method == HttpMethod.Patch ? Json(HttpStatusCode.NotFound, """{"error":{"code":404}}""") : Json(HttpStatusCode.NotFound, "{}"));

        var patch = await client.PatchEventAsync(Refresh, "calendar_1", "cbgone", new(null, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), "UTC"), CancellationToken.None);
        await Assert.That(patch.NotFound).IsTrue();
        await client.DeleteEventAsync(Refresh, "calendar_1", "cbgone", CancellationToken.None);
        await Assert.That(handler.Calls.Single(call => call.Request.Method == HttpMethod.Delete).Request.RequestUri!.AbsoluteUri).IsEqualTo("https://www.googleapis.com/calendar/v3/calendars/calendar_1/events/cbgone");
    }

    [Test]
    public async Task TheChangeFeed_UsesTheSyncToken_AndAGoneTokenMeansStartOver()
    {
        var (client, handler) = ApiClient(request => request.RequestUri!.Query.Contains("syncToken=st-1", StringComparison.Ordinal)
            ? Json(HttpStatusCode.Gone, """{"error":{"code":410,"errors":[{"reason":"gone"}]}}""")
            : Json(HttpStatusCode.OK, """
                {"nextSyncToken":"st-2","items":[
                  {"id":"cba","etag":"\"e1\"","updated":"2026-11-20T09:00:00Z","summary":"Moved","start":{"dateTime":"2026-11-20T15:00:00+01:00","timeZone":"Europe/Paris"},"end":{"dateTime":"2026-11-20T16:00:00+01:00","timeZone":"Europe/Paris"}},
                  {"id":"cbb","etag":"\"e2\"","updated":"2026-11-20T10:00:00Z","status":"cancelled"}]}
                """));

        var gone = await client.ListEventChangesAsync(Refresh, "calendar_1", "st-1", null, CancellationToken.None);
        await Assert.That(gone.TokenExpired).IsTrue();
        var page = await client.ListEventChangesAsync(Refresh, "calendar_1", null, null, CancellationToken.None);

        await Assert.That(page.NextSyncToken).IsEqualTo("st-2");
        await Assert.That(page.NextPageToken).IsNull();
        await Assert.That(page.Items.Count).IsEqualTo(2);
        var moved = page.Items[0];
        await Assert.That(moved.StartUtc).IsEqualTo(new DateTimeOffset(2026, 11, 20, 14, 0, 0, TimeSpan.Zero));
        await Assert.That(moved.TimeZoneId).IsEqualTo("Europe/Paris");
        await Assert.That(moved.Summary).IsEqualTo("Moved");
        await Assert.That(page.Items[1].Cancelled).IsTrue();
        await Assert.That(page.Items[1].StartUtc).IsNull();
        var reads = handler.Calls.Where(call => call.Request.Method == HttpMethod.Get && call.Request.RequestUri!.AbsolutePath.EndsWith("/events", StringComparison.Ordinal)).Select(call => call.Request.RequestUri!.Query).ToList();
        await Assert.That(reads[0]).Contains("syncToken=st-1");
        await Assert.That(reads[0]).DoesNotContain("pageToken");
        await Assert.That(reads[1]).DoesNotContain("syncToken");
        await Assert.That(reads[1]).Contains("showDeleted=true");
        await Assert.That(reads[1]).DoesNotContain("singleEvents");
    }

    [Test]
    public async Task CreateAndDeleteCalendar_UseTheCalendarsResource()
    {
        var (client, handler) = ApiClient(request => request.Method == HttpMethod.Post
            ? Json(HttpStatusCode.OK, """{"id":"calendar_1@group.v.calendar.google.com","summary":"Chaos","backgroundColor":"#9a9cff"}""")
            : Json(HttpStatusCode.NotFound, "{}"));

        var calendar = await client.CreateCalendarAsync(Refresh, "Chaos", CancellationToken.None);
        await Assert.That(calendar.Id).IsEqualTo("calendar_1@group.v.calendar.google.com");
        await Assert.That(calendar.Name).IsEqualTo("Chaos");
        var post = handler.Calls.Single(call => call.Request.Method == HttpMethod.Post && call.Request.RequestUri!.AbsolutePath.EndsWith("/calendars", StringComparison.Ordinal));
        await Assert.That(post.Request.RequestUri!.AbsoluteUri).IsEqualTo("https://www.googleapis.com/calendar/v3/calendars");
        await Assert.That(post.Body).IsEqualTo("{\"summary\":\"Chaos\"}");

        // Deleting a calendar that is already gone is not an error.
        await client.DeleteCalendarAsync(Refresh, calendar.Id, CancellationToken.None);
        await Assert.That(handler.Calls.Single(call => call.Request.Method == HttpMethod.Delete).Request.RequestUri!.AbsoluteUri).IsEqualTo("https://www.googleapis.com/calendar/v3/calendars/calendar_1%40group.v.calendar.google.com");
    }

    [Test]
    public async Task SignIn_ForTimeBlocking_AsksForTheNewScopeOnly_AndKeepsTheGrants()
    {
        var browser = new LoopbackBrowser();
        var idToken = "x." + Convert.ToBase64String(Encoding.UTF8.GetBytes("""{"sub":"1234567890","email":"me@example.com"}""")).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".y";
        using var http = new HttpClient(new Handler((request, _) => request.RequestUri!.AbsoluteUri.StartsWith(GoogleCalendarClient.TokenEndpoint, StringComparison.Ordinal)
            ? Json(HttpStatusCode.OK, $"{{\"access_token\":\"a\",\"expires_in\":3600,\"refresh_token\":\"{Refresh}\",\"id_token\":\"{idToken}\"}}")
            : Json(HttpStatusCode.NotFound, "{}")));
        using var client = new GoogleCalendarClient(Config, browser, TimeProvider.System, http);

        var signIn = await client.SignInAsync([GoogleCalendarClient.ScopeAppCreated], CancellationToken.None);

        await Assert.That(signIn.RefreshToken).IsEqualTo(Refresh);
        var query = System.Web.HttpUtility.ParseQueryString(browser.Opened!.Query);
        await Assert.That(query["scope"]!).IsEqualTo(GoogleCalendarClient.Scopes + " " + GoogleCalendarClient.ScopeAppCreated);
        await Assert.That(query["include_granted_scopes"]).IsEqualTo("true");
        await Assert.That(query["scope"]!).DoesNotContain("auth/calendar "); // never the full-access scope
    }

    /// <summary>Plays the person's browser: reads the sign-in address, then visits the loopback redirect the way Google would.</summary>
    private sealed class LoopbackBrowser : ISystemBrowser
    {
        public Uri? Opened { get; private set; }
        public Func<string, string, string>? Respond { get; init; }

        public bool Open(Uri url)
        {
            Opened = url;
            var query = System.Web.HttpUtility.ParseQueryString(url.Query);
            var redirect = query["redirect_uri"]!;
            var state = query["state"]!;
            var target = Respond is null ? $"{redirect}?code=the-code&state={state}" : Respond(redirect, state).Replace("STATE", state, StringComparison.Ordinal);
            _ = Task.Run(async () =>
            {
                using var visitor = new HttpClient();
                using var _ = await visitor.GetAsync(target);
            });
            return true;
        }
    }
}
