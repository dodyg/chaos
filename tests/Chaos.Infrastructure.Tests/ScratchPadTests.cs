using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.History;
using Chaos.Application.Pad;
using Chaos.Application.Queries;
using Chaos.Application.Tags;
using Chaos.Application.Tasks;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Infrastructure.Tests.Testing;

namespace Chaos.Infrastructure.Tests;

/// <summary>
/// PLAN §15 WP5 against real SQLite: revision-aware autosave (overlapping/out-of-order saves,
/// restart), Make task with a UTF-16 backlink that survives edits, source-revision mismatch,
/// archive undo, pad tags, search and the weekly review.
/// </summary>
public sealed class ScratchPadTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan? by = null) => Now = Now.Add(by ?? TimeSpan.FromMinutes(1));
    }

    private sealed class GuidIds : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }

    private sealed class Host : IDisposable
    {
        public required string Root { get; init; }
        public required ChaosStorageHandle Storage { get; init; }
        public required ManualClock Clock { get; init; }
        public required PadCommands Pad { get; init; }
        public required TaskCommands Tasks { get; init; }
        public required TagCommands Tags { get; init; }
        public required TaskQueries Queries { get; init; }
        public required UndoCommands Undo { get; init; }
        public required TaskHistoryCommands History { get; init; }

        public static async Task<Host> StartAsync(string? root = null, ManualClock? clock = null)
        {
            root ??= TestPaths.NewDataRoot();
            clock ??= new ManualClock(Start);
            var ids = new GuidIds();
            var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", ids, clock, CancellationToken.None);
            var tasks = new TaskCommands(storage.Store, ids, clock);
            return new Host
            {
                Root = root,
                Storage = storage,
                Clock = clock,
                Pad = new PadCommands(storage.Store, ids, clock),
                Tasks = tasks,
                Tags = new TagCommands(storage.Store, ids, clock),
                Queries = new TaskQueries(storage.Store, clock),
                Undo = new UndoCommands(storage.Store, ids, clock),
                History = new TaskHistoryCommands(storage.Store, tasks),
            };
        }

        public void Dispose() => Storage.Dispose();

        public async Task<UndoResult> UndoLatestAsync()
        {
            var latest = (await Undo.ListAsync(1)).Single();
            Clock.Advance();
            return await Undo.UndoAsync(new UndoOperationCommand(Op(), latest.OperationId));
        }

        /// <summary>A note holding <paramref name="text"/> as editor revision 1.</summary>
        public async Task<string> NoteAsync(string text)
        {
            var item = (await Pad.CreateAsync(new(Op(), string.Empty))).Item;
            await Pad.SaveTextAsync(new(item.Id, 1, text));
            return item.Id;
        }

        public async Task<MakeTaskFromPadResult> MakeTaskAsync(string itemId, string quote, string title, long? revision = null)
        {
            var item = (await Pad.GetAsync(itemId)).Item;
            var start = item.RawMarkdown.IndexOf(quote, StringComparison.Ordinal);
            return await Pad.MakeTaskAsync(new(Op(), itemId, revision ?? item.DraftVersion, start, start + quote.Length, quote, title, null, null, null, null, []));
        }
    }

    private static string Op() => Guid.NewGuid().ToString();

    private static async Task<AppException> ThrowsAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (AppException error)
        {
            return error;
        }

        throw new InvalidOperationException("Expected an AppException.");
    }

    [Test]
    public async Task Autosave_OutOfOrderAndRepeatedSaves_KeepNewestTextAcrossRestart()
    {
        var root = TestPaths.NewDataRoot();
        string itemId;
        using (var host = await Host.StartAsync(root))
        {
            itemId = (await host.Pad.CreateAsync(new(Op(), string.Empty))).Item.Id;
            var second = await host.Pad.SaveTextAsync(new(itemId, 2, "draft two"));
            var lateFirst = await host.Pad.SaveTextAsync(new(itemId, 1, "draft one")); // delivered after a newer save
            var repeat = await host.Pad.SaveTextAsync(new(itemId, 2, "draft two"));   // a retried request

            await Assert.That(second.Applied).IsTrue();
            await Assert.That(lateFirst.Applied).IsFalse();
            await Assert.That(lateFirst.Item.RawMarkdown).IsEqualTo("draft two");
            await Assert.That(repeat.Applied).IsFalse();
            await host.Pad.SaveTextAsync(new(itemId, 3, "final ✍️ text"));

            // Typing is not an app-level undo entry; only the note's creation is.
            await Assert.That((await host.Undo.ListAsync(10)).Select(x => x.Label)).IsEquivalentTo(["Add pad note"]);
        }

        using (var reopened = await Host.StartAsync(root))
        {
            var item = (await reopened.Pad.GetAsync(itemId)).Item;
            await Assert.That(item.RawMarkdown).IsEqualTo("final ✍️ text");
            await Assert.That(item.DraftVersion).IsEqualTo(3L);
        }
    }

    [Test]
    public async Task Autosave_DeletedNote_IsRefusedAndKeepsStoredText()
    {
        using var host = await Host.StartAsync();
        var itemId = await host.NoteAsync("keep me");
        var item = (await host.Pad.GetAsync(itemId)).Item;
        await host.Pad.DeleteAsync(new(Op(), itemId, item.Version));

        var error = await ThrowsAsync(() => host.Pad.SaveTextAsync(new(itemId, 2, "lost?")));

        await Assert.That(error.Kind).IsEqualTo(AppErrorKind.Validation);
        await Assert.That((await host.Pad.GetAsync(itemId)).Item.RawMarkdown).IsEqualTo("keep me");
    }

    [Test]
    public async Task MakeTask_UnicodeSelection_CreatesTaskAndUtf16BacklinkAtomically()
    {
        using var host = await Host.StartAsync();
        const string text = "مرحبا 😀 ideas\n- [ ] اتصل بأحمد ٣ مرات\n- [ ] buy milk";
        var itemId = await host.NoteAsync(text);
        var tag = (await host.Tags.CreateAsync(new(Op(), "errands", null, null))).Tag;
        const string quote = "اتصل بأحمد ٣ مرات";
        var start = text.IndexOf(quote, StringComparison.Ordinal);

        var result = await host.Pad.MakeTaskAsync(new(Op(), itemId, 1, start, start + quote.Length, quote, quote, "from the pad", null,
            new DateOnly(2026, 11, 12), null, [tag.Id]));

        // UTF-16 offsets: the emoji before the selection counts as two code units, as in the editor.
        await Assert.That(result.Link.StartIndex).IsEqualTo(start);
        await Assert.That(result.Link.Quote).IsEqualTo(quote);
        var task = (await host.Tasks.GetAsync(result.Task.Id))!;
        await Assert.That(task.Title).IsEqualTo(quote);
        await Assert.That(task.PlannedDate).IsEqualTo(new DateOnly(2026, 11, 12));
        await Assert.That(await host.Tags.ListTargetTagsAsync(TagTargetKind.Task, task.Id)).Contains(tag.Id);
        await Assert.That((await host.Pad.GetAsync(itemId)).Item.RawMarkdown).IsEqualTo(text); // source untouched
        var view = await host.Pad.GetAsync(itemId);
        await Assert.That(view.Tasks.Select(x => x.Link.TaskId)).IsEquivalentTo([task.Id]);
        var anchor = (await host.Pad.LocateAsync(task.Id))!;
        await Assert.That(anchor.State).IsEqualTo(PadAnchorState.Exact);
        await Assert.That(anchor.Start).IsEqualTo(start);
        await Assert.That((await host.History.ListAsync(task.Id))[0].EventKind).IsEqualTo("task.created");
    }

    [Test]
    public async Task MakeTask_SplitSurrogatePairOrMismatchedQuote_IsRefusedAndCreatesNothing()
    {
        using var host = await Host.StartAsync();
        const string text = "go 😀 now";
        var itemId = await host.NoteAsync(text);
        var emoji = text.IndexOf("😀", StringComparison.Ordinal);
        var inbox = await host.Tasks.ListInboxAsync();

        var split = await ThrowsAsync(() => host.Pad.MakeTaskAsync(new(Op(), itemId, 1, emoji + 1, text.Length, text[(emoji + 1)..], "half", null, null, null, null, [])));
        var mismatch = await ThrowsAsync(() => host.Pad.MakeTaskAsync(new(Op(), itemId, 1, 0, 2, "no", "wrong", null, null, null, null, [])));

        await Assert.That(split.Kind).IsEqualTo(AppErrorKind.Validation);
        await Assert.That(mismatch.Kind).IsEqualTo(AppErrorKind.Validation);
        await Assert.That((await host.Tasks.ListInboxAsync()).Count).IsEqualTo(inbox.Count);
        await Assert.That((await host.Pad.GetAsync(itemId)).Tasks).IsEmpty();
    }

    [Test]
    public async Task MakeTask_StaleSourceRevision_FindsTheQuoteAgainOrRefuses()
    {
        using var host = await Host.StartAsync();
        var itemId = await host.NoteAsync("call the bank");
        await host.Pad.SaveTextAsync(new(itemId, 2, "first: email Sara\ncall the bank"));

        // Offsets from revision 1, but the text moved on: the quote is found at its new place.
        var moved = await host.Pad.MakeTaskAsync(new(Op(), itemId, 1, 0, "call the bank".Length, "call the bank", "Call the bank", null, null, null, null, []));
        await Assert.That(moved.Link.StartIndex).IsEqualTo("first: email Sara\n".Length);
        await Assert.That(moved.Link.SourceRevision).IsEqualTo(2L);

        // A quote that no longer exists is refused instead of pointing at unrelated text.
        var gone = await ThrowsAsync(() => host.Pad.MakeTaskAsync(new(Op(), itemId, 1, 0, 9, "walk dogs", "Walk dogs", null, null, null, null, [])));
        await Assert.That(gone.Kind).IsEqualTo(AppErrorKind.Validation);
        await Assert.That((await host.Pad.GetAsync(itemId)).Tasks.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Backlink_AfterEdits_RelocatesToNearestCopyOrKeepsOldQuoteWhenGone()
    {
        var root = TestPaths.NewDataRoot();
        string taskId;
        string itemId;
        using (var host = await Host.StartAsync(root))
        {
            itemId = await host.NoteAsync("plan\nbook tickets 🎫\nnotes");
            taskId = (await host.MakeTaskAsync(itemId, "book tickets 🎫", "Book tickets")).Task.Id;

            await host.Pad.SaveTextAsync(new(itemId, 2, "book tickets 🎫 (old)\nmore lines here\nplan\nbook tickets 🎫\nnotes"));
            var relocated = (await host.Pad.LocateAsync(taskId))!;
            await Assert.That(relocated.State).IsEqualTo(PadAnchorState.Relocated);
            // Two copies exist; the one nearest the original spot wins.
            await Assert.That(relocated.Start).IsEqualTo("book tickets 🎫 (old)\nmore lines here\nplan\n".Length);
        }

        using (var reopened = await Host.StartAsync(root))
        {
            await reopened.Pad.SaveTextAsync(new(itemId, 3, "plan\ntickets bought\nnotes"));
            var missing = (await reopened.Pad.LocateAsync(taskId))!;
            await Assert.That(missing.State).IsEqualTo(PadAnchorState.Missing);
            await Assert.That(missing.Start).IsNull();
            await Assert.That(missing.Quote).IsEqualTo("book tickets 🎫");
            await Assert.That(missing.PadItemId).IsEqualTo(itemId);
        }
    }

    [Test]
    public async Task Archive_Undo_RestoresNote_ButALaterEditBlocksTheUndo()
    {
        using var host = await Host.StartAsync();
        var itemId = await host.NoteAsync("old idea");
        var item = (await host.Pad.GetAsync(itemId)).Item;
        host.Clock.Advance();
        await host.Pad.ArchiveAsync(new(Op(), [new(itemId, item.Version)]));
        await Assert.That(await host.Pad.ListAsync(archived: false, null)).IsEmpty();

        var undone = await host.UndoLatestAsync();
        await Assert.That(undone.Label).IsEqualTo("Archive pad note “old idea”");
        var restored = (await host.Pad.GetAsync(itemId)).Item;
        await Assert.That(restored.ArchivedAtUtc).IsNull();
        await Assert.That(restored.DraftVersion).IsEqualTo(1L);

        // Archive again, then type in the archived note: undoing the archive would overwrite that text.
        host.Clock.Advance();
        await host.Pad.ArchiveAsync(new(Op(), [new(itemId, restored.Version)]));
        await host.Pad.SaveTextAsync(new(itemId, 2, "old idea, refined"));
        var conflict = await ThrowsAsync(host.UndoLatestAsync);
        await Assert.That(conflict.Kind).IsEqualTo(AppErrorKind.VersionConflict);
        var after = (await host.Pad.GetAsync(itemId)).Item;
        await Assert.That(after.ArchivedAtUtc).IsNotNull();
        await Assert.That(after.RawMarkdown).IsEqualTo("old idea, refined");
    }

    [Test]
    public async Task MakeTask_Undo_RemovesTheTaskAndLeavesTheNote()
    {
        using var host = await Host.StartAsync();
        var itemId = await host.NoteAsync("- water plants");
        var task = (await host.MakeTaskAsync(itemId, "water plants", "Water plants")).Task;

        var undone = await host.UndoLatestAsync();

        await Assert.That(undone.Label).IsEqualTo("Make task “Water plants”");
        await Assert.That(await host.Tasks.GetAsync(task.Id)).IsNull();
        await Assert.That((await host.Pad.GetAsync(itemId)).Item.RawMarkdown).IsEqualTo("- water plants");
        await Assert.That((await host.Pad.GetAsync(itemId)).Tasks.Single().Deleted).IsTrue();
    }

    [Test]
    public async Task DeleteRestoreAndReorder_AreRecordedAndKeepOrder()
    {
        using var host = await Host.StartAsync();
        var a = await host.NoteAsync("a");
        var b = await host.NoteAsync("b");
        var c = await host.NoteAsync("c");
        var cItem = (await host.Pad.GetAsync(c)).Item;
        await host.Pad.ReorderAsync(new(Op(), c, cItem.Version, 0));
        await Assert.That(string.Join(",", (await host.Pad.ListAsync(false, null)).Select(x => x.Item.Id))).IsEqualTo(string.Join(",", c, a, b));

        var bItem = (await host.Pad.GetAsync(b)).Item;
        await host.Pad.DeleteAsync(new(Op(), b, bItem.Version));
        await Assert.That((await host.Pad.ListDeletedAsync()).Select(x => x.Id)).IsEquivalentTo([b]);
        var deleted = (await host.Pad.ListDeletedAsync()).Single();
        await host.Pad.RestoreAsync(new(Op(), b, deleted.Version));
        await Assert.That(string.Join(",", (await host.Pad.ListAsync(false, null)).Select(x => x.Item.Id))).IsEqualTo(string.Join(",", c, a, b));
    }

    [Test]
    public async Task PadTags_FilterByTagGroup_AndSurviveTagDeleteRestoreAndMerge()
    {
        using var host = await Host.StartAsync();
        var garden = await host.NoteAsync("roses");
        var other = await host.NoteAsync("unrelated");
        var link = await host.Tags.LinkAsync(new(Op(), TagTargetKind.Pad, garden, null, "home/garden"));
        var home = (await host.Tags.ListAsync()).Single(x => x.Path == "home").Tag;

        // A tag group includes notes tagged with nested tags.
        await Assert.That((await host.Pad.ListAsync(false, home.Id)).Select(x => x.Item.Id)).IsEquivalentTo([garden]);

        var tag = (await host.Tags.ListAsync()).Single(x => x.Tag.Id == link.TagId).Tag;
        var deletion = await host.Tags.DeleteAsync(new(Op(), tag.Id, tag.Version));
        await Assert.That(await host.Tags.ListTargetTagsAsync(TagTargetKind.Pad, garden)).IsEmpty();
        await host.Tags.RestoreAsync(new(Op(), tag.Id, deletion.Tag.Version));
        await Assert.That(await host.Tags.ListTargetTagsAsync(TagTargetKind.Pad, garden)).IsEquivalentTo([tag.Id]);

        var target = (await host.Tags.CreateAsync(new(Op(), "plants", null, null))).Tag;
        await host.Tags.LinkAsync(new(Op(), TagTargetKind.Pad, other, target.Id, null));
        var source = (await host.Tags.ListAsync()).Single(x => x.Tag.Id == tag.Id).Tag;
        var preview = await host.Tags.PreviewMergeAsync(source.Id, target.Id);
        await Assert.That(preview.PadLinksMoved).IsEqualTo(1L);
        await host.Tags.MergeAsync(new(Op(), source.Id, source.Version, target.Id, target.Version));
        await Assert.That(await host.Tags.ListTargetTagsAsync(TagTargetKind.Pad, garden)).IsEquivalentTo([target.Id]);

        // Tagging a note is undoable like tagging a task.
        await host.Tags.LinkAsync(new(Op(), TagTargetKind.Pad, other, home.Id, null));
        await host.UndoLatestAsync();
        await Assert.That(await host.Tags.ListTargetTagsAsync(TagTargetKind.Pad, other)).IsEquivalentTo([target.Id]);
    }

    [Test]
    public async Task Search_FindsTypedPadText_IncludingArchived_ButNotDeletedOrAsTasks()
    {
        using var host = await Host.StartAsync();
        var kept = await host.NoteAsync("Quarterly «budget» draft");
        var archivedId = await host.NoteAsync("budget for the trip");
        var gone = await host.NoteAsync("budget leftovers");
        await host.Pad.ArchiveAsync(new(Op(), [new(archivedId, (await host.Pad.GetAsync(archivedId)).Item.Version)]));
        await host.Pad.DeleteAsync(new(Op(), gone, (await host.Pad.GetAsync(gone)).Item.Version));

        var found = await host.Pad.SearchAsync("budget", 10);

        await Assert.That(found.Select(x => x.Item.Id)).IsEquivalentTo([kept, archivedId]);
        var tasks = await host.Queries.RunAsync(new TaskQuerySpec { View = TaskView.Search, Text = "budget" });
        await Assert.That(tasks.Rows).IsEmpty();

        // The rebuild command keeps pad documents.
        await host.Queries.RebuildSearchIndexAsync();
        await Assert.That((await host.Pad.SearchAsync("trip", 10)).Select(x => x.Item.Id)).IsEquivalentTo([archivedId]);
    }

    [Test]
    public async Task WeeklyReview_SuggestsOldNotes_ArchivesTheChosenOnes_AndIsNotDueAgainForAWeek()
    {
        using var host = await Host.StartAsync();
        var old = await host.NoteAsync("old");
        var alsoOld = await host.NoteAsync("also old");
        host.Clock.Advance(TimeSpan.FromDays(8));
        var fresh = await host.NoteAsync("fresh");

        var status = await host.Pad.ReviewStatusAsync();
        await Assert.That(status.Due).IsTrue();
        await Assert.That(status.Suggestions.Select(x => x.Id)).IsEquivalentTo([old, alsoOld]);

        var oldItem = status.Suggestions.Single(x => x.Id == old);
        await host.Pad.CompleteReviewAsync(new(Op(), [new(old, oldItem.Version)]));

        await Assert.That((await host.Pad.ListAsync(false, null)).Select(x => x.Item.Id)).IsEquivalentTo([alsoOld, fresh]);
        var after = await host.Pad.ReviewStatusAsync();
        await Assert.That(after.Due).IsFalse();
        await Assert.That(after.LastReviewedAtUtc).IsEqualTo(host.Clock.Now);
        await Assert.That((await host.Undo.ListAsync(1)).Single().Label).IsEqualTo("Archive pad notes in review “old”");

        host.Clock.Advance(TimeSpan.FromDays(7));
        await Assert.That((await host.Pad.ReviewStatusAsync()).Due).IsTrue();
    }

    [Test]
    public async Task Shutdown_WaitsForTheWriteInProgress_AndRefusesLaterWrites()
    {
        var root = TestPaths.NewDataRoot();
        var host = await Host.StartAsync(root);
        var itemId = await host.NoteAsync("before close");
        var store = host.Storage.Store;
        host.Dispose();

        var refused = await ThrowsAsync(() => store.ExecuteAsync((session, ct) => session.Pad.GetAsync(itemId, ct)));
        await Assert.That(refused.Kind).IsEqualTo(AppErrorKind.StorageUnavailable);

        using var reopened = await Host.StartAsync(root);
        await Assert.That((await reopened.Pad.GetAsync(itemId)).Item.RawMarkdown).IsEqualTo("before close");
    }
}
