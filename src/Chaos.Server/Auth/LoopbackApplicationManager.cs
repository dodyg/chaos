using Microsoft.Extensions.Options;
using OpenIddict.Abstractions;
using OpenIddict.Core;

namespace Chaos.Server.Auth;

/// <summary>
/// RFC 8252 section 7.3: a native app listens on a random loopback port, so a registered
/// <c>http://127.0.0.1/callback</c> accepts <c>http://127.0.0.1:{port}/callback</c>. OpenIddict matches
/// redirect URIs exactly, port included, so this manager adds the loopback rule and nothing else.
/// </summary>
public sealed class LoopbackApplicationManager<TApplication>(
    IOpenIddictApplicationCache<TApplication> cache,
    ILogger<OpenIddictApplicationManager<TApplication>> logger,
    IOptionsMonitor<OpenIddictCoreOptions> options,
    IOpenIddictApplicationStore<TApplication> store) : OpenIddictApplicationManager<TApplication>(cache, logger, options, store)
    where TApplication : class
{
    public override async ValueTask<bool> ValidateRedirectUriAsync(TApplication application, string uri, CancellationToken cancellationToken = default)
    {
        if (await base.ValidateRedirectUriAsync(application, uri, cancellationToken))
        {
            return true;
        }

        if (!IsLoopbackHttp(uri, out var requested) || !string.IsNullOrEmpty(requested.Query) || !string.IsNullOrEmpty(requested.Fragment))
        {
            return false;
        }

        foreach (var registered in await GetRedirectUrisAsync(application, cancellationToken))
        {
            if (IsLoopbackHttp(registered, out var known) && known.IsDefaultPort && known.Host == requested.Host && known.AbsolutePath == requested.AbsolutePath)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLoopbackHttp(string value, out Uri uri) =>
        Uri.TryCreate(value, UriKind.Absolute, out uri!) && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback && string.IsNullOrEmpty(uri.UserInfo);
}
