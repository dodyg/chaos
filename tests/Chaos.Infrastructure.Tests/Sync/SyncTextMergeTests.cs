using Chaos.Application.Pages;
using Chaos.Infrastructure.Sqlite;
using Chaos.Infrastructure.Sync;
using Chaos.Infrastructure.Tests.DataProtection;
using static Chaos.Infrastructure.Tests.Sync.SyncTestHelpers;

namespace Chaos.Infrastructure.Tests.Sync;

/// <summary>Three-way merge of the same shared text edited on two devices at once (PLAN3 §9.9, decision E2).</summary>
public sealed class SyncTextMergeTests
{
    private sealed class SharedSeedIds : Chaos.Application.Abstractions.IIdGenerator
    {
        private int _seeded;

        public Guid NewId() => _seeded < 3 ? new Guid(_seeded++, 0, 0, new byte[8]) : Guid.NewGuid();
    }

    private static async Task<DataHost> StartAsync()
    {
        var host = await DataHost.StartAsync(ids: new SharedSeedIds());
        await EnableSyncAsync(host);
        return host;
    }

    private static async Task<IReadOnlyList<string>> OutboxAsync(DataHost host)
    {
        var payloads = new List<string>();
        await host.Store.QueryAsync((session, _) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = "SELECT PayloadJson FROM SyncOutbox ORDER BY Seq;";
            using var reader = command.ExecuteReader();
            while (reader.Read()) payloads.Add(reader.GetString(0));
            return Task.FromResult(0);
        }, CancellationToken.None);
        return payloads;
    }

    private static async Task ExchangeAsync(DataHost a, DataHost b)
    {
        var applierA = new SyncApplier(a.Store, a.Clock, a.Ids);
        var applierB = new SyncApplier(b.Store, b.Clock, b.Ids);
        for (var round = 0; round < 8; round++)
        {
            var toA = await applierA.ApplyAsync(await OutboxAsync(b), CancellationToken.None);
            var toB = await applierB.ApplyAsync(await OutboxAsync(a), CancellationToken.None);
            if (!toA.Changed && !toB.Changed) return;
        }

        throw new InvalidOperationException("The two data roots did not settle.");
    }

    /// <summary>What the engine does when the server accepts a pushed change: the text becomes the merge base.</summary>
    private static Task AcknowledgeAsync(DataHost host) =>
        host.Store.ExecuteAsync((session, _) =>
        {
            using var command = session.Connection().CreateCommand();
            command.CommandText = """
                INSERT OR REPLACE INTO TextBase (Kind, EntityKey, Hlc, Text)
                SELECT 'page', p.Id, (SELECT Hlc FROM SyncClocks c WHERE c.Kind='page' AND c.EntityKey=p.Id AND c.GroupName='text'), p.RawMarkdown FROM Pages p;
                """;
            command.ExecuteNonQuery();
            return Task.FromResult(0);
        }, CancellationToken.None);

    private static async Task<(DataHost A, DataHost B, string PageId)> SharedPageAsync(string text)
    {
        var a = await StartAsync();
        var b = await StartAsync();
        var page = (await new PageCommands(a.Store, a.Ids, a.Clock).CreateAsync(new(Op(), null, "Notes", text))).Page;
        await ExchangeAsync(a, b);
        await AcknowledgeAsync(a);
        await AcknowledgeAsync(b);
        return (a, b, page.Id);
    }

    private static async Task SaveAsync(DataHost host, string pageId, string text, TimeSpan later)
    {
        host.Clock.Now += later;
        var commands = new PageCommands(host.Store, host.Ids, host.Clock);
        var page = (await host.Store.QueryAsync((session, token) => session.Pages.GetIncludingDeletedAsync(pageId, token), CancellationToken.None))!;
        await commands.SaveAsync(new(pageId, page.DraftVersion + 1, page.Title, text));
    }

    private static Task<string> PageTextAsync(DataHost host, string pageId) =>
        TextAsync(host, $"SELECT RawMarkdown FROM Pages WHERE Id = '{pageId}';");

    [Test]
    public async Task Edits_to_different_paragraphs_of_one_page_merge_cleanly_on_both_devices()
    {
        var (a, b, id) = await SharedPageAsync("one\n\ntwo\n\nthree\n\nfour\n");
        using (a)
        using (b)
        {
            await SaveAsync(a, id, "ONE\n\ntwo\n\nthree\n\nfour\n", TimeSpan.FromMinutes(1));
            await SaveAsync(b, id, "one\n\ntwo\n\nthree\n\nFOUR\n", TimeSpan.FromMinutes(2));
            await ExchangeAsync(a, b);
            var onA = await PageTextAsync(a, id);
            var onB = await PageTextAsync(b, id);
            await Assert.That(onA).IsEqualTo("ONE\n\ntwo\n\nthree\n\nFOUR\n");
            await Assert.That(onB).IsEqualTo(onA);
        }
    }

    [Test]
    public async Task The_merge_is_the_same_whichever_device_had_the_newer_clock()
    {
        var (a, b, id) = await SharedPageAsync("one\n\ntwo\n\nthree\n");
        using (a)
        using (b)
        {
            await SaveAsync(b, id, "one\n\ntwo\n\nTHREE\n", TimeSpan.FromMinutes(1));
            await SaveAsync(a, id, "ONE\n\ntwo\n\nthree\n", TimeSpan.FromMinutes(2));
            await ExchangeAsync(a, b);
            await Assert.That(await PageTextAsync(a, id)).IsEqualTo("ONE\n\ntwo\n\nTHREE\n");
            await Assert.That(await PageTextAsync(b, id)).IsEqualTo("ONE\n\ntwo\n\nTHREE\n");
        }
    }

    [Test]
    public async Task Same_line_edits_leave_identical_markers_on_both_devices()
    {
        var (a, b, id) = await SharedPageAsync("intro\nthe date is TBD\noutro\n");
        using (a)
        using (b)
        {
            await SaveAsync(a, id, "intro\nthe date is Monday\noutro\n", TimeSpan.FromMinutes(1));
            await SaveAsync(b, id, "intro\nthe date is Friday\noutro\n", TimeSpan.FromMinutes(2));
            await ExchangeAsync(a, b);
            var onA = await PageTextAsync(a, id);
            var onB = await PageTextAsync(b, id);
            await Assert.That(onB).IsEqualTo(onA);
            await Assert.That(onA).Contains("<<<<<<< ").And.Contains("=======\n").And.Contains(">>>>>>> ");
            await Assert.That(onA).Contains("the date is Monday\n").And.Contains("the date is Friday\n");
            await Assert.That(onA).StartsWith("intro\n").And.EndsWith("outro\n");
        }
    }

    [Test]
    public async Task A_remote_edit_with_no_local_edit_applies_without_a_merge()
    {
        var (a, b, id) = await SharedPageAsync("one\ntwo\n");
        using (a)
        using (b)
        {
            await SaveAsync(a, id, "one\ntwo\nthree\n", TimeSpan.FromMinutes(1));
            await ExchangeAsync(a, b);
            await Assert.That(await PageTextAsync(b, id)).IsEqualTo("one\ntwo\nthree\n");
            await Assert.That(await CountAsync(b, "SELECT count(*) FROM SyncOutbox WHERE Kind='page' AND Op='upsert';")).IsEqualTo(0);
        }
    }

    [Test]
    public async Task A_merge_keeps_the_remote_text_as_a_page_version()
    {
        var (a, b, id) = await SharedPageAsync("one\n\ntwo\n");
        using (a)
        using (b)
        {
            await SaveAsync(a, id, "ONE\n\ntwo\n", TimeSpan.FromMinutes(1));
            await SaveAsync(b, id, "one\n\nTWO\n", TimeSpan.FromMinutes(2));
            await ExchangeAsync(a, b);
            await Assert.That(await CountAsync(a, $"SELECT count(*) FROM PageRevisions WHERE PageId='{id}' AND RawMarkdown='one\n\nTWO\n';")).IsEqualTo(1);
        }
    }

    private const string Mine = "{\"v\":2,\"strokes\":[{\"id\":\"a\",\"createdAt\":2,\"tool\":\"pen\",\"color\":\"#111827\",\"size\":4,\"points\":[[0,0,0.5]]}],\"erased\":[]}";
    private const string Yours = "{\"v\":2,\"strokes\":[{\"id\":\"b\",\"createdAt\":1,\"tool\":\"pen\",\"color\":\"#111827\",\"size\":4,\"points\":[[9,9,0.5]]}],\"erased\":[]}";

    [Test]
    public async Task Drawings_from_two_members_both_survive_on_both_devices()
    {
        var a = await StartAsync();
        var b = await StartAsync();
        using (a)
        using (b)
        {
            var pads = new Chaos.Application.Pad.PadCommands(a.Store, a.Ids, a.Clock);
            var item = (await pads.CreateDrawingAsync(new(Op()))).Item;
            await ExchangeAsync(a, b);
            var padsB = new Chaos.Application.Pad.PadCommands(b.Store, b.Ids, b.Clock);
            a.Clock.Now += TimeSpan.FromMinutes(1);
            b.Clock.Now += TimeSpan.FromMinutes(2);
            await pads.SaveDrawingAsync(new(item.Id, 100, Mine));
            await padsB.SaveDrawingAsync(new(item.Id, 100, Yours));
            await ExchangeAsync(a, b);

            var onA = await TextAsync(a, $"SELECT StrokesJson FROM PadDrawings WHERE PadItemId='{item.Id}';");
            var onB = await TextAsync(b, $"SELECT StrokesJson FROM PadDrawings WHERE PadItemId='{item.Id}';");
            await Assert.That(onB).IsEqualTo(onA);
            await Assert.That(onA).Contains("\"id\":\"a\"").And.Contains("\"id\":\"b\"");
            // Ordered by creation time, not by whose clock won.
            await Assert.That(onA.IndexOf("\"id\":\"b\"", StringComparison.Ordinal)).IsLessThan(onA.IndexOf("\"id\":\"a\"", StringComparison.Ordinal));
        }
    }

    [Test]
    public async Task A_stroke_erased_on_one_device_stays_erased_after_a_concurrent_edit_on_the_other()
    {
        var a = await StartAsync();
        var b = await StartAsync();
        using (a)
        using (b)
        {
            var pads = new Chaos.Application.Pad.PadCommands(a.Store, a.Ids, a.Clock);
            var item = (await pads.CreateDrawingAsync(new(Op()))).Item;
            await pads.SaveDrawingAsync(new(item.Id, 100, Mine));
            await ExchangeAsync(a, b);
            var padsB = new Chaos.Application.Pad.PadCommands(b.Store, b.Ids, b.Clock);
            a.Clock.Now += TimeSpan.FromMinutes(1);
            b.Clock.Now += TimeSpan.FromMinutes(2);
            await pads.SaveDrawingAsync(new(item.Id, 101, "{\"v\":2,\"strokes\":[],\"erased\":[\"a\"]}"));
            await padsB.SaveDrawingAsync(new(item.Id, 101, Mine.Replace("\"erased\":[]", "\"erased\":[]").Replace("[[0,0,0.5]]", "[[0,0,0.5]]")));
            await ExchangeAsync(a, b);
            var onA = await TextAsync(a, $"SELECT StrokesJson FROM PadDrawings WHERE PadItemId='{item.Id}';");
            var onB = await TextAsync(b, $"SELECT StrokesJson FROM PadDrawings WHERE PadItemId='{item.Id}';");
            await Assert.That(onB).IsEqualTo(onA);
            await Assert.That(onA).DoesNotContain("\"id\":\"a\"").And.Contains("\"erased\":[\"a\"]");
        }
    }
}
