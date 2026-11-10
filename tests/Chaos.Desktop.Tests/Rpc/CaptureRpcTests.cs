using Chaos.Application.Capture;
using Chaos.Application.Tasks;
using Chaos.Desktop.Services;
using Chaos.Desktop.Tests.Testing;
using Chaos.Infrastructure.Bootstrap;

namespace Chaos.Desktop.Tests.Rpc;

/// <summary>
/// PLAN §15 WP7 over RPC: the preview maps tokens back to offsets in the typed text, a
/// disabled token stays in the title, the confirmed draft is saved without reparsing and
/// survives restart, and invalid drafts come back as line-addressed field errors.
/// </summary>
public sealed class CaptureRpcTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 9, 0, 0, TimeSpan.Zero);

    private sealed class GuidIds : Chaos.Application.Abstractions.IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }

    private static (CaptureRpcService Capture, TaskRpcService Tasks) Wire(ChaosStorageHandle storage)
    {
        var ids = new GuidIds();
        var clock = new FixedTimeProvider(Now);
        return (new CaptureRpcService(new CaptureCommands(storage.Store, ids, clock)), new TaskRpcService(new TaskCommands(storage.Store, ids, clock)));
    }

    private static Task<ChaosStorageHandle> OpenAsync(string root) =>
        StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(root), "0.1.0", new GuidIds(), new FixedTimeProvider(Now), CancellationToken.None);

    [Test]
    public async Task PreviewDisableTokenEditCreate_SavesShownDraftAcrossRestart()
    {
        var root = TestPaths.NewDataRoot();
        string taskId;
        const string text = "Meet Friday #team";
        using (var storage = await OpenAsync(root))
        {
            var (capture, _) = Wire(storage);
            var start = text.IndexOf("Friday", StringComparison.Ordinal);
            var preview = await capture.PreviewAsync(new(text, [new(start, start + "Friday".Length)]), CancellationToken.None);
            var item = preview.Items.Single();
            await Assert.That(preview.Ok).IsTrue();
            await Assert.That(item.Draft.Title).IsEqualTo("Meet Friday");
            await Assert.That(item.Draft.PlannedDate).IsNull();
            await Assert.That(item.Tokens.Single().Kind).IsEqualTo("Tag");

            var edited = item.Draft with { Title = "Meet the team on Friday", Priority = "High" };
            var created = await capture.CreateAsync(new(Guid.NewGuid().ToString(), [edited]), CancellationToken.None);
            await Assert.That(created.Ok).IsTrue();
            taskId = created.Tasks.Single().Id;
        }

        using var reopened = await OpenAsync(root);
        var (_, tasks) = Wire(reopened);
        var saved = await tasks.GetAsync(new(taskId), CancellationToken.None);
        await Assert.That(saved.Task!.Title).IsEqualTo("Meet the team on Friday");
        await Assert.That(saved.Task.Priority).IsEqualTo("High");
    }

    [Test]
    public async Task Preview_PastedLines_TokenOffsetsPointIntoTypedText()
    {
        using var storage = await OpenAsync(TestPaths.NewDataRoot());
        var (capture, _) = Wire(storage);
        const string text = "One\n  - sub\nTwo tomorrow";
        var preview = await capture.PreviewAsync(new(text, null), CancellationToken.None);
        await Assert.That(preview.IsBatch).IsTrue();
        await Assert.That(preview.Items[0].Draft.Subtasks.Single().Title).IsEqualTo("sub");
        var second = preview.Items[1];
        var token = second.Tokens.Single();
        await Assert.That(text[(second.SourceStart + token.Start)..(second.SourceStart + token.End)]).IsEqualTo("tomorrow");
        await Assert.That(second.LineNumber).IsEqualTo(3);
    }

    [Test]
    public async Task Create_InvalidDateOnSecondDraft_ReturnsFieldErrorAndSavesNothing()
    {
        using var storage = await OpenAsync(TestPaths.NewDataRoot());
        var (capture, tasks) = Wire(storage);
        var good = (await capture.PreviewAsync(new("Fine", null), CancellationToken.None)).Items.Single().Draft;
        var bad = good with { Title = "Broken", PlannedDate = "2026-13-40" };

        var result = await capture.CreateAsync(new(Guid.NewGuid().ToString(), [good, bad]), CancellationToken.None);

        await Assert.That(result.Ok).IsFalse();
        await Assert.That(result.Error!.Kind).IsEqualTo("validation");
        await Assert.That(result.Error.Fields.Single().FieldPath).IsEqualTo("drafts[1].plannedDate");
        await Assert.That((await tasks.ListInboxAsync(new(), CancellationToken.None)).Tasks.Count).IsEqualTo(0);
    }
}
