namespace Chaos.Domain.Validation;

/// <summary>
/// Explicit content limits (PLAN §6 task operations). Errors show the limits themselves.
/// </summary>
public static class Limits
{
    public const int TitleUnicodeScalars = 500;
    public const long NotesUtf8Bytes = 1024 * 1024; // 1 MiB, notes/diary/pad item
    public const int MaxBatchCaptureLines = 500;
    public const int MaxEstimateMinutes = 10_080; // one week of minutes
}
