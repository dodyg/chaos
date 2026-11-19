using System.Globalization;
using Chaos.Application.Abstractions;
using Chaos.Application.Activity;

namespace Chaos.Desktop.Services;

/// <summary>
/// The one place an RPC method turns an <see cref="AppException"/> into its response. Opens the activity
/// scope, runs the body and maps a failure with <paramref name="onError"/>, so a service method is just the
/// call and the two response shapes.
/// </summary>
internal static class Rpc
{
    public static async ValueTask<T> Run<T>(string action, Func<ValueTask<T>> body, Func<TaskErrorResponse, T> onError)
    {
        using var scope = ActivityScope.Rpc(action);
        try
        {
            return await body();
        }
        catch (AppException error)
        {
            return onError(TaskRpcService.ToErrorResponse(error));
        }
    }
}

/// <summary>Parsers for the string-typed fields that cross the RPC boundary; each throws a field-level validation error.</summary>
internal static class RpcParse
{
    public const string DateFormat = "yyyy-MM-dd";

    /// <summary>A positive version or ID number; zero and negatives are rejected.</summary>
    public static long Version(string? value, string field = "expectedVersion", string message = "Invalid version. Refresh and try again.") =>
        long.TryParse(value, out var result) && result > 0 ? result : throw AppException.Validation(field, message);

    public static DateOnly Date(string? value, string field, string message = "Enter a valid date.") =>
        DateOnly.TryParseExact(value, DateFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : throw AppException.Validation(field, message);

    public static DateOnly? OptionalDate(string? value, string field, string message = "Enter a valid date.") =>
        string.IsNullOrEmpty(value) ? null : Date(value, field, message);

    public static TimeOnly? OptionalTime(string? value, string field, string message = "Enter a valid time.") =>
        string.IsNullOrEmpty(value) ? null : TimeOnly.TryParse(value, CultureInfo.InvariantCulture, out var time) ? time : throw AppException.Validation(field, message);

    public static T Enum<T>(string? value, string field, string message = "Choose a valid value.") where T : struct, Enum =>
        System.Enum.TryParse<T>(value, out var parsed) && System.Enum.IsDefined(parsed) ? parsed : throw AppException.Validation(field, message);
}
