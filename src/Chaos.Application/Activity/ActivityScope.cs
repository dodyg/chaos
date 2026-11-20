namespace Chaos.Application.Activity;

/// <summary>The origin of a write, closed list matching the ActivityLog.Source check (PLAN2 §6.4).</summary>
public static class ActivitySources
{
    public const string User = "user";
    public const string Schedule = "schedule";
    public const string Sync = "sync";
    public const string Import = "import";
    public const string Undo = "undo";
    public const string System = "system";
}

/// <summary>
/// The ambient operation context of PLAN2 §6.4: who is writing and why. Opened at every entry
/// point (RPC method, scheduler pass, undo, import, remote apply) and read by the capture inside
/// the store's write transaction. Disposing restores the enclosing scope.
/// </summary>
public sealed class ActivityScope
{
    private static readonly AsyncLocal<ActivityScope?> Ambient = new();

    private ActivityScope(string source, string action, string operationId, string? jobRunId, string? actorId)
    {
        Source = source;
        Action = action;
        OperationId = operationId;
        JobRunId = jobRunId;
        ActorId = actorId;
    }

    public string Source { get; }
    public string Action { get; }
    public string OperationId { get; }
    public string? JobRunId { get; }
    public string? ActorId { get; }

    public static ActivityScope? Current => Ambient.Value;

    private static readonly AsyncLocal<bool> StrictFlow = new();

    /// <summary>Process-wide default of <see cref="Strict"/>; a Debug app build turns it on at startup.</summary>
    public static bool StrictByDefault { get; set; }

    /// <summary>
    /// When true a write with no scope, or with an action outside <see cref="ActivityActions"/>,
    /// throws instead of being recorded as <c>system/unscoped</c>. Release never loses a write.
    /// </summary>
    public static bool Strict => StrictByDefault || StrictFlow.Value;

    /// <summary>Makes <see cref="Strict"/> true for the current async flow only (tests run in parallel).</summary>
    public static IDisposable UseStrict()
    {
        var previous = StrictFlow.Value;
        StrictFlow.Value = true;
        return new StrictRestore(previous);
    }

    private sealed class StrictRestore(bool previous) : IDisposable
    {
        public void Dispose() => StrictFlow.Value = previous;
    }

    public static IDisposable Open(string source, string action, string? operationId = null, string? jobRunId = null, string? actorId = null)
    {
        var previous = Ambient.Value;
        Ambient.Value = new ActivityScope(source, action, operationId ?? Guid.NewGuid().ToString("D"), jobRunId ?? previous?.JobRunId, actorId ?? previous?.ActorId);
        return new Restore(previous);
    }

    /// <summary>The scope of an RPC method: <c>using var scope = ActivityScope.Rpc("tasks.create");</c></summary>
    public static IDisposable Rpc(string action) => Open(ActivitySources.User, action);

    /// <summary>Keeps the enclosing scope when there is one (a pass reached through an RPC call); otherwise opens this one.</summary>
    public static IDisposable Ensure(string source, string action) =>
        Current is null ? Open(source, action) : new Restore(Current);

    private sealed class Restore(ActivityScope? previous) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            Ambient.Value = previous;
        }
    }
}
