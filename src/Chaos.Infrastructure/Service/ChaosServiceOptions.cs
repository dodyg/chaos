using System.Text.Json;
using Chaos.Application.Abstractions;

namespace Chaos.Infrastructure.Service;

/// <summary>
/// Where the online service is. Read from <c>CHAOS_SERVICE_URL</c> or <c>chaos-service.json</c>
/// (<c>{"baseUrl": "https://sync.example.com"}</c>) beside the exe; without either, accounts are not offered.
/// Only HTTPS is accepted, except <c>http://127.0.0.1</c> and <c>http://localhost</c> when the build allows it (PLAN2 §8.5).
/// </summary>
public sealed record ChaosServiceOptions(Uri BaseAddress)
{
    public const string EnvironmentVariable = "CHAOS_SERVICE_URL";

    /// <summary>Checks the address; null when it is not usable.</summary>
    public static ChaosServiceOptions? TryCreate(string? address, bool allowLoopbackHttp)
    {
        if (string.IsNullOrWhiteSpace(address) || !Uri.TryCreate(address.Trim(), UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }

        var secure = uri.Scheme == Uri.UriSchemeHttps;
        var loopback = uri.Scheme == Uri.UriSchemeHttp && allowLoopbackHttp && (uri.IsLoopback || uri.Host == "localhost");
        if (!secure && !loopback)
        {
            return null;
        }

        // Always a trailing slash, so relative paths resolve under the service root.
        return new ChaosServiceOptions(new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/"));
    }

    public static ChaosServiceOptions? TryLoad(string jsonPath, bool allowLoopbackHttp)
    {
        if (TryCreate(Environment.GetEnvironmentVariable(EnvironmentVariable), allowLoopbackHttp) is { } fromEnvironment)
        {
            return fromEnvironment;
        }

        try
        {
            if (!File.Exists(jsonPath))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(jsonPath));
            return document.RootElement.TryGetProperty("baseUrl", out var value) ? TryCreate(value.GetString(), allowLoopbackHttp) : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
