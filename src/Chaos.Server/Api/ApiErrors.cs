namespace Chaos.Server.Api;

/// <summary>The one error shape: ProblemDetails with a machine-readable <c>code</c> the clients branch on.</summary>
internal static class ApiErrors
{
    public static IResult Problem(string code, string detail, int status) =>
        Results.Problem(detail: detail, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = code });
}
