using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Capture;
using Chaos.Application.Containers;
using Chaos.Application.History;
using Chaos.Application.Pad;
using Chaos.Application.Planning;
using Chaos.Application.Tags;
using Chaos.Application.Tasks;
using Chaos.Application.Transfer;
using Chaos.Domain.Capture;
using Chaos.Domain.Recurrence;
using Chaos.Domain.Tasks;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Infrastructure.DataProtection;
using Chaos.Infrastructure.Sqlite;
using Chaos.Infrastructure.Transfer;
using Chaos.Infrastructure.Tests.Testing;

namespace Chaos.Infrastructure.Tests.DataProtection;

/// <summary>A started data root with the package 12 services, for data-protection tests on real SQLite.</summary>
internal sealed class DataHost : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    public sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    public sealed class GuidIds : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }

    public required string Root { get; init; }
    public required ChaosStorageHandle Storage { get; init; }
    public required ManualClock Clock { get; init; }
    public required IIdGenerator Ids { get; init; }
    public required BackupManager Backups { get; init; }
    public required DataProtectionService Protection { get; init; }
    public required DataTransfer Transfer { get; init; }
    public required ImportCommands Imports { get; init; }
    public required DataPurge Purge { get; init; }
    public required TaskCommands Tasks { get; init; }

    public SqliteApplicationStore Store => Storage.Store;

    public static async Task<DataHost> StartAsync(string? root = null, ManualClock? clock = null, IIdGenerator? ids = null, Action<string>? afterBackupCopy = null)
    {
        root ??= TestPaths.NewDataRoot();
        clock ??= new ManualClock(Start);
        ids ??= new GuidIds();
        var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", ids, clock, CancellationToken.None);
        var backups = new BackupManager(storage.Store.Source, storage.Options.BackupsDirectory, "0.1.0", clock, afterBackupCopy);
        return new DataHost
        {
            Root = root,
            Storage = storage,
            Clock = clock,
            Ids = ids,
            Backups = backups,
            Protection = new DataProtectionService(storage.Store, storage.Options, backups, ids, clock),
            Transfer = new DataTransfer(storage.Store, "0.1.0", clock),
            Imports = new ImportCommands(storage.Store, ids, clock),
            Purge = new DataPurge(storage.Store),
            Tasks = new TaskCommands(storage.Store, ids, clock),
        };
    }

    public static string Op() => Guid.NewGuid().ToString();

    /// <summary>
    /// Data touching most persisted features: a project with a section, tasks with tags,
    /// subtasks, a reminder, waiting with a follow-up and recurrence, diary entries, a pad note,
    /// My Day membership, a template, a completed task, and deleted task and tag.
    /// </summary>
    public async Task<SeedIds> SeedAsync()
    {
        var containers = new ContainerCommands(Store, Ids, Clock);
        var project = (await containers.CreateAsync(new CreateContainerCommand(Op(), "Home project", ContainerKind.Project, null))).Container;
        await containers.CreateSectionAsync(new CreateSectionCommand(Op(), project.Id, "Kitchen"));

        var capture = new CaptureCommands(Store, Ids, Clock);
        var rule = new RecurrenceRule(RecurrenceMode.FixedSchedule, RecurrenceFrequency.Week, 1, new(2026, 11, 10), null, "UTC", [DayOfWeek.Tuesday], null, null, null, null, RecurrenceEndKind.Never, null, null);
        var created = (await capture.CreateAsync(new CreateCapturedTasksCommand(Op(),
        [
            new CaptureTaskDraft
            {
                Title = "Buy paint — أزرق 🎨",
                Notes = "## Colours\n\n| room | colour |\n| --- | --- |\n| hall | blue |\n\n- [ ] sample\n* not a heading\n#+ not org",
                ContainerId = project.Id,
                Priority = TaskPriority.High,
                EstimateMinutes = 45,
                PlannedDate = new DateOnly(2026, 11, 11),
                PlannedTime = new TimeOnly(9, 30),
                DueDate = new DateOnly(2026, 11, 14),
                ZoneId = "UTC",
                TagPaths = ["home/errands", "colour"],
                Subtasks = [new CaptureSubtask("Pick shade", true), new CaptureSubtask("Get brushes", false)],
                Reminders = [new CaptureReminder(CaptureReminderAnchor.Due, -60, null)],
            },
            new CaptureTaskDraft { Title = "Call the plumber", WaitingOn = "Sam", FollowUpDate = new DateOnly(2026, 11, 12) },
            new CaptureTaskDraft { Title = "Water plants", Recurrence = rule, PlannedDate = new DateOnly(2026, 11, 10) },
            new CaptureTaskDraft { Title = "Old idea", TagPaths = ["someday"] },
            new CaptureTaskDraft { Title = "Finished thing" },
        ]))).Tasks;

        var paint = created[0];
        var diary = new DiaryCommands(Store, Ids, Clock);
        await diary.AddAsync(new AddDiaryEntryCommand(Op(), paint.Id, "Bought the **blue** sample.\n* careful", null));

        var finished = (await Tasks.GetAsync(created[4].Id))!;
        await Tasks.SetStatusAsync(new SetTaskStatusCommand(Op(), finished.Id, finished.Version, TaskItemStatus.Done, null));

        var old = (await Tasks.GetAsync(created[3].Id))!;
        await Tasks.DeleteAsync(new DeleteTaskCommand(Op(), old.Id, old.Version));

        var pad = (await new PadCommands(Store, Ids, Clock).CreateAsync(new CreatePadItemCommand(Op(), "# Shopping\nmilk, eggs"))).Item;
        await new MyDayCommands(Store, Ids, Clock).AddAsync(new DayItemCommand(Op(), new DateOnly(2026, 11, 10), paint.Id));
        await new TemplateCommands(Store, Ids, Clock).CreateAsync(new CreateTemplateCommand(Op(), "Paint job", new TemplatePayload { Title = "Paint a room", Notes = "Prime first", Subtasks = [] }, null));

        return new SeedIds(project.Id, paint.Id, created[1].Id, created[2].Id, old.Id, finished.Id, pad.Id);
    }

    public async Task<IReadOnlyList<ArchiveTable>> DumpAsync() =>
        await Store.QueryAsync(async (session, token) =>
        {
            var connection = ((SqliteDataSession)session).Connection;
            using var snapshot = connection.BeginTransaction(deferred: true);
            return await JsonArchive.DumpAsync(connection, token);
        });

    public async Task<long> CountAsync(string sql) =>
        await Store.QueryAsync(async (session, token) =>
        {
            using var command = ((SqliteDataSession)session).Connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(token));
        });

    public void Dispose() => Storage.Dispose();
}

internal sealed record SeedIds(string ProjectId, string PaintTaskId, string WaitingTaskId, string RecurringTaskId, string DeletedTaskId, string DoneTaskId, string PadItemId);
