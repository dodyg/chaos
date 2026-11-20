using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chaos.Application.Abstractions;

namespace Chaos.Infrastructure.Service;

/// <summary>
/// The online service over HTTP (PLAN2 §8.4, P12 item 5): authorization code + PKCE through the system browser and a loopback
/// redirect, a refresh token that rotates on every use and lives only in <see cref="ISecretStore"/>, and access tokens that stay
/// in memory. Refreshing is single-flight, so concurrent callers share one refresh (the server revokes a refresh token that is
/// used twice). No message or exception here contains a token, a code or a verifier.
/// </summary>
public sealed class ChaosServiceClient : IChaosServiceClient, IBlobTransport, IDisposable
{
    public const string ClientId = "chaos-desktop";
    public const string RefreshKey = "chaos/service/refresh";
    public const string EmailKey = "chaos/service/email";
    public const string CallbackPath = "/callback";
    public const string Scope = "offline_access chaos";

    private static readonly TimeSpan ExpirySkew = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Func<ChaosServiceOptions?> _options;
    private readonly ISystemBrowser _browser;
    private readonly ISecretStore _secrets;
    private readonly IDeviceIdentity _identity;
    private readonly TimeProvider _clock;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly Lock _cacheGate = new();
    private string? _accessToken;
    private DateTimeOffset _accessExpires;

    public ChaosServiceClient(ChaosServiceOptions? options, ISystemBrowser browser, ISecretStore secrets, IDeviceIdentity identity, TimeProvider? clock = null, HttpClient? http = null)
        : this(() => options, browser, secrets, identity, clock, http)
    {
    }

