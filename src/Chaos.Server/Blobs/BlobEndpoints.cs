using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Chaos.Server.Auth;
using Chaos.Server.Api;
using Chaos.Server.Data;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Chaos.Server.Blobs;

public sealed record StartUploadRequest(long Size);

/// <summary>The blob endpoints (PLAN2 §9.2). The account comes from the token, so another account's hash is simply "not found".</summary>
public static class BlobEndpoints
{
    public const string StateHeader = "X-Chaos-Blob-State";

    public static IServiceCollection AddBlobService(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<BlobOptions>().Bind(configuration.GetSection(BlobOptions.Section))
            .Validate(o => o.PartSize > 0 && o.MaxBlobBytes > 0, "Blob:PartSize and Blob:MaxBlobBytes must be greater than zero.")
            .Validate(o => o.UnreferencedGrace >= TimeSpan.Zero && o.UploadExpiry > TimeSpan.Zero && o.JobInterval > TimeSpan.Zero,
                "Blob:UnreferencedGrace must not be negative; Blob:UploadExpiry and Blob:JobInterval must be greater than zero.")
            .ValidateOnStart();
        services.Configure<KeyVaultOptions>(configuration.GetSection(KeyVaultOptions.Section));
        services.AddOptions<ClamOptions>().Bind(configuration.GetSection(ClamOptions.Section))
            .Validate(o => o.Port is > 0 and <= 65535, "Clam:Port must be a valid port number.")
            .ValidateOnStart();

        // Development fills in the folders and the scanner; any other host must say where its blobs, key and scanner are.
        services.TryAddSingleton<IBlobStore>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<BlobOptions>>();
            if (options.Value.Directory is null && environment.IsDevelopment())
            {
                options.Value.Directory = Path.Combine(AppContext.BaseDirectory, "blobs");
            }

            return new FileBlobStore(options);
        });
        services.TryAddSingleton<IKeyWrapper>(provider =>
        {
            var path = provider.GetRequiredService<IOptions<KeyVaultOptions>>().Value.MasterKeyPath;
            if (path is null && environment.IsDevelopment())
            {
                path = Path.Combine(AppContext.BaseDirectory, "blobs-master.key");
            }

            return new FileKeyWrapper(path ?? throw new InvalidOperationException("KeyVault:MasterKeyPath is not configured."), createIfMissing: environment.IsDevelopment());
        });
        services.TryAddSingleton<IMalwareScanner>(provider =>
        {
            var clam = provider.GetRequiredService<IOptions<ClamOptions>>().Value;
            if (clam.Host is { Length: > 0 })
            {
                return new ClamAvScanner(clam.Host, clam.Port);
            }

            return environment.IsDevelopment() ? new EicarScanner() : throw new InvalidOperationException("Clam:Host is not configured.");
        });
        services.AddScoped<BlobService>();
        services.AddHostedService<BlobSweepService>();
        services.AddHealthChecks().AddCheck<BlobReadyCheck>("blobs", tags: ["ready"]);
        return services;
    }

    public static void MapBlobEndpoints(this RouteGroupBuilder v1)
    {
        var blobs = v1.MapGroup("/blobs");
        blobs.MapMethods("/{sha256}", [HttpMethods.Head], Head);
        blobs.MapPost("/{sha256}", Start);
        blobs.MapPut("/{sha256}", PutPart);
        blobs.MapPost("/{sha256}/complete", Complete);
        blobs.MapGet("/{sha256}", Download);
    }

    private static IResult Failure(BlobFailure failure) => failure switch
    {
        BlobFailure.InvalidHash => ApiErrors.Problem("invalid-hash", "A blob is addressed by the lowercase hex SHA-256 of its bytes.", StatusCodes.Status400BadRequest),
        BlobFailure.TooLarge => ApiErrors.Problem("too-large", "That file is larger than the service accepts.", StatusCodes.Status413PayloadTooLarge),
        BlobFailure.Quota => ApiErrors.Problem("quota", "The account has no room for that file.", StatusCodes.Status413PayloadTooLarge),
        BlobFailure.NoUpload => ApiErrors.Problem("no-upload", "No upload was started for that file.", StatusCodes.Status404NotFound),
        BlobFailure.BadPart => ApiErrors.Problem("bad-part", "That part is not the size or position the upload expects.", StatusCodes.Status400BadRequest),
        BlobFailure.Incomplete => ApiErrors.Problem("incomplete", "Some parts have not arrived.", StatusCodes.Status409Conflict),
        BlobFailure.HashMismatch => ApiErrors.Problem("hash-mismatch", "The bytes do not match their SHA-256. Upload the file again.", StatusCodes.Status422UnprocessableEntity),
        BlobFailure.ScannerUnavailable => ApiErrors.Problem("scanner-unavailable", "The malware check is not available right now. Try again later.", StatusCodes.Status503ServiceUnavailable),
        _ => ApiErrors.Problem("error", "The request failed.", StatusCodes.Status500InternalServerError),
    };

    private static async Task<IResult> Head(string sha256, ClaimsPrincipal user, HttpContext http, BlobService blobs, CancellationToken ct)
    {
        if (CallerIdentity.TryGet(user) is not { } caller)
        {
            return Results.Unauthorized();
        }

        if (!BlobService.IsHash(sha256))
        {
            return Failure(BlobFailure.InvalidHash);
        }

        if (await blobs.FindAsync(caller.AccountId, sha256, ct) is not { } blob)
        {
            return Results.NotFound();
        }

        http.Response.Headers[StateHeader] = blob.State;
        http.Response.ContentLength = blob.ByteSize;
        return Results.Ok();
    }

    private static async Task<IResult> Start(string sha256, StartUploadRequest request, ClaimsPrincipal user, BlobService blobs, CancellationToken ct)
    {
        if (CallerIdentity.TryGet(user) is not { } caller)
        {
            return Results.Unauthorized();
        }

        var outcome = await blobs.StartAsync(caller.AccountId, caller.DeviceId, sha256, request.Size, ct);
        return outcome.Succeeded
            ? Results.Ok(new { state = outcome.Value!.BlobState, partSize = outcome.Value.PartSize, partCount = outcome.Value.PartCount, received = outcome.Value.Received })
            : Failure(outcome.Failure);
    }

    private static async Task<IResult> PutPart(string sha256, int part, HttpContext http, ClaimsPrincipal user, BlobService blobs, CancellationToken ct)
    {
        if (CallerIdentity.TryGet(user) is not { } caller)
        {
            return Results.Unauthorized();
        }

        var failure = await blobs.PutPartAsync(caller.AccountId, sha256, part, http.Request.Body, ct);
        return failure == BlobFailure.None ? Results.NoContent() : Failure(failure);
    }

    private static async Task<IResult> Complete(string sha256, ClaimsPrincipal user, BlobService blobs, CancellationToken ct)
    {
        if (CallerIdentity.TryGet(user) is not { } caller)
        {
            return Results.Unauthorized();
        }

        var outcome = await blobs.CompleteAsync(caller.AccountId, caller.DeviceId, sha256, ct);
        return outcome.Succeeded
            ? Results.Ok(new { state = outcome.Value!.State, size = outcome.Value.ByteSize, detail = outcome.Value.Detail })
            : Failure(outcome.Failure);
    }

    private static async Task<IResult> Download(string sha256, ClaimsPrincipal user, HttpContext http, BlobService blobs, CancellationToken ct)
    {
        if (CallerIdentity.TryGet(user) is not { } caller)
        {
            return Results.Unauthorized();
        }

        if (!BlobService.IsHash(sha256))
        {
            return Failure(BlobFailure.InvalidHash);
        }

        if (await blobs.FindAsync(caller.AccountId, sha256, ct) is not { } blob)
        {
            return Results.NotFound();
        }

        if (blob.State != BlobStates.Clean)
        {
            // Never served. The state header lets the client show why.
            http.Response.Headers[StateHeader] = blob.State;
            return ApiErrors.Problem("blocked", "This file was blocked by the malware check.", StatusCodes.Status409Conflict);
        }

        var range = ParseRange(http.Request.Headers.Range.ToString(), blob.ByteSize);
        if (range is { Satisfiable: false })
        {
            http.Response.Headers.ContentRange = $"bytes */{blob.ByteSize}";
            return Results.StatusCode(StatusCodes.Status416RangeNotSatisfiable);
        }

        return new BlobDownloadResult(blobs, blob, range?.Start ?? 0, range?.End ?? Math.Max(blob.ByteSize - 1, 0), range is not null);
    }

    internal sealed record ByteRange(bool Satisfiable, long Start, long End);

    /// <summary>One <c>bytes=</c> range; anything else (several ranges, other units) is served whole, as the standard allows.</summary>
    internal static ByteRange? ParseRange(string header, long size)
    {
        if (!header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || header.Contains(','))
        {
            return null;
        }

        var spec = header["bytes=".Length..].Trim();
        var dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return null;
        }

        var from = spec[..dash];
        var to = spec[(dash + 1)..];
        long start;
        long end;
        if (from.Length == 0)
        {
            if (!long.TryParse(to, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix == 0)
            {
                return new ByteRange(false, 0, 0);
            }

            start = Math.Max(size - suffix, 0);
            end = size - 1;
        }
        else
        {
            if (!long.TryParse(from, NumberStyles.None, CultureInfo.InvariantCulture, out start))
            {
                return null;
            }

            end = size - 1;
            if (to.Length > 0 && (!long.TryParse(to, NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start))
            {
                return to.Length > 0 && long.TryParse(to, NumberStyles.None, CultureInfo.InvariantCulture, out _) ? new ByteRange(false, 0, 0) : null;
            }

            end = Math.Min(end, size - 1);
        }

        return start >= size ? new ByteRange(false, 0, 0) : new ByteRange(true, start, end);
    }
}

