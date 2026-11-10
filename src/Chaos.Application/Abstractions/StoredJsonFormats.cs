using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Chaos.Application.Abstractions;

/// <summary>Stored receipt results and history snapshots carry a version so later app versions can recognize what they read.</summary>
public sealed record VersionedPayload<T>(int V, T Payload)
{
    public const int CurrentVersion = 1;
}

/// <summary>
/// Canonical JSON for receipts, history snapshots and stored payloads (PLAN §4/§5):
/// camelCase properties, versioned envelopes, SHA-256 payload hashes. Shared by the
/// application commands (receipts/history) and the SQLite stores (template payloads).
/// </summary>
public static class StoredJsonFormats
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>SHA-256 lowercase hex over the canonical camelCase JSON of the value.</summary>
    public static string Hash<T>(T value)
    {
        var canonical = JsonSerializer.Serialize(value, Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// <summary>Serializes the payload inside a versioned envelope.</summary>
    public static string Snapshot<T>(T payload) =>
        JsonSerializer.Serialize(new VersionedPayload<T>(VersionedPayload<T>.CurrentVersion, payload), Options);

    /// <summary>Reads the payload from a versioned envelope, or null when it cannot be read.</summary>
    public static T? ReadSnapshot<T>(string json) where T : class =>
        JsonSerializer.Deserialize<VersionedPayload<T>>(json, Options)?.Payload;
}
