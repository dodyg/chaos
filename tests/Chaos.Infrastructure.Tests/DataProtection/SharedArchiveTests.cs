using Chaos.Application.Transfer;
using Chaos.Infrastructure.Sqlite;
using Chaos.Infrastructure.Transfer;

namespace Chaos.Infrastructure.Tests.DataProtection;

/// <summary>PLAN3 §8.7: shared spaces in an archive come back as Personal copies with new IDs and no membership.</summary>
public sealed class SharedArchiveTests
{
    private static Task ExecuteAsync(DataHost host, string sql, params (string Name, object Value)[] parameters) =>
        host.Store.ExecuteAsync((session, _) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = sql;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        });

    [Test]
    public async Task RestoreArchive_WithASharedSpace_ImportsPersonalCopiesWithNewIds()
    {
        using var source = await DataHost.StartAsync();
        var personalList = (await source.Tasks.CaptureAsync(new(Guid.NewGuid().ToString(), string.Empty, "Personal task", null))).Task;
        var shared = (await source.Tasks.CaptureAsync(new(Guid.NewGuid().ToString(), string.Empty, "Shared task", null))).Task;
        var space = Guid.NewGuid().ToString("D");
        var list = Guid.NewGuid().ToString("D");
        await ExecuteAsync(source, """
            INSERT INTO Spaces (Id, OwnerProfileId, Kind, Name, CreatedAtUtc, UpdatedAtUtc) VALUES ($space, $owner, 2, 'Team', 'now', 'now');
            INSERT INTO SpaceMembers (Id, SpaceId, MemberId, MemberKind, DisplayName, Role, JoinedAtUtc) VALUES ($member, $space, $owner, 1, 'Owner', 1, 'now');
            INSERT INTO Containers (Id, SpaceId, Kind, Name, NormalizedName, IsInbox, Position, Version, CreatedAtUtc, UpdatedAtUtc)
                SELECT $list, $space, Kind, 'Inbox', 'inbox', 0, 1, 1, CreatedAtUtc, UpdatedAtUtc FROM Containers WHERE IsInbox = 1;
            UPDATE Tasks SET SpaceId = $space, ContainerId = $list WHERE Id = $task;
            INSERT INTO DayPlans (Id, ProfileId, LocalDate, InitializedAtUtc, Version, CreatedAtUtc, UpdatedAtUtc)
                SELECT $plan, Id, '2026-11-20', 'now', 1, 'now', 'now' FROM Profiles LIMIT 1;
            INSERT INTO DayPlanItems (Id, DayPlanId, Kind, TaskId, Position, Origin, AddedAtUtc, Version, CreatedAtUtc, UpdatedAtUtc)
                VALUES ($item, $plan, 0, $task, 0, 0, 'now', 1, 'now', 'now');
            """,
            ("$space", space), ("$owner", Guid.NewGuid().ToString("D")), ("$member", Guid.NewGuid().ToString("D")), ("$list", list),
            ("$task", shared.Id), ("$plan", Guid.NewGuid().ToString("D")), ("$item", Guid.NewGuid().ToString("D")));
        var export = await source.Transfer.ExportAsync(ExportFormat.Json, Path.Combine(source.Root, "out"), CancellationToken.None);

        using var target = await DataHost.StartAsync();
        ArchiveContent archive;
        await using (var stream = File.OpenRead(export.Path))
        {
            archive = JsonArchive.Read(stream);
        }

        await target.Protection.RestoreArchiveAsync(archive, export.FileName, CancellationToken.None);

        var personalSpace = await target.Store.QueryAsync((session, _) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = "SELECT Id FROM Spaces WHERE Kind = 1;";
            return Task.FromResult(command.ExecuteScalar() as string);
        });
        await Assert.That(await target.CountAsync("SELECT count(*) FROM Spaces WHERE Kind = 2;")).IsEqualTo(0L);
        await Assert.That(await target.CountAsync("SELECT count(*) FROM SpaceMembers;")).IsEqualTo(0L);
        // The shared task and list are copies: same content, new IDs, in the Personal space.
        await Assert.That(await target.CountAsync($"SELECT count(*) FROM Tasks WHERE Id = '{shared.Id}';")).IsEqualTo(0L);
        await Assert.That(await target.CountAsync($"SELECT count(*) FROM Containers WHERE Id = '{list}';")).IsEqualTo(0L);
        await Assert.That(await target.CountAsync($"SELECT count(*) FROM Tasks WHERE Title = 'Shared task' AND SpaceId = '{personalSpace}';")).IsEqualTo(1L);
        await Assert.That(await target.CountAsync($"SELECT count(*) FROM Containers WHERE Name = 'Inbox (Team)' AND SpaceId = '{personalSpace}' AND IsInbox = 0;")).IsEqualTo(1L);
        await Assert.That(await target.CountAsync("SELECT count(*) FROM Tasks t JOIN Containers c ON c.Id = t.ContainerId WHERE t.Title = 'Shared task' AND c.Name = 'Inbox (Team)';")).IsEqualTo(1L);
        // Personal rows follow the copy, and personal work keeps its IDs.
        await Assert.That(await target.CountAsync("SELECT count(*) FROM DayPlanItems d JOIN Tasks t ON t.Id = d.TaskId WHERE t.Title = 'Shared task';")).IsEqualTo(1L);
        await Assert.That(await target.CountAsync($"SELECT count(*) FROM Tasks WHERE Id = '{personalList.Id}';")).IsEqualTo(1L);
        await Assert.That(await target.CountAsync("SELECT count(*) FROM TaskHistory h JOIN Tasks t ON t.Id = h.TaskId WHERE t.Title = 'Shared task';")).IsGreaterThan(0L);
    }
}
