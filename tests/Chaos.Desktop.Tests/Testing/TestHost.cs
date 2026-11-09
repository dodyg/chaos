using Chaos.Application.Abstractions;

namespace Chaos.Desktop.Tests.Testing;

/// <summary>Deterministic clock for RPC slice tests (PLAN §16).</summary>
public sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}

/// <summary>Deterministic IDs so created tasks can be asserted exactly.</summary>
public sealed class SequentialIdGenerator : IIdGenerator
{
    private int _counter;

    public Guid NewId() => new(_counter++, 0, 0, new byte[8]);
}

public static class TestPaths
{
    /// <summary>Unique temp data root per test (PLAN §16 isolation rules).</summary>
    public static string NewDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "chaos-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
