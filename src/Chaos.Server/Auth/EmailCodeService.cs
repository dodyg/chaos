using System.Globalization;
using System.Security.Cryptography;
using Chaos.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chaos.Server.Auth;

public enum CodeIssue
{
    Sent,
    RateLimited,
    SendFailed,
}

public enum CodeCheck
{
    Accepted,
    Wrong,

    /// <summary>No live code: none sent, expired, already used, or out of attempts.</summary>
    NoLiveCode,
}

/// <summary>
/// Six-digit email codes (PLAN2 §8.4): ten minutes, five attempts, stored only as a PBKDF2 hash, at most
/// a few per address per hour. Nothing here logs a code or an address.
/// </summary>
public sealed class EmailCodeService(ChaosDbContext db, IEmailSender mail, TimeProvider clock, IOptions<SignInOptions> options, ILogger<EmailCodeService> log)
{
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    public async Task<CodeIssue> IssueAsync(string email, CancellationToken ct)
    {
        var settings = options.Value;
        var now = clock.GetUtcNow();
        var since = now.AddHours(-1);
        var recent = await db.EmailCodes.CountAsync(c => c.Email == email && c.CreatedAt > since, ct);
        if (recent >= settings.MaxCodesPerEmailPerHour)
        {
            return CodeIssue.RateLimited;
        }

        var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);
        var row = new EmailCode
        {
            Id = Guid.NewGuid(),
            Email = email,
            CodeHash = Hash(code, settings.Pbkdf2Iterations),
            CreatedAt = now,
            ExpiresAt = now + settings.CodeLifetime,
        };
        db.EmailCodes.Add(row);
        await db.SaveChangesAsync(ct);

        try
        {
            await mail.SendAsync(email, "Your Chaos sign-in code",
                $"Your Chaos sign-in code is {code}.\n\nIt works for {(int)settings.CodeLifetime.TotalMinutes} minutes. If you did not ask for it, you can ignore this email.", ct);
            return CodeIssue.Sent;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // The code was never delivered, so it must not count as a live one.
            await db.EmailCodes.Where(c => c.Id == row.Id).ExecuteDeleteAsync(CancellationToken.None);
            log.LogWarning(error, "A sign-in email could not be sent");
            return CodeIssue.SendFailed;
        }
    }

    public async Task<CodeCheck> VerifyAsync(string email, string code, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var live = await db.EmailCodes.AsNoTracking()
            .Where(c => c.Email == email && c.ConsumedAt == null && c.ExpiresAt > now && c.Attempts < EmailCode.MaxAttempts)
            .OrderByDescending(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct);
        if (live is null)
        {
            return CodeCheck.NoLiveCode;
        }

        // Count the attempt before comparing, in one statement, so parallel guesses cannot exceed the limit.
        var counted = await db.EmailCodes
            .Where(c => c.Id == live.Id && c.ConsumedAt == null && c.Attempts < EmailCode.MaxAttempts)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Attempts, c => c.Attempts + 1), ct);
        if (counted == 0)
        {
            return CodeCheck.NoLiveCode;
        }

        if (!Matches(code, live.CodeHash))
        {
            return CodeCheck.Wrong;
        }

        var consumed = await db.EmailCodes.Where(c => c.Id == live.Id && c.ConsumedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ConsumedAt, now), ct);
        return consumed == 1 ? CodeCheck.Accepted : CodeCheck.NoLiveCode;
    }

    /// <summary>Format <c>v1.iterations.salt.hash</c>, base64.</summary>
    internal static string Hash(string code, int iterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(code, salt, iterations, HashAlgorithmName.SHA256, HashBytes);
        return $"v1.{iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(hash)}";
    }

    internal static bool Matches(string code, string stored)
    {
        var parts = stored.Split('.');
        if (parts is not ["v1", var iterations, var salt, var hash] || !int.TryParse(iterations, CultureInfo.InvariantCulture, out var count))
        {
            return false;
        }

        var expected = Convert.FromBase64String(hash);
        var actual = Rfc2898DeriveBytes.Pbkdf2(code, Convert.FromBase64String(salt), count, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
