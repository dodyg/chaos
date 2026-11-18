namespace Chaos.Server.Auth;

/// <summary>Sign-in limits and timings (PLAN2 §8.4, §8.5). Defaults are the plan's numbers; tests tighten them.</summary>
public sealed class SignInOptions
{
    public const string Section = "SignIn";

    /// <summary>How long an emailed code works.</summary>
    public TimeSpan CodeLifetime { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Codes one address may be sent per hour.</summary>
    public int MaxCodesPerEmailPerHour { get; set; } = 5;

    /// <summary>Sign-in form posts one IP address may make per hour.</summary>
    public int MaxAttemptsPerIpPerHour { get; set; } = 20;

    /// <summary>PBKDF2-SHA256 iterations for code hashes.</summary>
    public int Pbkdf2Iterations { get; set; } = 100_000;
}

/// <summary>Outgoing mail through an SMTP relay (<c>Smtp</c> section). No host means mail cannot be sent.</summary>
public sealed class SmtpOptions
{
    public const string Section = "Smtp";

    public string? Host { get; set; }
    public int Port { get; set; } = 587;
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string From { get; set; } = "Chaos <no-reply@localhost>";
    public bool EnableSsl { get; set; }
}

/// <summary>Per-account limits (PLAN2 D5).</summary>
public sealed class LimitsOptions
{
    public const string Section = "Limits";

    public long AccountQuotaBytes { get; set; } = 5L * 1024 * 1024 * 1024;

    /// <summary>Requests one device may make per minute (PLAN2 §8.5).</summary>
    public int RequestsPerMinutePerDevice { get; set; } = 600;
}

/// <summary>
/// Token signing and encryption keys. Outside development both certificates must be configured
/// (<c>Auth:SigningCertificatePath</c>, <c>Auth:EncryptionCertificatePath</c> and passwords); development
/// uses ephemeral keys, so a restart signs the devices out.
/// </summary>
public sealed class KeyOptions
{
    public const string Section = "Auth";

    public string? SigningCertificatePath { get; set; }
    public string? SigningCertificatePassword { get; set; }
    public string? EncryptionCertificatePath { get; set; }
    public string? EncryptionCertificatePassword { get; set; }
}
