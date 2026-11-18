namespace Chaos.Server.Auth;

/// <summary>One external provider's settings. A provider is enabled only when every field it needs is set.</summary>
public sealed class ExternalProviderSettings
{
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    /// <summary>Microsoft: the tenant (default <c>common</c>). Apple: the Apple developer team ID.</summary>
    public string? TenantId { get; set; }

    /// <summary>Apple: the sign-in key's ID and its PKCS#8 private key (PEM text).</summary>
    public string? KeyId { get; set; }
    public string? PrivateKey { get; set; }
}

/// <summary>The <c>Providers</c> configuration section (PLAN2 §8.4).</summary>
public sealed class ProvidersOptions
{
    public const string Section = "Providers";

    public ExternalProviderSettings Google { get; set; } = new();
    public ExternalProviderSettings Microsoft { get; set; } = new();
    public ExternalProviderSettings Apple { get; set; } = new();
    public ExternalProviderSettings GitHub { get; set; } = new();
}

/// <summary>An enabled provider: <see cref="Key"/> is the <c>AccountLogins.Provider</c> value, <see cref="Name"/> OpenIddict's provider name.</summary>
public sealed record ExternalProviderInfo(string Key, string Name, string Display)
{
    public string CallbackPath => $"/callback/login/{Key}";
}

public static class ExternalProviders
{
    public static IReadOnlyList<ExternalProviderInfo> Enabled(ProvidersOptions options)
    {
        var result = new List<ExternalProviderInfo>();
        if (Has(options.Google.ClientId, options.Google.ClientSecret))
        {
            result.Add(new(Data.LoginProviders.Google, "Google", "Google"));
        }

        if (Has(options.Microsoft.ClientId, options.Microsoft.ClientSecret))
        {
            result.Add(new(Data.LoginProviders.Microsoft, "Microsoft", "Microsoft"));
        }

        if (Has(options.Apple.ClientId, options.Apple.TenantId, options.Apple.KeyId, options.Apple.PrivateKey))
        {
            result.Add(new(Data.LoginProviders.Apple, "Apple", "Apple"));
        }

        if (Has(options.GitHub.ClientId, options.GitHub.ClientSecret))
        {
            result.Add(new(Data.LoginProviders.GitHub, "GitHub", "GitHub"));
        }

        return result;
    }

    private static bool Has(params string?[] values) => values.All(v => !string.IsNullOrWhiteSpace(v));
}