/// <summary>Streams a decrypted range. A chunk that fails its tag aborts the response, so tampered bytes are never delivered as complete.</summary>
internal sealed class BlobDownloadResult(BlobService blobs, BlobRow blob, long start, long end, bool partial) : IResult
{
    public async Task ExecuteAsync(HttpContext http)
    {
        var response = http.Response;
        response.StatusCode = partial ? StatusCodes.Status206PartialContent : StatusCodes.Status200OK;
        response.ContentType = "application/octet-stream";
        response.Headers.AcceptRanges = "bytes";
        response.Headers[BlobEndpoints.StateHeader] = blob.State;
        response.Headers.ETag = $"\"{blob.Sha256}\"";
        response.Headers["X-Content-Type-Options"] = "nosniff";
        response.Headers.ContentDisposition = "attachment";
        var length = blob.ByteSize == 0 ? 0 : end - start + 1;
        response.ContentLength = length;
        if (partial)
        {
            response.Headers.ContentRange = $"bytes {start}-{end}/{blob.ByteSize}";
        }

        try
        {
            await blobs.WriteRangeAsync(blob, start, end, response.Body, http.RequestAborted);
        }
        catch (CryptographicException)
        {
            // Headers are already sent; dropping the connection tells the client the body is not complete.
            http.Abort();
        }
    }
}

/// <summary>Readiness (<c>/health/ready</c>): the blob store and the scanner answer (PLAN2 §8.7).</summary>
public sealed class BlobReadyCheck(IBlobStore store, IMalwareScanner scanner) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await store.CheckAsync(cancellationToken);
            await scanner.CheckAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("The blob store or the malware scanner is not available.", error);
        }
    }
}

/// <summary>Runs <see cref="BlobService.SweepAsync"/> on a timer.</summary>
public sealed class BlobSweepService(IServiceScopeFactory scopes, IOptions<BlobOptions> options, TimeProvider clock, ILogger<BlobSweepService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Value.JobInterval, clock);
        try
        {
            do
            {
                try
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var (blobs, uploads) = await scope.ServiceProvider.GetRequiredService<BlobService>().SweepAsync(stoppingToken);
                    log.LogInformation("Blob sweep removed {Blobs} blobs and {Uploads} unfinished uploads", blobs, uploads);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    log.LogWarning(error, "The blob sweep failed; it will run again");
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
            log.LogDebug("Blob sweep service stopped.");
        }
    }
}
