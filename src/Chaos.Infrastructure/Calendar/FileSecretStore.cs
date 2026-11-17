using System.Security.Cryptography;
using System.Text;
using Chaos.Application.Abstractions;

namespace Chaos.Infrastructure.Calendar;

/// <summary>
/// An <see cref="ISecretStore"/> that keeps one protected file per key under a folder outside the
/// database and outside backups and exports (PLAN2 §10.2). File names are a hash of the key, so a
/// key (which names a source) is not readable from the folder listing either.
/// </summary>
public sealed class FileSecretStore(string directory, ISecretProtector protector) : ISecretStore
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            Directory.CreateDirectory(directory);
            var path = PathFor(key);
            var temp = path + ".tmp";
            await File.WriteAllBytesAsync(temp, protector.Protect(Encoding.UTF8.GetBytes(value)), ct);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var path = PathFor(key);
            if (!File.Exists(path))
            {
                return null;
            }

            try
            {
                return Encoding.UTF8.GetString(protector.Unprotect(await File.ReadAllBytesAsync(path, ct)));
            }
            catch (CryptographicException)
            {
                // Copied from another user or machine: the secret is unreadable here, as if absent.
                return null;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DeleteAsync(string key, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var path = PathFor(key);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string PathFor(string key) =>
        Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32].ToLowerInvariant() + ".secret");
}
