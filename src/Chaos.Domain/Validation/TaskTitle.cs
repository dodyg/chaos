namespace Chaos.Domain.Validation;

/// <summary>
/// Raised when user input violates a domain limit. Messages state the limit itself
/// (PLAN §6: "Show the limits in errors") and never include internal details.
/// </summary>
public sealed class DomainValidationException(string message) : Exception(message);

/// <summary>
/// Normalizes a task title (PLAN §6 task operations: trim exterior whitespace,
/// reject a blank title, enforce explicit limits; title 500 Unicode scalar values).
/// </summary>
public static class TaskTitle
{
    public const int MaxUnicodeScalarValues = 500;

    /// <summary>Returns the trimmed, limit-checked title text.</summary>
    /// <exception cref="DomainValidationException">Blank input, or more than <see cref="MaxUnicodeScalarValues"/> scalar values.</exception>
    public static string Normalize(string? raw)
    {
        var title = (raw ?? string.Empty).Trim();

        if (title.Length == 0)
        {
            throw new DomainValidationException("A task needs a title. Enter at least one character.");
        }

        // One scalar value per non-surrogate char; each surrogate pair (2 chars) is one scalar.
        var scalarValues = title.Length - title.Count(char.IsHighSurrogate);

        if (scalarValues > MaxUnicodeScalarValues)
        {
            throw new DomainValidationException(
                $"A task title can hold at most {MaxUnicodeScalarValues} characters (Unicode scalar values). The entered title has {scalarValues}.");
        }

        return title;
    }
}
