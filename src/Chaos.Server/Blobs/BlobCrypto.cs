using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Chaos.Server.Blobs;

/// <summary>
/// AES-256-GCM in 1 MiB chunks (PLAN2 §8.5). Each stored chunk is <c>nonce(12) | ciphertext | tag(16)</c> with a fresh
/// random nonce, and the chunk's index is authenticated, so a reordered, swapped or tampered chunk fails. Every chunk but
/// the last holds exactly <see cref="ChunkSize"/> bytes, so a plaintext offset maps straight to a stored offset for range reads.
/// </summary>
public static class BlobCrypto
{
    public const int ChunkSize = 1024 * 1024;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int Overhead = NonceSize + TagSize;
    public const int KeySize = 32;

    public static byte[] NewDataKey() => RandomNumberGenerator.GetBytes(KeySize);

    /// <summary>The stored size of <paramref name="plainSize"/> bytes.</summary>
    public static long EncryptedSize(long plainSize) => plainSize + (((plainSize + ChunkSize - 1) / ChunkSize) * Overhead);

    private static byte[] Aad(long index)
    {
        var aad = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(aad, index);
        return aad;
    }

    /// <summary>Encrypts <paramref name="plain"/> to <paramref name="destination"/> and returns the plaintext length.</summary>
    public static async Task<long> EncryptAsync(Stream plain, Stream destination, byte[] dataKey, CancellationToken ct)
    {
        using var aes = new AesGcm(dataKey, TagSize);
        var buffer = new byte[ChunkSize];
        var sealedChunk = new byte[ChunkSize + Overhead];
        long total = 0;
        long index = 0;
        while (true)
        {
            var read = await ReadFullAsync(plain, buffer, ct);
            if (read == 0)
            {
                break;
            }

            var nonce = sealedChunk.AsSpan(0, NonceSize);
            RandomNumberGenerator.Fill(nonce);
            aes.Encrypt(nonce, buffer.AsSpan(0, read), sealedChunk.AsSpan(NonceSize, read), sealedChunk.AsSpan(NonceSize + read, TagSize), Aad(index));
            await destination.WriteAsync(sealedChunk.AsMemory(0, read + Overhead), ct);
            total += read;
            index++;
            if (read < ChunkSize)
            {
                break;
            }
        }

        return total;
    }

    /// <summary>
    /// Writes plaintext bytes <paramref name="start"/> to <paramref name="endInclusive"/> from the stored object. A chunk that fails
    /// authentication, or a stored object shorter than <paramref name="plainSize"/>, throws <see cref="CryptographicException"/>.
    /// </summary>
    public static async Task DecryptRangeAsync(Func<long, Task<Stream?>> openFrom, byte[] dataKey, long plainSize, long start, long endInclusive, Stream destination, CancellationToken ct)
    {
        if (plainSize == 0 || start > endInclusive)
        {
            return;
        }

        var firstChunk = start / ChunkSize;
        var lastChunk = endInclusive / ChunkSize;
        await using var stored = await openFrom(firstChunk * (ChunkSize + Overhead)) ?? throw new CryptographicException("The stored blob is missing.");
        using var aes = new AesGcm(dataKey, TagSize);
        var sealedChunk = new byte[ChunkSize + Overhead];
        var plain = new byte[ChunkSize];
        for (var index = firstChunk; index <= lastChunk; index++)
        {
            var plainLength = (int)Math.Min(ChunkSize, plainSize - (index * ChunkSize));
            var storedLength = plainLength + Overhead;
            if (await ReadFullAsync(stored, sealedChunk.AsMemory(0, storedLength), ct) != storedLength)
            {
                throw new CryptographicException("The stored blob is shorter than it should be.");
            }

            aes.Decrypt(sealedChunk.AsSpan(0, NonceSize), sealedChunk.AsSpan(NonceSize, plainLength), sealedChunk.AsSpan(NonceSize + plainLength, TagSize), plain.AsSpan(0, plainLength), Aad(index));
            var from = index == firstChunk ? (int)(start - (index * ChunkSize)) : 0;
            var to = index == lastChunk ? (int)(endInclusive - (index * ChunkSize)) + 1 : plainLength;
            await destination.WriteAsync(plain.AsMemory(from, to - from), ct);
        }
    }

    private static Task<int> ReadFullAsync(Stream stream, byte[] buffer, CancellationToken ct) => ReadFullAsync(stream, buffer.AsMemory(), ct);

    private static async Task<int> ReadFullAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[total..], ct);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }
}
