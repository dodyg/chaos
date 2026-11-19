using System.Diagnostics;
using Chaos.Application.Contracts;
using Chaos.Application.Tasks;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Sqlite;
using Chaos.Infrastructure.Sync;
using Chaos.Infrastructure.Tests.DataProtection;
using Microsoft.Data.Sqlite;

namespace Chaos.Infrastructure.Tests.Sync;

/// <summary>
/// The capture overhead budget of PLAN2 §P1 item 12: on a database of 10,000 tasks, an ordinary
/// task save (details edit through the real command) must stay within the Phase 1 p95 target of
/// 150 ms while sync is enabled — triggers pend the changed groups and the flush turns them into
/// outbox changes inside the same transaction. The bound is asserted in Release, the build the
/// gate measures; a Debug build logs its numbers without asserting them, because Debug timings are
/// not the product's.
/// </summary>
[NotInParallel]
public sealed class SyncCapturePerformanceTests
{
    private const int TaskCount = 10_000;
    private const int SaveCount = 60;
    private static readonly TimeSpan p95Target = TimeSpan.FromMilliseconds(150);

    [Test]
    public async Task An_ordinary_task_save_stays_within_the_p95_target_with_sync_enabled()
    {
        using var host = await DataHost.StartAsync(clock: new DataHost.ManualClock(new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero)));
        await SyncTestHelpers.EnableSyncAsync(host);

        // The dataset: 10,000 tasks with tags-scale rows, inserted in one transaction so the
        // capture cost is paid once, as a bulk import would.
        await host.Store.ExecuteAsync(async (session, token) =>
        {
            var connection = session.Connection();
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO Tasks (Id, SpaceId, ContainerId, Title, Status, Priority, CapacityCategory, Position, Version, CreatedAtUtc, UpdatedAtUtc)
                VALUES ($id, (SELECT Id FROM Spaces LIMIT 1), (SELECT Id FROM Containers WHERE IsInbox = 1 LIMIT 1), $title, 0, 0, 0, $position, 1, $now, $now);
                """;
            var id = insert.CreateParameter();
            id.ParameterName = "$id";
            var title = insert.CreateParameter();
            title.ParameterName = "$title";
            var position = insert.CreateParameter();
            position.ParameterName = "$position";
            var now = insert.CreateParameter();
            now.ParameterName = "$now";
            now.Value = "2026-11-15T09:00:00.0000000Z";
            insert.Parameters.AddRange([id, title, position, now]);
            insert.Prepare();
            for (var index = 0; index < TaskCount; index++)
            {
                id.Value = Guid.NewGuid().ToString();
                title.Value = $"Task {index}";
                position.Value = (long)index * 1024;
                insert.ExecuteNonQuery();
            }

            return 0;
        }, CancellationToken.None);

        // Measure ordinary saves through the real command.
        var probe = (await host.Tasks.CaptureAsync(new(Guid.NewGuid().ToString(), string.Empty, "Probe", null))).Task;
        var durations = new List<long>();
        for (var round = 0; round < SaveCount; round++)
        {
            var current = (await host.Tasks.GetAsync(probe.Id))!;
            var watch = Stopwatch.StartNew();
            await host.Tasks.UpdateDetailsAsync(new(
                Guid.NewGuid().ToString(), probe.Id, current.Version, $"Probe {round}", current.Notes,
                null, null, null, null, null, null, TaskPriority.Normal, null, CapacityCategory.Personal));
            watch.Stop();
            durations.Add(watch.ElapsedMilliseconds);
        }

        durations.Sort();
        var p50 = durations[durations.Count / 2];
        var p95 = durations[(int)Math.Ceiling(durations.Count * 0.95) - 1];
        var maximum = durations[^1];

        // The measured numbers are the package's record (PLAN2 §P1 item 12): they land next to the
        // convergence diagnostics in temp, and the gate's Release run asserts the p95 bound.
        var report = Path.Combine(Path.GetTempPath(), "chaos-convergence", "perf.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        var build = "Debug";
#if !DEBUG
        build = "Release";
#endif
        await File.WriteAllTextAsync(report, $"{build}: {TaskCount} tasks, {SaveCount} saves: p50 {p50} ms, p95 {p95} ms, max {maximum} ms (budget p95 <= {(long)p95Target.TotalMilliseconds} ms)\n");
#if !DEBUG
        // The gate measures Release; Debug builds run the same workload without the bound.
        await Assert.That(p95).IsLessThanOrEqualTo((long)p95Target.TotalMilliseconds);
#endif
    }
}