    /// <summary>The address is asked for on every call, so a change made in Settings takes effect without a restart.</summary>
    public ChaosServiceClient(Func<ChaosServiceOptions?> options, ISystemBrowser browser, ISecretStore secrets, IDeviceIdentity identity, TimeProvider? clock = null, HttpClient? http = null)
    {
        _options = options;
        _browser = browser;
        _secrets = secrets;
        _identity = identity;
        _clock = clock ?? TimeProvider.System;
        _ownsClient = http is null;
        _http = http ?? new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>How long to wait for the person to finish in the browser (they may need to fetch an email code).</summary>
    public TimeSpan SignInTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public TimeSpan CallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    public bool IsConfigured => _options() is not null;

    public void Dispose()
    {
        _refreshGate.Dispose();
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }

    public async Task<ServiceSession> GetSessionAsync(CancellationToken ct)
    {
        if (_options() is not { } options)
        {
            return new ServiceSession(false, false, null, null);
        }

        var signedIn = await _secrets.GetAsync(RefreshKey, ct) is not null;
        return new ServiceSession(true, signedIn, signedIn ? await _secrets.GetAsync(EmailKey, ct) : null, options.BaseAddress.Authority);
    }

    // ---- Sign-in -------------------------------------------------------------------------------------

    public Task<ServiceAccount> SignInAsync(CancellationToken ct) => SignInCoreAsync(fresh: false, ct);

    public Task<ServiceAccount> ReauthenticateAsync(CancellationToken ct) => SignInCoreAsync(fresh: true, ct);

    private async Task<ServiceAccount> SignInCoreAsync(bool fresh, CancellationToken ct)
    {
        var options = Require();
        var device = await _identity.GetAsync(ct);
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(RandomNumberGenerator.GetBytes(16));

        using var redirect = new LoopbackRedirect(CallbackPath);
        var url = new Uri(options.BaseAddress, "connect/authorize?" + Query(
            ("client_id", ClientId), ("response_type", "code"), ("scope", Scope), ("redirect_uri", redirect.Uri.ToString()),
            ("state", state), ("code_challenge", challenge), ("code_challenge_method", "S256"), ("device_id", device.Id.ToString("D")),
            ("prompt", fresh ? "login" : "")));
        if (!_browser.Open(url))
        {
            throw new ChaosServiceException(ServiceFailure.Other, "Chaos could not open your web browser to sign in.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SignInTimeout);
        LoopbackResult result;
        try
        {
            result = await redirect.WaitAsync(state, "You are signed in. You can close this tab and return to Chaos.", timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ChaosServiceException(ServiceFailure.Other, "Sign-in timed out. Try again.");
        }

        if (result.Code is null)
        {
            throw new ChaosServiceException(ServiceFailure.Other,
                result.Error == "access_denied" ? "Sign-in was cancelled or refused." : "The service did not complete the sign-in.");
        }

        var response = await PostFormAsync("connect/token",
            [("grant_type", "authorization_code"), ("client_id", ClientId), ("code", result.Code), ("redirect_uri", redirect.Uri.ToString()), ("code_verifier", verifier)], ct);
        if (!response.Success)
        {
            throw new ChaosServiceException(ServiceFailure.Other, "The service did not accept the sign-in. Try again.");
        }

        await StoreTokensAsync(response.Body, ct);
        await RegisterDeviceAsync(device, ct);
        return await GetAccountAsync(ct);
    }

    public async Task SignOutAsync(CancellationToken ct)
    {
        // Tell the service first, while the token still exists: a revoked refresh token can never be used again, even if a copy
        // were kept. With no connection the token is simply forgotten here (best effort).
        if (_options() is not null && await _secrets.GetAsync(RefreshKey, ct) is { } refresh)
        {
            await TryRevokeAsync(refresh, ct);
        }

        await ForgetSignInAsync(ct);
    }

    private async Task ForgetSignInAsync(CancellationToken ct)
    {
        lock (_cacheGate)
        {
            _accessToken = null;
        }

        await _secrets.DeleteAsync(RefreshKey, ct);
        await _secrets.DeleteAsync(EmailKey, ct);
    }

    private async Task TryRevokeAsync(string refreshToken, CancellationToken ct)
    {
        try
        {
            using var shortCall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            shortCall.CancelAfter(TimeSpan.FromSeconds(5));
            await PostFormAsync("connect/revocation", [("client_id", ClientId), ("token", refreshToken), ("token_type_hint", "refresh_token")], shortCall.Token);
        }
        catch (ChaosServiceException error)
        {
            // Offline or unreachable: the token is removed from this computer either way.
            System.Diagnostics.Trace.TraceInformation("Could not revoke the signed-out device token ({0}).", error.Kind);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Took too long: same.
            System.Diagnostics.Trace.TraceInformation("Device token revocation timed out; local sign-out will continue.");
        }
    }

    // ---- Account and devices -------------------------------------------------------------------------

    public async Task<ServiceAccount> GetAccountAsync(CancellationToken ct)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Get, "v1/account", null, ct);
        await EnsureSuccessAsync(response, ct);
        var dto = await response.Content.ReadFromJsonAsync<AccountDto>(Json, ct) ?? throw new ChaosServiceException(ServiceFailure.Other, "The service sent an unreadable answer.");
        await _secrets.SetAsync(EmailKey, dto.Email, ct);
        return new ServiceAccount(dto.Email, dto.DisplayName, dto.StorageBytes, dto.StorageQuotaBytes,
            [.. dto.Devices.Select(d => new ServiceDevice(d.Id, d.Name, d.Platform, d.ClientVersion, d.CreatedAt, d.LastSeenAt, d.Current))],
            dto.Id,
            dto.Canonical is { } ids ? new AccountCanonicalIds(dto.Id, ids.ProfileId, ids.PersonalSpaceId, ids.InboxId) : null,
            dto.Counts is { } counts ? new AccountContentCounts(counts.Tasks, counts.Lists, counts.Tags, counts.Pages) : null);
    }

    public async Task<(bool Bound, AccountCanonicalIds Canonical)> BindAccountAsync(Guid profileId, Guid personalSpaceId, Guid inboxId, CancellationToken ct)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Post, "v1/account/bind", new { profileId, personalSpaceId, inboxId }, ct);
        await EnsureSuccessAsync(response, ct);
        var answer = await response.Content.ReadFromJsonAsync<BindDto>(Json, ct) ?? throw new ChaosServiceException(ServiceFailure.Other, "The service sent an unreadable answer.");
        var account = await GetAccountAsync(ct);
        return (answer.Bound, new AccountCanonicalIds(account.AccountId ?? Guid.Empty, answer.Canonical.ProfileId, answer.Canonical.PersonalSpaceId, answer.Canonical.InboxId));
    }

