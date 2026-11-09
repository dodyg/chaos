namespace Chaos.Application.Abstractions;

/// <summary>Stable error kinds surfaced to the renderer (PLAN §4). Never stack traces, SQL or local paths.</summary>
public enum AppErrorKind
{
    Validation = 1,
    NotFound = 2,
    VersionConflict = 3,
    OperationConflict = 4,
    StorageFull = 5,
    StorageUnavailable = 6,
    Cancelled = 7,
    UnsupportedCapability = 8,
}

public sealed record AppErrorField(string FieldPath, string Message);

/// <summary>
/// The only exception type that crosses the RPC boundary unhandled-safe: it carries a
/// stable kind and field paths. Infrastructure storage failures map into it before they
/// leave the application store.
/// </summary>
public sealed class AppException : Exception
{
    public AppErrorKind Kind { get; }

    public IReadOnlyList<AppErrorField> Fields { get; }

    public string CorrelationId { get; }

    public AppException(AppErrorKind kind, string message, IReadOnlyList<AppErrorField>? fields = null, string? correlationId = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        Fields = fields ?? [];
        CorrelationId = correlationId ?? Guid.NewGuid().ToString("N");
    }

    public static AppException Validation(string fieldPath, string message) =>
        new(AppErrorKind.Validation, message, [new AppErrorField(fieldPath, message)]);

    public static AppException NotFound(string message) => new(AppErrorKind.NotFound, message);

    public static AppException VersionConflict(string message) => new(AppErrorKind.VersionConflict, message);

    public static AppException OperationConflict(string message) => new(AppErrorKind.OperationConflict, message);
}
