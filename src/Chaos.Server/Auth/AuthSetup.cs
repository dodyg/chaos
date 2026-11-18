using System.Security.Cryptography.X509Certificates;
using Chaos.Server.Data;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Client;
using OpenIddict.Client.AspNetCore;
using OpenIddict.Client.WebIntegration;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace Chaos.Server.Auth;

/// <summary>The sign-in cookie that carries a person from the sign-in page back to the authorize endpoint.</summary>
public static class SignInCookie
{
    public const string Scheme = "chaos-signin";
    public const string LinkCookie = "chaos.link";
}

/// <summary>The one OAuth client the desktop app uses, and the scope names it asks for.</summary>
public static class DesktopClient
{
    public const string ClientId = "chaos-desktop";
    public const string Scope = "chaos";
    public const string CallbackPath = "/callback";

    /// <summary>The device the sign-in is for: sent on the authorize request and bound into the tokens.</summary>
    public const string DeviceIdParameter = "device_id";
    public const string DeviceIdClaim = "device_id";
}

public static class AuthSetup
{
    public static IServiceCollection AddChaosAuth(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.Configure<SignInOptions>(configuration.GetSection(SignInOptions.Section));
        services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.Section));
        services.Configure<LimitsOptions>(configuration.GetSection(LimitsOptions.Section));
        services.Configure<ProvidersOptions>(configuration.GetSection(ProvidersOptions.Section));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<SignInThrottle>();
        services.AddSingleton<IEmailSender, SmtpEmailSender>();
        services.AddScoped<EmailCodeService>();
        services.AddScoped<AccountService>();
        services.AddAntiforgery(options =>
        {
            options.Cookie.Name = "chaos.af";
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        });

        var keys = configuration.GetSection(KeyOptions.Section).Get<KeyOptions>() ?? new KeyOptions();
        var providers = ExternalProviders.Enabled(configuration.GetSection(ProvidersOptions.Section).Get<ProvidersOptions>() ?? new ProvidersOptions());
        var providerSettings = configuration.GetSection(ProvidersOptions.Section).Get<ProvidersOptions>() ?? new ProvidersOptions();

        services.AddAuthentication(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
            .AddCookie(SignInCookie.Scheme, options =>
            {
                options.LoginPath = "/signin";
                options.Cookie.Name = "chaos.signin";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                options.ExpireTimeSpan = TimeSpan.FromMinutes(30);
                options.SlidingExpiration = false;
            });
        services.AddAuthorization();

        var openiddict = services.AddOpenIddict();
        openiddict.AddCore(options =>
        {
            options.UseEntityFrameworkCore().UseDbContext<ChaosDbContext>();
            options.ReplaceApplicationManager(typeof(LoopbackApplicationManager<>));
        });

        openiddict.AddServer(options =>
        {
            options.SetAuthorizationEndpointUris("/connect/authorize").SetTokenEndpointUris("/connect/token");
            options.AllowAuthorizationCodeFlow().AllowRefreshTokenFlow();
            options.RequireProofKeyForCodeExchange();
            options.RegisterScopes(Scopes.OfflineAccess, DesktopClient.Scope);
            options.SetAccessTokenLifetime(TimeSpan.FromMinutes(15));
            options.SetRefreshTokenLifetime(TimeSpan.FromDays(90));
            // Reuse of a rotated refresh token revokes the whole family at once (PLAN2 §8.4 step 4): no grace window.
            options.SetRefreshTokenReuseLeeway(TimeSpan.Zero);
            ConfigureKeys(options, keys, environment);
            var aspNet = options.UseAspNetCore().EnableAuthorizationEndpointPassthrough().EnableTokenEndpointPassthrough();
            if (environment.IsDevelopment())
            {
                aspNet.DisableTransportSecurityRequirement();
            }
        });

        openiddict.AddValidation(options =>
        {
            options.UseLocalServer();
            options.UseAspNetCore();
        });

        if (providers.Count > 0)
        {
            openiddict.AddClient(options =>
            {
                options.AllowAuthorizationCodeFlow();
                ConfigureKeys(options, keys, environment);
                options.SetRedirectionEndpointUris([.. providers.Select(p => p.CallbackPath)]);
                options.UseAspNetCore().EnableRedirectionEndpointPassthrough();
                options.UseSystemNetHttp();
                var web = options.UseWebProviders();
                foreach (var provider in providers)
                {
                    var redirect = new Uri(provider.CallbackPath.TrimStart('/'), UriKind.Relative);
                    switch (provider.Key)
                    {
                        case LoginProviders.Google:
                            web.AddGoogle(p => p.SetClientId(providerSettings.Google.ClientId!).SetClientSecret(providerSettings.Google.ClientSecret!).SetRedirectUri(redirect).AddScopes(Scopes.Email));
                            break;
                        case LoginProviders.Microsoft:
                            web.AddMicrosoft(p => p.SetClientId(providerSettings.Microsoft.ClientId!).SetClientSecret(providerSettings.Microsoft.ClientSecret!)
                                .SetTenant(providerSettings.Microsoft.TenantId ?? "common").SetRedirectUri(redirect).AddScopes(Scopes.Email));
                            break;
                        case LoginProviders.GitHub:
                            web.AddGitHub(p => p.SetClientId(providerSettings.GitHub.ClientId!).SetClientSecret(providerSettings.GitHub.ClientSecret!).SetRedirectUri(redirect).AddScopes("user:email"));
                            break;
                        case LoginProviders.Apple:
                            web.AddApple(p => p.SetClientId(providerSettings.Apple.ClientId!).SetTeamId(providerSettings.Apple.TenantId!)
                                .SetRedirectUri(redirect).SetSigningKey(AppleKey(providerSettings.Apple.PrivateKey!, providerSettings.Apple.KeyId!)).AddScopes(Scopes.Email));
                            break;
                    }
                }
            });
        }

        return services;
    }

    private static Microsoft.IdentityModel.Tokens.ECDsaSecurityKey AppleKey(string pem, string keyId)
    {
        var key = System.Security.Cryptography.ECDsa.Create();
        key.ImportFromPem(pem);
        return new Microsoft.IdentityModel.Tokens.ECDsaSecurityKey(key) { KeyId = keyId };
    }

    private static void ConfigureKeys(OpenIddictServerBuilder options, KeyOptions keys, IHostEnvironment environment)
    {
        if (keys.SigningCertificatePath is { Length: > 0 } signing && keys.EncryptionCertificatePath is { Length: > 0 } encryption)
        {
            options.AddSigningCertificate(X509CertificateLoader.LoadPkcs12FromFile(signing, keys.SigningCertificatePassword));
            options.AddEncryptionCertificate(X509CertificateLoader.LoadPkcs12FromFile(encryption, keys.EncryptionCertificatePassword));
        }
        else if (environment.IsDevelopment())
        {
            options.AddEphemeralSigningKey().AddEphemeralEncryptionKey();
        }
        else
        {
            throw new InvalidOperationException("Auth:SigningCertificatePath and Auth:EncryptionCertificatePath must be set outside development.");
        }
    }

    private static void ConfigureKeys(OpenIddictClientBuilder options, KeyOptions keys, IHostEnvironment environment)
    {
        if (keys.SigningCertificatePath is { Length: > 0 } signing && keys.EncryptionCertificatePath is { Length: > 0 } encryption)
        {
            options.AddSigningCertificate(X509CertificateLoader.LoadPkcs12FromFile(signing, keys.SigningCertificatePassword));
            options.AddEncryptionCertificate(X509CertificateLoader.LoadPkcs12FromFile(encryption, keys.EncryptionCertificatePassword));
        }
        else if (environment.IsDevelopment())
        {
            options.AddEphemeralSigningKey().AddEphemeralEncryptionKey();
        }
        else
        {
            throw new InvalidOperationException("Auth:SigningCertificatePath and Auth:EncryptionCertificatePath must be set outside development.");
        }
    }

    /// <summary>Creates or updates the desktop client (a public client: authorization code + PKCE, loopback redirect).</summary>
    public static async Task EnsureDesktopClientAsync(IOpenIddictApplicationManager manager, CancellationToken ct)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = DesktopClient.ClientId,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = "Chaos desktop",
            RedirectUris = { new Uri("http://127.0.0.1" + DesktopClient.CallbackPath), new Uri("http://[::1]" + DesktopClient.CallbackPath) },
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Prefixes.Scope + Scopes.OfflineAccess,
                Permissions.Prefixes.Scope + DesktopClient.Scope,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };

        if (await manager.FindByClientIdAsync(DesktopClient.ClientId, ct) is { } existing)
        {
            await manager.UpdateAsync(existing, descriptor, ct);
        }
        else
        {
            await manager.CreateAsync(descriptor, ct);
        }
    }
}