    public async Task DeleteAccountAsync(string confirmation, CancellationToken ct)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Delete, "v1/account", new { confirmation }, ct);
        await EnsureSuccessAsync(response, ct);
        // The service dropped every token with the account, so there is nothing to revoke; just forget them here.
        await ForgetSignInAsync(ct);
    }

    public async Task RevokeDeviceAsync(Guid deviceId, CancellationToken ct)
    {
        var own = (await _identity.GetAsync(ct)).Id == deviceId;
        using var response = await SendAuthorizedAsync(HttpMethod.Delete, $"v1/devices/{deviceId:D}", null, ct);
        await EnsureSuccessAsync(response, ct);
        if (own)
        {
            await SignOutAsync(ct);
        }
    }

    private async Task RegisterDeviceAsync(DeviceIdentity device, CancellationToken ct)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Post, "v1/devices", new { deviceId = device.Id, name = device.Name, platform = device.Platform, clientVersion = device.ClientVersion, clientSchema = device.ClientSchema }, ct);
        await EnsureSuccessAsync(response, ct);
    }

    // ---- Tokens --------------------------------------------------------------------------------------

    public async Task<string> GetAccessTokenAsync(CancellationToken ct)
    {
        if (TryCached(out var cached))
        {
            return cached;
        }

        await _refreshGate.WaitAsync(ct);
        try
        {
            if (TryCached(out cached))
            {
                return cached;
            }

            var refresh = await _secrets.GetAsync(RefreshKey, ct) ?? throw new ChaosServiceException(ServiceFailure.SignInAgain, "You are not signed in to the online service.");
            var response = await PostFormAsync("connect/token", [("grant_type", "refresh_token"), ("client_id", ClientId), ("refresh_token", refresh)], ct);
            if (!response.Success)
            {
                if (response.Status == HttpStatusCode.BadRequest && IsGrantError(response.Body))
                {
                    // Revoked, reused or expired: this sign-in is over, so do not keep a token that can never work.
                    await ForgetSignInAsync(ct);
                    throw GrantEnded(response.Body);
                }

                throw new ChaosServiceException(ServiceFailure.Other, "The online service did not renew the sign-in.");
            }

            return await StoreTokensAsync(response.Body, ct);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task<string> StoreTokensAsync(string body, CancellationToken ct)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var access = root.TryGetProperty("access_token", out var a) ? a.GetString() : null;
        var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
        var lifetime = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds) ? seconds : 0;
        if (string.IsNullOrEmpty(access) || string.IsNullOrEmpty(refresh))
        {
            throw new ChaosServiceException(ServiceFailure.Other, "The service sent no sign-in tokens.");
        }

        // The old refresh token is already spent on the server, so the new one is saved before anything else happens.
        await _secrets.SetAsync(RefreshKey, refresh, ct);
        lock (_cacheGate)
        {
            _accessToken = access;
            _accessExpires = _clock.GetUtcNow().AddSeconds(lifetime) - ExpirySkew;
        }

        return access;
    }

    private bool TryCached(out string token)
    {
        lock (_cacheGate)
        {
            token = _accessToken ?? string.Empty;
            return _accessToken is not null && _clock.GetUtcNow() < _accessExpires;
        }
    }

    /// <summary>The refresh was refused because the device or the account is gone (the service says which), or the sign-in simply ended.</summary>
    private static ChaosServiceException GrantEnded(string body)
    {
        if (body.Contains("device-revoked", StringComparison.Ordinal))
        {
            return new ChaosServiceException(ServiceFailure.DeviceRevoked, "This computer was removed from your account. It keeps its data and works offline.");
        }

        if (body.Contains("account-deleted", StringComparison.Ordinal))
        {
            return new ChaosServiceException(ServiceFailure.AccountDeleted, "Your account was deleted. This computer keeps its data and works offline.");
        }

        return new ChaosServiceException(ServiceFailure.SignInAgain, "Your sign-in to the online service ended. Sign in again.");
    }

    private static bool IsGrantError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) && error.GetString() is "invalid_grant" or "invalid_token";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ---- HTTP ----------------------------------------------------------------------------------------

    public async Task<ServiceResponse> CallAsync(HttpMethod method, string path, string? json, CancellationToken ct)
    {
        var device = await _identity.GetAsync(ct);
        using var response = await SendAuthorizedAsync(method, path, null, ct, json, device);
        try
        {
            return new ServiceResponse((int)response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception error) when (error is IOException or HttpRequestException)
        {
            throw new ChaosServiceException(ServiceFailure.Offline, "The connection to the online service was lost.", error);
        }
    }

    public async IAsyncEnumerable<string> StreamLinesAsync(string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var device = await _identity.GetAsync(ct);
        using var response = await SendAuthorizedAsync(HttpMethod.Get, path, null, ct, null, device, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode)
        {
            throw new ChaosServiceException(ServiceFailure.Refused, $"The online service answered with an error ({(int)response.StatusCode}).");
        }

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct), Encoding.UTF8);
        while (true)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(ct);
            }
            catch (Exception error) when (error is IOException or HttpRequestException)
            {
                throw new ChaosServiceException(ServiceFailure.Offline, "The connection to the online service was lost.", error);
            }

            if (line is null)
            {
                yield break;
            }

            yield return line;
        }
    }

    // ---- Blobs (PLAN2 §9.2) ---------------------------------------------------------------------

    public async Task<RemoteBlob?> HeadAsync(string sha256, CancellationToken ct)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Head, $"v1/blobs/{sha256}", null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        await EnsureBlobSuccessAsync(response, ct);
        var state = response.Headers.TryGetValues("X-Chaos-Blob-State", out var values) ? values.First() : "Clean";
        return new RemoteBlob(state, response.Content.Headers.ContentLength ?? 0);
    }

    public async Task<RemoteUpload> StartUploadAsync(string sha256, long size, CancellationToken ct)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Post, $"v1/blobs/{sha256}", null, ct, $"{{\"size\":{size.ToString(CultureInfo.InvariantCulture)}}}");
        await EnsureBlobSuccessAsync(response, ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = document.RootElement;
        var state = root.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        var received = root.TryGetProperty("received", out var r) ? r.EnumerateArray().Select(e => e.GetInt32()).ToList() : [];
        return new RemoteUpload(state, root.GetProperty("partSize").GetInt32(), root.GetProperty("partCount").GetInt32(), received);
    }

    public async Task PutPartAsync(string sha256, int index, ReadOnlyMemory<byte> bytes, CancellationToken ct)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Put, $"v1/blobs/{sha256}?part={index.ToString(CultureInfo.InvariantCulture)}", null, ct, rawBytes: bytes.ToArray());
        await EnsureBlobSuccessAsync(response, ct);
    }

    public async Task<RemoteBlob> CompleteUploadAsync(string sha256, CancellationToken ct)
    {
        using var response = await SendAuthorizedAsync(HttpMethod.Post, $"v1/blobs/{sha256}/complete", null, ct);
        await EnsureBlobSuccessAsync(response, ct);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return new RemoteBlob(document.RootElement.GetProperty("state").GetString() ?? "Clean", document.RootElement.TryGetProperty("size", out var size) ? size.GetInt64() : 0);
    }

    public async Task<Stream> OpenDownloadAsync(string sha256, long offset, CancellationToken ct)
    {
        var response = await SendAuthorizedAsync(HttpMethod.Get, $"v1/blobs/{sha256}", null, ct, completion: HttpCompletionOption.ResponseHeadersRead, range: offset > 0 ? offset : null);
        try
        {
            await EnsureBlobSuccessAsync(response, ct);
            return new ResponseStream(response, await response.Content.ReadAsStreamAsync(ct));
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    /// <summary>Keeps the response alive while its body is read, and turns a dropped connection into the service's offline failure.</summary>
    private sealed class ResponseStream(HttpResponseMessage response, Stream body) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                return await body.ReadAsync(buffer, cancellationToken);
            }
            catch (Exception error) when (error is IOException or HttpRequestException)
            {
                throw new ChaosServiceException(ServiceFailure.Offline, "The connection to the online service was lost.", error);
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                body.Dispose();
                response.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Turns the service's blob problem codes into <see cref="BlobTransferException"/>; anything else is the general failure.</summary>
    private static async Task EnsureBlobSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var code = response.RequestMessage?.Method == HttpMethod.Head ? null : await ReadCodeAsync(response, ct);
        var kind = code switch
        {
            "blocked" => BlobFailureKind.Blocked,
            "quota" => BlobFailureKind.Quota,
            "too-large" => BlobFailureKind.TooLarge,
            "hash-mismatch" => BlobFailureKind.HashMismatch,
            "scanner-unavailable" => BlobFailureKind.ScannerUnavailable,
            "no-upload" => BlobFailureKind.NotFound,
            _ => response.StatusCode == HttpStatusCode.NotFound ? BlobFailureKind.NotFound : BlobFailureKind.Other,
        };
        if (kind == BlobFailureKind.Other)
        {
            await EnsureSuccessAsync(response, ct);
        }

        throw new BlobTransferException(kind, await ReadDetailAsync(response, ct) ?? "The online service refused the file.");
    }

    private async Task<HttpResponseMessage> SendAuthorizedAsync(HttpMethod method, string path, object? body, CancellationToken ct, string? rawJson = null, DeviceIdentity? protocolFor = null, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead, byte[]? rawBytes = null, long? range = null)
    {
        var options = Require();
        for (var attempt = 0; ; attempt++)
        {
            var token = await GetAccessTokenAsync(ct);
            using var request = new HttpRequestMessage(method, new Uri(options.BaseAddress, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body, options: Json);
            }
            else if (rawJson is not null)
            {
                request.Content = new StringContent(rawJson, Encoding.UTF8, "application/json");
            }
            else if (rawBytes is not null)
            {
                request.Content = new ByteArrayContent(rawBytes);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            }

            if (range is { } from)
            {
                request.Headers.Range = new RangeHeaderValue(from, null);
            }

            if (protocolFor is not null)
            {
                var protocol = path.StartsWith("v2/", StringComparison.Ordinal) ? Chaos.Sync.SyncProtocol.Version2 : Chaos.Sync.SyncProtocol.Version1;
                request.Headers.TryAddWithoutValidation(Chaos.Sync.SyncProtocol.ProtocolHeader, protocol.ToString(CultureInfo.InvariantCulture));
                request.Headers.TryAddWithoutValidation(Chaos.Sync.SyncProtocol.SchemaHeader, protocolFor.ClientSchema.ToString(CultureInfo.InvariantCulture));
                request.Headers.TryAddWithoutValidation(Chaos.Sync.SyncProtocol.DeviceHeader, protocolFor.Id.ToString("D"));
            }

            var response = await SendAsync(request, ct, completion);
            if (response.StatusCode != HttpStatusCode.Unauthorized)
            {
                return response;
            }

            var code = await ReadCodeAsync(response, ct);
            response.Dispose();
            lock (_cacheGate)
            {
                _accessToken = null;
            }

            // A 401 that names its reason is final: a fresh token cannot bring a device or an account back.
            if (code is "device-revoked" or "account-deleted")
            {
                await ForgetSignInAsync(ct);
                throw code == "device-revoked"
                    ? new ChaosServiceException(ServiceFailure.DeviceRevoked, "This computer was removed from your account. It keeps its data and works offline.")
                    : new ChaosServiceException(ServiceFailure.AccountDeleted, "Your account was deleted. This computer keeps its data and works offline.");
            }

            if (attempt == 1)
            {
                throw new ChaosServiceException(ServiceFailure.SignInAgain, "The online service no longer accepts this sign-in. Sign in again.");
            }
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct, HttpCompletionOption completion = HttpCompletionOption.ResponseContentRead)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CallTimeout);
        try
        {
            return await _http.SendAsync(request, completion, timeout.Token);
        }
        catch (HttpRequestException error)
        {
            throw new ChaosServiceException(ServiceFailure.Offline, "The online service could not be reached.", error);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new ChaosServiceException(ServiceFailure.Offline, "The online service took too long to answer.");
        }
    }

    private async Task<(bool Success, HttpStatusCode Status, string Body)> PostFormAsync(string path, (string Name, string Value)[] fields, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Require().BaseAddress, path))
        {
            Content = new FormUrlEncodedContent(fields.Select(f => KeyValuePair.Create(f.Name, f.Value))),
        };
        using var response = await SendAsync(request, ct);
        return (response.IsSuccessStatusCode, response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var detail = await ReadDetailAsync(response, ct);
        if (response.StatusCode == HttpStatusCode.Forbidden && await ReadCodeAsync(response, ct) == "fresh-sign-in-required")
        {
            throw new ChaosServiceException(ServiceFailure.FreshSignInRequired, detail ?? "Sign in again to confirm this.");
        }

        throw response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new ChaosServiceException(ServiceFailure.SignInAgain, "The online service no longer accepts this sign-in. Sign in again."),
            HttpStatusCode.TooManyRequests => new ChaosServiceException(ServiceFailure.Refused, "Too many requests. Try again in a minute."),
            HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.BadRequest =>
                new ChaosServiceException(ServiceFailure.Refused, detail ?? "The online service refused the request."),
            _ => new ChaosServiceException(ServiceFailure.Other, $"The online service answered with an error ({(int)response.StatusCode})."),
        };
    }

    private static async Task<string?> ReadCodeAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return document.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<string?> ReadDetailAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var detail = document.RootElement.TryGetProperty("detail", out var d) ? d.GetString() : null;
            return detail is { Length: > 0 and <= 200 } ? detail : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private ChaosServiceOptions Require() =>
        _options() ?? throw new ChaosServiceException(ServiceFailure.NotConfigured, "The online service is not set up in this build of Chaos.");

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Query(params (string Name, string Value)[] pairs) =>
        string.Join('&', pairs.Where(p => p.Value.Length > 0).Select(p => $"{Uri.EscapeDataString(p.Name)}={Uri.EscapeDataString(p.Value)}"));

    private sealed record DeviceDto(Guid Id, string Name, string Platform, string ClientVersion, DateTimeOffset CreatedAt, DateTimeOffset LastSeenAt, bool Current);

    private sealed record IdsDto(Guid ProfileId, Guid PersonalSpaceId, Guid InboxId);

    private sealed record CountsDto(long Tasks, long Lists, long Tags, long Pages);

    private sealed record BindDto(bool Bound, IdsDto Canonical);

    private sealed record AccountDto(Guid Id, string Email, string? DisplayName, long StorageBytes, long StorageQuotaBytes, List<DeviceDto> Devices, IdsDto? Canonical = null, CountsDto? Counts = null);
}
