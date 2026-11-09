using Chaos.Application.Abstractions;

namespace Chaos.Infrastructure.Sqlite;

/// <summary>
/// Holds the exclusive data-root lock for this process (PLAN §5: the app lock prevents a
/// second process migrating or restoring the same root). A file opened with FileShare.None
/// is the strongest portable lock; a second opener fails immediately.
/// </summary>
public sealed class DataRootLock : IDisposable
{
    private readonly FileStream? _stream;

    private DataRootLock(FileStream stream) => _stream = stream;

    /// <exception cref="AppException">StorageUnavailable when another process owns the root.</exception>
    public static DataRootLock Acquire(string dataRoot)
    {
        Directory.CreateDirectory(dataRoot);
        var lockPath = Path.Combine(dataRoot, "chaos.lock");

        FileStream stream;
        try
        {
            stream = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            throw new AppException(
                AppErrorKind.StorageUnavailable,
                "Chaos is already running for this data folder. Use the running window, or close it before starting another copy.");
        }

        return new DataRootLock(stream);
    }

    public void Dispose() => _stream?.Dispose();
}
