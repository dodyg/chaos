using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Chaos.Server.Blobs;

/// <summary>Blob settings (<c>Blob</c> section, <c>Blob__*</c> in the container host).</summary>
public sealed class BlobOptions
{
    public const string Section = "Blob";

    /// <summary>Where the file store keeps objects. Required outside development.</summary>
    public string? Directory { get; set; }

    /// <summary>Most bytes in one blob (PLAN2 D5: 100 MiB).</summary>
    public long MaxBlobBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>The size of one uploaded part.</summary>
    public int PartSize { get; set; } = 1024 * 1024;

    /// <summary>How long an unreferenced blob is kept (PLAN2 §9.2: 7 days).</summary>
    public TimeSpan UnreferencedGrace { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How long an upload may sit unfinished before its parts are discarded.</summary>
    public TimeSpan UploadExpiry { get; set; } = TimeSpan.FromDays(1);

    public TimeSpan JobInterval { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>Key vault settings (<c>KeyVault</c> section). The file wrapper reads the master key from <see cref="MasterKeyPath"/>.</summary>
public sealed class KeyVaultOptions
{
    public const string Section = "KeyVault";

    public string? MasterKeyPath { get; set; }
}

/// <summary>ClamAV settings (<c>Clam</c> section): the clamd host and port. Unset in development means the built-in EICAR check.</summary>
public sealed class ClamOptions
{
    public const string Section = "Clam";

    public string? Host { get; set; }
    public int Port { get; set; } = 3310;
}

/// <summary>An object store on a local folder. Writes go to a temporary file and are renamed into place.</summary>
public sealed class FileBlobStore : IBlobStore
{
    private readonly string _root;

    public FileBlobStore(IOptions<BlobOptions> options)
    {
        _root = Path.GetFullPath(options.Value.Directory ?? throw new InvalidOperationException("Blob:Directory is not configured."));
        Directory.CreateDirectory(_root);
    }

    /// <summary>Keys are built by the service from GUIDs, hashes and indexes; anything else is refused so a key can never leave the root.</summary>
    private string PathOf(string key)
    {
        foreach (var part in key.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or ".." || part.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new ArgumentException("Not a blob key.", nameof(key));
            }
        }

        return Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar));
    }

