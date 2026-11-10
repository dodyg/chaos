using System.Text;

namespace Chaos.Domain.Validation;

/// <summary>
/// Task notes limit (PLAN §6 task operations): notes hold at most 1 MiB of UTF-8 text
/// and the limit is stated in errors. Entered text is preserved as-is.
/// </summary>
public static class TaskNotes
{
    public const int MaxUtf8Bytes = 1024 * 1024;

    /// <returns>The notes text (empty when the input is null).</returns>
    /// <exception cref="DomainValidationException">More than <see cref="MaxUtf8Bytes"/> UTF-8 bytes.</exception>
    public static string Normalize(string? raw)
    {
        var notes = raw ?? string.Empty;
        var byteCount = Encoding.UTF8.GetByteCount(notes);

        if (byteCount > MaxUtf8Bytes)
        {
            throw new DomainValidationException(
                $"Notes can hold at most {MaxUtf8Bytes} bytes of UTF-8 text (1 MiB). The entered notes have {byteCount}.");
        }

        return notes;
    }
}

/// <summary>
/// Time estimate limits (PLAN §6 task operations): a nonnegative number of minutes,
/// at most one week (10,080 minutes).
/// </summary>
public static class EstimateMinutes
{
    public const int MaxMinutes = 10_080;

    /// <returns>The estimate, or null when unset.</returns>
    /// <exception cref="DomainValidationException">Negative, or above <see cref="MaxMinutes"/>.</exception>
    public static int? Normalize(int? raw)
    {
        if (raw is null)
        {
            return null;
        }

        if (raw < 0)
        {
            throw new DomainValidationException("An estimate cannot be negative. Enter a value from 0 to 10080 minutes.");
        }

        if (raw > MaxMinutes)
        {
            throw new DomainValidationException(
                $"An estimate can hold at most {MaxMinutes} minutes (one week). The entered estimate is {raw}.");
        }

        return raw;
    }
}
