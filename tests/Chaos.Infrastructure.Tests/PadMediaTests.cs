using System.IO.Compression;
using System.Text;
using Chaos.Application.Abstractions;
using Chaos.Application.Attachments;
using Chaos.Application.Contracts;
using Chaos.Application.Pad;
using Chaos.Infrastructure.Tests.DataProtection;
using Chaos.Infrastructure.Transfer;

namespace Chaos.Infrastructure.Tests;

/// <summary>PLAN2 P10 against real SQLite: drawing previews, purge, archive round trip and Markdown export.</summary>
public sealed class PadMediaTests
{
    private const string Strokes = "{\"v\":1,\"strokes\":[{\"tool\":\"pen\",\"color\":\"#111827\",\"size\":6,\"points\":[[10,10,0.5],[50,60,0.5]]}]}";

    private static byte[] Png(byte fill)
    {
        var bytes = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        bytes[11] = 13;
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), 8);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), 8);
        bytes[40] = fill;
        return bytes;
    }

    private static async Task<AttachmentRecord> AttachAsync(DataHost host, string padId, string name, byte[] bytes)
    {
        var upload = await host.Attachments.BeginAsync(new BeginAttachmentUploadCommand(AttachmentOwnerKind.Pad, padId, name, bytes.Length));
        await host.Attachments.ChunkAsync(upload.UploadId, 0, bytes);
        return (await host.Attachments.CommitAsync(new CommitAttachmentCommand(DataHost.Op(), upload.UploadId))).Attachment;
    }

    private static PadCommands PadOf(DataHost host) => new(host.Store, host.Ids, host.Clock);

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
    public async Task Preview_MustBeAPngOfThisDrawing_AndANewPreviewRetiresTheOldOne()
    {
        using var host = await DataHost.StartAsync();
        var pad = PadOf(host);
        var item = (await pad.CreateDrawingAsync(new(DataHost.Op()))).Item;
        var other = (await pad.CreateDrawingAsync(new(DataHost.Op()))).Item;
        await pad.SaveDrawingAsync(new(item.Id, 1, Strokes));

        var text = await AttachAsync(host, item.Id, "note.txt", "hello"u8.ToArray());
        await Assert.That((await ThrowsAsync(() => pad.SaveDrawingAsync(new(item.Id, 1, Strokes, text.Id)))).Kind).IsEqualTo(AppErrorKind.Validation);
        var foreign = await AttachAsync(host, other.Id, "other.png", Png(1));
        await Assert.That((await ThrowsAsync(() => pad.SaveDrawingAsync(new(item.Id, 1, Strokes, foreign.Id)))).Kind).IsEqualTo(AppErrorKind.Validation);

        var first = await AttachAsync(host, item.Id, "drawing-preview.png", Png(2));
        var saved = await pad.SaveDrawingAsync(new(item.Id, 1, Strokes, first.Id));
        await Assert.That(saved.Drawing.PreviewAttachmentId).IsEqualTo(first.Id);
        await Assert.That(saved.Applied).IsFalse(); // same revision: only the preview changed
        await Assert.That(saved.Drawing.StrokesJson).IsEqualTo(Strokes);

        var second = await AttachAsync(host, item.Id, "drawing-preview.png", Png(3));
        await pad.SaveDrawingAsync(new(item.Id, 1, Strokes, second.Id));
        var retired = await host.Store.QueryAsync((session, token) => session.Attachments.GetIncludingDeletedAsync(first.Id, token));
        await Assert.That(retired!.DeletedAtUtc).IsNotNull();
        await Assert.That((await host.Attachments.GetAsync(second.Id)).Attachment.DeletedAtUtc).IsNull();
        await Assert.That((await pad.GetDrawingAsync(item.Id)).Drawing.PreviewAttachmentId).IsEqualTo(second.Id);
    }

    [Test]
    public async Task Purging_ADeletedDrawing_RemovesItsStrokesRegionLinksAndPreview()
    {
        using var host = await DataHost.StartAsync();
        var pad = PadOf(host);
        var item = (await pad.CreateDrawingAsync(new(DataHost.Op(), 400, 300))).Item;
        await pad.SaveDrawingAsync(new(item.Id, 1, Strokes));
        var preview = await AttachAsync(host, item.Id, "drawing-preview.png", Png(4));
        await pad.SaveDrawingAsync(new(item.Id, 1, Strokes, preview.Id));
        await pad.MakeTaskFromRegionAsync(new(DataHost.Op(), item.Id, 1, new PadRegion(10, 10, 100, 100), "Region task", null, null, null, null, []));

        var current = (await pad.GetAsync(item.Id)).Item;
        await pad.DeleteAsync(new(DataHost.Op(), item.Id, current.Version));
        var counts = await host.Purge.PurgeAsync(null, CancellationToken.None);

        await Assert.That(counts.Removed.PadItems).IsEqualTo(1L);
        await Assert.That(await host.Files.ExistsAsync(preview.Sha256, CancellationToken.None)).IsFalse();
        await Assert.That((await ThrowsAsync(() => pad.GetDrawingAsync(item.Id))).Kind).IsEqualTo(AppErrorKind.NotFound);
    }

    [Test]
    public async Task JsonArchive_RoundTripsDrawingsRegionLinksAndPreviews()
    {
        using var source = await DataHost.StartAsync();
        var pad = PadOf(source);
        var item = (await pad.CreateDrawingAsync(new(DataHost.Op(), 640, 480))).Item;
        await pad.SaveDrawingAsync(new(item.Id, 3, Strokes));
        var preview = await AttachAsync(source, item.Id, "drawing-preview.png", Png(5));
        await pad.SaveDrawingAsync(new(item.Id, 3, Strokes, preview.Id));
        var made = await pad.MakeTaskFromRegionAsync(new(DataHost.Op(), item.Id, 3, new PadRegion(20, 30, 200, 120), "From the sketch", null, null, null, null, []));

        var export = await source.Transfer.ExportAsync(ExportFormat.Json, Path.Combine(source.Root, "out"), CancellationToken.None);
        ArchiveContent archive;
        await using (var stream = File.OpenRead(export.Path))
        {
            archive = JsonArchive.Read(stream);
        }

        await Assert.That(archive.Manifest.Tables["PadDrawings"]).IsEqualTo(1L);

        using var target = await DataHost.StartAsync();
        await target.Protection.RestoreArchiveAsync(archive, export.FileName, CancellationToken.None);

        var restored = PadOf(target);
        var drawing = await restored.GetDrawingAsync(item.Id);
        await Assert.That(drawing.Item.Kind).IsEqualTo(PadItemKind.Drawing);
        await Assert.That(drawing.Drawing.StrokesJson).IsEqualTo(Strokes);
        await Assert.That(drawing.Drawing.Width).IsEqualTo(640);
        await Assert.That(drawing.Drawing.Revision).IsEqualTo(3L);
        await Assert.That(drawing.Drawing.PreviewAttachmentId).IsEqualTo(preview.Id);
        await Assert.That((await target.Attachments.GetAsync(preview.Id)).Bytes).IsEqualTo(AttachmentBytesState.Present);
        var anchor = (await restored.LocateAsync(made.Task.Id))!;
        await Assert.That(anchor.State).IsEqualTo(PadAnchorState.Exact);
        await Assert.That(anchor.Region).IsEqualTo(new PadRegion(20, 30, 200, 120));
    }

    [Test]
    public async Task MarkdownExport_WritesTheDrawingPreviewAndTheStrokesJsonBesideTheNote()
    {
        using var host = await DataHost.StartAsync();
        var pad = PadOf(host);
        var item = (await pad.CreateDrawingAsync(new(DataHost.Op(), 640, 480))).Item;
        await pad.SaveDrawingAsync(new(item.Id, 1, Strokes));
        var preview = await AttachAsync(host, item.Id, "drawing-preview.png", Png(6));
        await pad.SaveDrawingAsync(new(item.Id, 1, Strokes, preview.Id));
        await pad.CreateDrawingAsync(new(DataHost.Op())); // an empty drawing still exports

        var export = await host.Transfer.ExportAsync(ExportFormat.Markdown, Path.Combine(host.Root, "out"), CancellationToken.None);

        using var zip = ZipFile.OpenRead(export.Path);
        string Read(ZipArchiveEntry entry)
        {
            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            return reader.ReadToEnd();
        }

        var notes = zip.Entries.Where(entry => entry.FullName.StartsWith("pad/", StringComparison.Ordinal) && entry.FullName.EndsWith(".md", StringComparison.Ordinal)).ToList();
        await Assert.That(notes.Count).IsEqualTo(2);
        var withPreview = notes.Single(entry => Read(entry).Contains("![Drawing]"));
        var text = Read(withPreview);
        await Assert.That(text).Contains("kind: \"drawing\"");
        await Assert.That(text).Contains("width: 640");
        await Assert.That(text).Contains($"](../attachments/{preview.Id}-drawing-preview.png)");
        await Assert.That(text).DoesNotContain("chaos:attachment/");
        var strokes = zip.Entries.Single(entry => entry.FullName == withPreview.FullName[..^3] + ".strokes.json");
        await Assert.That(Read(strokes)).IsEqualTo(Strokes);
        await Assert.That(notes.Any(entry => Read(entry).Contains("(An empty drawing.)"))).IsTrue();
        await Assert.That(zip.Entries.Any(entry => entry.FullName == $"attachments/{preview.Id}-drawing-preview.png")).IsTrue();
    }
}