    public async Task PutAsync(string key, Stream content, CancellationToken ct)
    {
        var path = PathOf(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await content.CopyToAsync(file, ct);
                await file.FlushAsync(ct);
                file.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    public Task<bool> ExistsAsync(string key, CancellationToken ct) => Task.FromResult(File.Exists(PathOf(key)));

    public Task<Stream?> OpenReadAsync(string key, long offset, CancellationToken ct)
    {
        var path = PathOf(key);
        if (!File.Exists(path))
        {
            return Task.FromResult<Stream?>(null);
        }

        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        file.Seek(offset, SeekOrigin.Begin);
        return Task.FromResult<Stream?>(file);
    }

    public Task DeleteAsync(string key, CancellationToken ct)
    {
        File.Delete(PathOf(key));
        return Task.CompletedTask;
    }

    public Task DeletePrefixAsync(string prefix, CancellationToken ct)
    {
        var path = PathOf(prefix);
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        return Task.CompletedTask;
    }

    public async Task CheckAsync(CancellationToken ct)
    {
        var key = "health/" + Guid.NewGuid().ToString("N");
        await PutAsync(key, new MemoryStream([1]), ct);
        await using var read = await OpenReadAsync(key, 0, ct) ?? throw new IOException("The blob store did not return what was written.");
        await DeleteAsync(key, ct);
    }
}

/// <summary>
/// Wraps data keys with AES-256-GCM under a master key kept in a file (development, and hosts that mount the key as a secret file).
/// A vault-backed wrapper replaces it behind <see cref="IKeyWrapper"/>. A missing file is created only when <paramref name="createIfMissing"/>.
/// </summary>
public sealed class FileKeyWrapper : IKeyWrapper
{
    private readonly byte[] _master;

    public FileKeyWrapper(string path, bool createIfMissing)
    {
        if (!File.Exists(path))
        {
            if (!createIfMissing)
            {
                throw new InvalidOperationException("KeyVault:MasterKeyPath names a file that does not exist.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(BlobCrypto.KeySize));
        }

        _master = File.ReadAllBytes(path);
        if (_master.Length != BlobCrypto.KeySize)
        {
            throw new InvalidOperationException("The master key file must hold exactly 32 bytes.");
        }
    }

    public byte[] Wrap(byte[] dataKey)
    {
        var result = new byte[BlobCrypto.NonceSize + dataKey.Length + BlobCrypto.TagSize];
        RandomNumberGenerator.Fill(result.AsSpan(0, BlobCrypto.NonceSize));
        using var aes = new AesGcm(_master, BlobCrypto.TagSize);
        aes.Encrypt(result.AsSpan(0, BlobCrypto.NonceSize), dataKey, result.AsSpan(BlobCrypto.NonceSize, dataKey.Length), result.AsSpan(BlobCrypto.NonceSize + dataKey.Length, BlobCrypto.TagSize));
        return result;
    }

    public byte[] Unwrap(byte[] wrappedKey)
    {
        var length = wrappedKey.Length - BlobCrypto.NonceSize - BlobCrypto.TagSize;
        if (length <= 0)
        {
            throw new CryptographicException("The wrapped key is too short.");
        }

        var key = new byte[length];
        using var aes = new AesGcm(_master, BlobCrypto.TagSize);
        aes.Decrypt(wrappedKey.AsSpan(0, BlobCrypto.NonceSize), wrappedKey.AsSpan(BlobCrypto.NonceSize, length), wrappedKey.AsSpan(BlobCrypto.NonceSize + length, BlobCrypto.TagSize), key);
        return key;
    }
}

/// <summary>
/// Development stand-in for ClamAV: blocks the standard EICAR test string and nothing else. Never registered outside development.
/// </summary>
public sealed class EicarScanner : IMalwareScanner
{
    private static readonly byte[] Eicar = Encoding.ASCII.GetBytes(@"X5O!P%@AP[4\PZX54(P^)7CC)7}$EICAR-STANDARD-ANTIVIRUS-TEST-FILE!$H+H*");

    public async Task<ScanResult> ScanAsync(Stream content, CancellationToken ct)
    {
        // The test string is 68 bytes; keep a tail so a match across two reads is found.
        var buffer = new byte[64 * 1024];
        var window = new List<byte>(buffer.Length + Eicar.Length);
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) > 0)
        {
            window.AddRange(buffer.AsSpan(0, read).ToArray());
            if (System.MemoryExtensions.IndexOf(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(window), Eicar) >= 0)
            {
                return new ScanResult(false, "Eicar-Test-Signature");
            }

            if (window.Count > Eicar.Length)
            {
                window.RemoveRange(0, window.Count - Eicar.Length);
            }
        }

        return ScanResult.Ok;
    }

    public Task CheckAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Scans through clamd's INSTREAM command over TCP.</summary>
public sealed class ClamAvScanner(string host, int port) : IMalwareScanner
{
    private const int MaxChunk = 64 * 1024;

    public async Task<ScanResult> ScanAsync(Stream content, CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, ct);
        await using var network = client.GetStream();
        await network.WriteAsync("zINSTREAM\0"u8.ToArray(), ct);
        var buffer = new byte[MaxChunk];
        var length = new byte[4];
        int read;
        while ((read = await content.ReadAsync(buffer, ct)) > 0)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, read);
            await network.WriteAsync(length, ct);
            await network.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        await network.WriteAsync(new byte[4], ct);
        await network.FlushAsync(ct);
        var reply = await ReadReplyAsync(network, ct);
        // "stream: OK" or "stream: <signature> FOUND" or "... ERROR". An error is never treated as clean.
        if (reply.EndsWith("OK", StringComparison.Ordinal))
        {
            return ScanResult.Ok;
        }

        if (reply.EndsWith("FOUND", StringComparison.Ordinal))
        {
            var signature = reply["stream:".Length..^"FOUND".Length].Trim();
            return new ScanResult(false, signature);
        }

        throw new IOException("The malware scanner could not scan the file.");
    }

    public async Task CheckAsync(CancellationToken ct)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(host, port, ct);
        await using var network = client.GetStream();
        await network.WriteAsync("zPING\0"u8.ToArray(), ct);
        if (!(await ReadReplyAsync(network, ct)).StartsWith("PONG", StringComparison.Ordinal))
        {
            throw new IOException("The malware scanner did not answer.");
        }
    }

    private static async Task<string> ReadReplyAsync(Stream network, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (await network.ReadAsync(one, ct) == 1 && one[0] != 0 && one[0] != (byte)'\n')
        {
            bytes.Add(one[0]);
        }

        return Encoding.UTF8.GetString([.. bytes]).Trim();
    }
}
