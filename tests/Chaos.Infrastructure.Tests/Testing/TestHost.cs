using Chaos.Application.Abstractions;

namespace Chaos.Infrastructure.Tests.Testing;

/// <summary>Deterministic clock for storage tests (PLAN §16: injectable time).</summary>
public sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}

/// <summary>Deterministic IDs so seeded rows can be asserted exactly.</summary>
public sealed class SequentialIdGenerator : IIdGenerator
{
    private int _counter;

    public Guid NewId() => new(_counter++, 0, 0, new byte[8]);
}

public static class TestPaths
{
    /// <summary>Unique temp data root per integration test (PLAN §16 isolation rules).</summary>
    public static string NewDataRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "chaos-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
