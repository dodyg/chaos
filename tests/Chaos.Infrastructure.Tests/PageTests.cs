using Chaos.Application.Abstractions;
using Chaos.Application.Capture;
using Chaos.Application.Contracts;
using Chaos.Application.History;
using Chaos.Application.Pad;
using Chaos.Application.Pages;
using Chaos.Application.Tags;
using Chaos.Application.Tasks;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Infrastructure.Tests.Testing;

namespace Chaos.Infrastructure.Tests;

/// <summary>
/// SPEC Pages against real SQLite: autosave with versions, the page tree (move, cycles,
/// delete and restore with undo), daily pages, front-matter tags, search, links and backlinks,
/// Make task with an anchor that survives edits, and moving pad notes to pages.
/// </summary>
public sealed class PageTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 14, 9, 0, 0, TimeSpan.Zero);

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
        public required ChaosStorageHandle Storage { get; init; }
        public required ManualClock Clock { get; init; }
        public required PageCommands Pages { get; init; }
        public required PadCommands Pad { get; init; }
        public required TagCommands Tags { get; init; }
        public required TaskCommands Tasks { get; init; }
        public required UndoCommands Undo { get; init; }

        public static async Task<Host> StartAsync(string? root = null)
        {
            root ??= TestPaths.NewDataRoot();
            var clock = new ManualClock(Start);
            var ids = new GuidIds();
            var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", ids, clock, CancellationToken.None);
            return new Host
            {
                Storage = storage,
                Clock = clock,
                Pages = new PageCommands(storage.Store, ids, clock),
                Pad = new PadCommands(storage.Store, ids, clock),
                Tags = new TagCommands(storage.Store, ids, clock),
                Tasks = new TaskCommands(storage.Store, ids, clock),
                Undo = new UndoCommands(storage.Store, ids, clock),
            };
        }

        public void Dispose() => Storage.Dispose();

        public async Task<PageRecord> PageAsync(string title, string markdown = "", string? parent = null) =>
            (await Pages.CreateAsync(new(Op(), parent, title, markdown))).Page;

        public async Task<UndoResult> UndoLatestAsync()
        {
            var latest = (await Undo.ListAsync(1)).Single();
            Clock.Advance();
            return await Undo.UndoAsync(new UndoOperationCommand(Op(), latest.OperationId));
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
    public async Task Autosave_IgnoresStaleRevisions_AndGroupsVersionsBySitting()
    {
        var root = TestPaths.NewDataRoot();
        string pageId;
        using (var host = await Host.StartAsync(root))
        {
            pageId = (await host.PageAsync("Kitchen renovation")).Id;
            host.Clock.Advance();
            await host.Pages.SaveAsync(new(pageId, 2, "Kitchen renovation", "two"));
            var late = await host.Pages.SaveAsync(new(pageId, 1, "Kitchen renovation", "one"));
            await Assert.That(late.Applied).IsFalse();
            await Assert.That(late.Page.RawMarkdown).IsEqualTo("two");

            host.Clock.Advance(TimeSpan.FromMinutes(3));
            await host.Pages.SaveAsync(new(pageId, 3, "Kitchen", "three"));
            host.Clock.Advance(TimeSpan.FromMinutes(30)); // a new sitting
            await host.Pages.SaveAsync(new(pageId, 4, "Kitchen", "four"));

            var versions = await host.Pages.ListRevisionsAsync(pageId);
            await Assert.That(versions.Select(v => v.RawMarkdown)).IsEquivalentTo(["four", "three"]);
            // Typing is not an undo step; creating the page is.
            await Assert.That((await host.Undo.ListAsync(10)).Select(entry => entry.Label)).IsEquivalentTo(["Add page “Kitchen renovation”"]);
        }

        using (var reopened = await Host.StartAsync(root))
        {
            var page = (await reopened.Pages.GetAsync(pageId)).Page;
            await Assert.That(page.Title).IsEqualTo("Kitchen");
            await Assert.That(page.RawMarkdown).IsEqualTo("four");
            await Assert.That(page.DraftVersion).IsEqualTo(4L);
        }
    }

    [Test]
    public async Task RestoreVersion_BringsBackOldText_AndUndoReturns()
    {
        using var host = await Host.StartAsync();
        var page = await host.PageAsync("Notes", "first draft");
        host.Clock.Advance(TimeSpan.FromHours(2));
        var saved = (await host.Pages.SaveAsync(new(page.Id, 1, "Notes", "second draft"))).Page;
        var first = (await host.Pages.ListRevisionsAsync(page.Id)).Last();

        var restored = (await host.Pages.RestoreVersionAsync(new(Op(), page.Id, saved.Version, first.Number))).Page;
        await Assert.That(restored.RawMarkdown).IsEqualTo("first draft");
        await Assert.That(restored.DraftVersion).IsGreaterThan(saved.DraftVersion);
        await Assert.That((await host.Pages.ListRevisionsAsync(page.Id)).Count).IsEqualTo(3);

        await host.UndoLatestAsync();
        await Assert.That((await host.Pages.GetAsync(page.Id)).Page.RawMarkdown).IsEqualTo("second draft");
    }

    [Test]
    public async Task Tree_MovesReorderAndRefusesCycles()
    {
        using var host = await Host.StartAsync();
        var home = await host.PageAsync("Home");
        var quotes = await host.PageAsync("Quotes", parent: home.Id);
        var plumbers = await host.PageAsync("Plumbers", parent: quotes.Id);
        var other = await host.PageAsync("Other");

        var cycle = await ThrowsAsync(() => host.Pages.MoveAsync(new(Op(), home.Id, home.Version, plumbers.Id, 0)));
        await Assert.That(cycle.Message).Contains("under itself");

        var moved = (await host.Pages.MoveAsync(new(Op(), other.Id, other.Version, home.Id, 0))).Page;
        await Assert.That(moved.ParentPageId).IsEqualTo(home.Id);
        var children = (await host.Pages.ListAsync()).Where(p => p.ParentPageId == home.Id).OrderBy(p => p.Position).Select(p => p.Title);
        await Assert.That(children).IsEquivalentTo(["Other", "Quotes"]);

        await host.UndoLatestAsync();
        await Assert.That((await host.Pages.GetAsync(other.Id)).Page.ParentPageId).IsNull();
    }

    [Test]
    public async Task Delete_TakesThePagesUnder_RestoreAndUndoBringThemBack()
    {
        using var host = await Host.StartAsync();
        var home = await host.PageAsync("Home");
        var quotes = await host.PageAsync("Quotes", parent: home.Id);
        await host.PageAsync("Plumbers", parent: quotes.Id);

        var deleted = await host.Pages.DeleteAsync(new(Op(), home.Id, home.Version));
        await Assert.That(deleted.Pages.Count).IsEqualTo(3);
        await Assert.That(await host.Pages.ListAsync()).IsEmpty();
        await Assert.That((await host.Pages.ListDeletedAsync()).Count).IsEqualTo(3);

        await host.UndoLatestAsync();
        await Assert.That((await host.Pages.ListAsync()).Count).IsEqualTo(3);

        var again = await host.Pages.DeleteAsync(new(Op(), home.Id, (await host.Pages.GetAsync(home.Id)).Page.Version));
        var root = again.Pages.Single(p => p.Id == home.Id);
        host.Clock.Advance();
        await host.Pages.RestoreAsync(new(Op(), home.Id, root.Version));
        await Assert.That((await host.Pages.ListAsync()).Select(p => p.Title)).IsEquivalentTo(["Home", "Quotes", "Plumbers"]);
    }

    [Test]
    public async Task RestoringAChildOfADeletedPage_PutsItAtTheTop()
    {
        using var host = await Host.StartAsync();
        var home = await host.PageAsync("Home");
        var quotes = await host.PageAsync("Quotes", parent: home.Id);
        await host.Pages.DeleteAsync(new(Op(), home.Id, home.Version));
        var deletedQuotes = (await host.Pages.ListDeletedAsync()).Single(p => p.Id == quotes.Id);

        await host.Pages.RestoreAsync(new(Op(), quotes.Id, deletedQuotes.Version));
        var restored = (await host.Pages.GetAsync(quotes.Id)).Page;
        await Assert.That(restored.DeletedAtUtc).IsNull();
        await Assert.That(restored.ParentPageId).IsNull();
    }

    [Test]
    public async Task DailyPages_OnePerDay_AppendAndList()
    {
        using var host = await Host.StartAsync();
        var day = new DateOnly(2026, 11, 14);
        await Assert.That(await host.Pages.GetDailyAsync(day)).IsNull();

        var created = (await host.Pages.CreateDailyAsync(new(Op(), day, "Good start."))).Page;
        var again = (await host.Pages.CreateDailyAsync(new(Op(), day, "ignored"))).Page;
        await Assert.That(again.Id).IsEqualTo(created.Id);
        await Assert.That(again.RawMarkdown).IsEqualTo("Good start.");

        await host.Pages.AppendToDailyAsync(new(Op(), day, "- Called the plumber"));
        var page = (await host.Pages.GetDailyAsync(day))!.Page;
        await Assert.That(page.RawMarkdown).IsEqualTo("Good start.\n\n- Called the plumber\n");
        await Assert.That(page.DailyDate).IsEqualTo(day);

        await host.Pages.AppendToDailyAsync(new(Op(), day.AddDays(1), "Tomorrow"));
        await Assert.That((await host.Pages.ListDailyAsync(null, null)).Select(p => p.DailyDate!.Value)).IsEquivalentTo([day.AddDays(1), day]);
        await Assert.That(await host.Pages.ListAsync()).IsEmpty(); // daily pages stay out of the tree

        var move = await ThrowsAsync(() => host.Pages.MoveAsync(new(Op(), page.Id, page.Version, null, 0)));
        await Assert.That(move.Message).Contains("Daily pages");
    }

    [Test]
    public async Task FrontMatter_TagsIconAndAliases_FeedTagsSearchAndLinks()
    {
        using var host = await Host.StartAsync();
        var home = (await host.Tags.CreateAsync(new(Op(), "home", null, null))).Tag;
        var renovation = (await host.Tags.CreateAsync(new(Op(), "home/renovation", null, null))).Tag;
        const string text = "---\nbackground: lighthouse\nicon: 🛠️\ntags: [home/renovation, nothere]\naliases:\n  - Kitchen\n---\n\n# Kitchen renovation\nTiles from Rome.";
        var page = await host.PageAsync("Kitchen renovation", text);

        var view = await host.Pages.GetAsync(page.Id);
        await Assert.That(view.Icon).IsEqualTo("🛠️");
        await Assert.That(view.TagIds).IsEquivalentTo([renovation.Id]);
        await Assert.That((await host.Pages.ListByTagAsync(home.Id)).Select(p => p.Id)).IsEquivalentTo([page.Id]);

        await Assert.That((await host.Pages.SearchAsync("tiles", 10)).Select(p => p.Id)).IsEquivalentTo([page.Id]);
        await Assert.That((await host.Pages.SearchAsync("lighthouse", 10)).Select(p => p.Id)).IsEquivalentTo([page.Id]);
        var names = await host.Pages.ResolveNamesAsync(["kitchen", "Nowhere"]);
        await Assert.That(names.Select(found => found.Target.Id)).IsEquivalentTo([page.Id]);

        // Broken YAML never locks the page: it saves and the properties are ignored.
        var saved = (await host.Pages.SaveAsync(new(page.Id, 1, "Kitchen renovation", "---\ntags: [home\n---\nbody"))).Page;
        await Assert.That(saved.RawMarkdown).StartsWith("---");
        await Assert.That((await host.Pages.GetAsync(page.Id)).TagIds).IsEmpty();
    }

    [Test]
    public async Task Backlinks_ComeFromPagesAndTasks_AndSurviveRenames()
    {
        using var host = await Host.StartAsync();
        var target = await host.PageAsync("Plumbers");
        await host.PageAsync("Meeting", $"Call one of the [plumbers](chaos:page/{target.Id}) today.");
        await host.PageAsync("Diary", "Remember [[plumbers]]");
        var task = (await host.Tasks.CaptureAsync(new CaptureTaskCommand(Op(), "", "Book the plumber", $"See [Plumbers](chaos:page/{target.Id})"))).Task;

        var backlinks = (await host.Pages.GetAsync(target.Id)).Backlinks;
        await Assert.That(backlinks.Select(link => link.Title)).IsEquivalentTo(["Meeting", "Diary", "Book the plumber"]);
        await Assert.That(backlinks.Single(link => link.Kind == "task").Id).IsEqualTo(task.Id);
        await Assert.That(backlinks.Single(link => link.Title == "Meeting").Line).IsEqualTo($"Call one of the [plumbers](chaos:page/{target.Id}) today.");

        await host.Pages.SaveAsync(new(target.Id, 1, "Trusted plumbers", ""));
        var resolved = await host.Pages.ResolveLinksAsync([target.Id]);
        await Assert.That(resolved.Single().Title).IsEqualTo("Trusted plumbers");

        var mentions = await host.Pages.MentionsOfTaskAsync(task.Id);
        await Assert.That(mentions).IsEmpty();
        await host.PageAsync("Plan", $"- [Book the plumber](chaos:task/{task.Id})");
        await Assert.That((await host.Pages.MentionsOfTaskAsync(task.Id)).Select(m => m.Title)).IsEquivalentTo(["Plan"]);
    }

    [Test]
    public async Task MakeTask_FromSelection_WithSubtasks_AnchorSurvivesEdits()
    {
        using var host = await Host.StartAsync();
        const string text = "# Meeting\n- Order tiles\n  - choose colour\n- Lunch";
        var page = await host.PageAsync("Meeting", text);
        const string quote = "Order tiles\n  - choose colour";
        var start = text.IndexOf(quote, StringComparison.Ordinal);
        var draft = new CaptureTaskDraft { Title = "Order tiles", Subtasks = [new CaptureSubtask("choose colour", false)] };

        var made = await host.Pages.MakeTaskAsync(new(Op(), page.Id, 0, start, start + quote.Length, quote, draft));
        await Assert.That(made.Task.Title).IsEqualTo("Order tiles");
        await Assert.That((await host.Pages.GetAsync(page.Id)).Tasks.Single().Link.TaskId).IsEqualTo(made.Task.Id);

        await host.Pages.SaveAsync(new(page.Id, 1, "Meeting", "Intro line\n" + text));
        var anchor = (await host.Pages.LocateAsync(made.Task.Id))!;
        await Assert.That(anchor.State).IsEqualTo(PadAnchorState.Relocated);
        await Assert.That(anchor.Start).IsEqualTo(start + "Intro line\n".Length);

        await host.Pages.SaveAsync(new(page.Id, 2, "Meeting", "all gone"));
        await Assert.That((await host.Pages.LocateAsync(made.Task.Id))!.State).IsEqualTo(PadAnchorState.Missing);
    }

    [Test]
    public async Task PadNote_MovesToAPage_OrAppendsToToday()
    {
        using var host = await Host.StartAsync();
        var note = (await host.Pad.CreateAsync(new(Op(), "## Recipe ideas\n- soup\n- bread"))).Item;
        var page = (await host.Pages.MovePadNoteAsync(new(Op(), note.Id, note.Version, null, null))).Page;
        await Assert.That(page.Title).IsEqualTo("Recipe ideas");
        await Assert.That(page.RawMarkdown).IsEqualTo("- soup\n- bread");
        await Assert.That((await host.Pad.GetAsync(note.Id)).Item.DeletedAtUtc).IsNotNull();

        await host.UndoLatestAsync();
        await Assert.That((await host.Pad.GetAsync(note.Id)).Item.DeletedAtUtc).IsNull();
        await Assert.That((await host.Pages.GetAsync(page.Id)).Page.DeletedAtUtc).IsNotNull();

        var current = (await host.Pad.GetAsync(note.Id)).Item;
        var day = new DateOnly(2026, 11, 14);
        await host.Pages.AppendPadNoteAsync(new(Op(), note.Id, current.Version, day));
        await Assert.That((await host.Pages.GetDailyAsync(day))!.Page.RawMarkdown).Contains("- soup");
        await Assert.That((await host.Pad.GetAsync(note.Id)).Item.ArchivedAtUtc).IsNotNull();
    }

    [Test]
    public async Task Export_KeepsFrontMatter_AndAddsChaosKeys()
    {
        var withFrontMatter = Transfer.PlainTextExport.PageMarkdown(new("11111111-2222-3333-4444-555555555555", "p", "Kitchen", "---\nicon: 🛠️\nfoo: bar # kept\n---\n\n# Kitchen\n", null, "c", "u"));
        await Assert.That(withFrontMatter).StartsWith("---\nicon: 🛠️\nfoo: bar # kept\nchaos:\n  id: \"11111111-2222-3333-4444-555555555555\"\n  title: \"Kitchen\"\n  parent: \"p\"\n");
        await Assert.That(withFrontMatter).EndsWith("---\n\n# Kitchen\n");

        var daily = Transfer.PlainTextExport.PageMarkdown(new("11111111-2222-3333-4444-555555555555", null, "2026-11-14", "Good day", "2026-11-14", "c", "u"));
        await Assert.That(daily).Contains("  daily: \"2026-11-14\"\n");
        await Assert.That(daily).EndsWith("---\n\nGood day\n");

        const string broken = "---\ntags: [x\n---\nbody";
        await Assert.That(Transfer.PlainTextExport.PageMarkdown(new("11111111-2222-3333-4444-555555555555", null, "B", broken, null, "c", "u"))).IsEqualTo(broken + "\n");
    }

    [Test]
    public async Task TitleAndBody_FromNoteText()
    {
        await Assert.That(PageCommands.TitleAndBody("# Trip\nPack bags", null)).IsEqualTo(("Trip", "Pack bags"));
        await Assert.That(PageCommands.TitleAndBody("- [ ] buy milk\nmore", null)).IsEqualTo(("buy milk", "- [ ] buy milk\nmore"));
        await Assert.That(PageCommands.TitleAndBody("text", "Given")).IsEqualTo(("Given", "text"));
    }
}
