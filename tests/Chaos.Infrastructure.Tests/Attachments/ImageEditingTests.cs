using Chaos.Application.Abstractions;
using Chaos.Application.Attachments;
using Chaos.Application.Contracts;
using Chaos.Application.History;
using Chaos.Application.Tasks;
using Chaos.Infrastructure.Tests.DataProtection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace Chaos.Infrastructure.Tests.Attachments;

/// <summary>PLAN2 P9 follow-up: crop, rotate, flip, resize and thumbnails through the image processor.</summary>
public sealed class ImageEditingTests
{
    private static byte[] Make(int width, int height, Action<Image<Rgba32>>? paint = null, string format = "png")
    {
        using var image = new Image<Rgba32>(width, height, new Rgba32(10, 20, 30, 255));
        paint?.Invoke(image);
        using var output = new MemoryStream();
        switch (format)
        {
            case "jpeg": image.Save(output, new JpegEncoder()); break;
            case "gif": image.Save(output, new GifEncoder()); break;
            default: image.Save(output, new PngEncoder()); break;
        }

        return output.ToArray();
    }

    private static async Task<string> NewTaskAsync(DataHost host)
    {
        var inbox = await host.Store.QueryAsync(async (session, token) =>
        {
            var profile = await session.Profiles.GetPrimaryAsync(token);
            var space = await session.Spaces.GetPersonalByOwnerAsync(profile!.Id, token);
            return (await session.Containers.GetInboxAsync(space!.Id, token))!.Id;
        });
        return (await host.Tasks.CaptureAsync(new CaptureTaskCommand(DataHost.Op(), inbox, "Owner", null))).Task.Id;
    }

    private static async Task<AttachmentRecord> AddAsync(DataHost host, string task, string name, byte[] bytes)
    {
        var upload = await host.Attachments.BeginAsync(new BeginAttachmentUploadCommand(AttachmentOwnerKind.Task, task, name, bytes.Length));
        for (int offset = 0, index = 0; offset < bytes.Length; offset += upload.ChunkBytes, index++)
        {
            await host.Attachments.ChunkAsync(upload.UploadId, index, bytes[offset..Math.Min(bytes.Length, offset + upload.ChunkBytes)]);
        }

        return (await host.Attachments.CommitAsync(new CommitAttachmentCommand(DataHost.Op(), upload.UploadId))).Attachment;
    }

    private static async Task<(int Width, int Height)> SizeOfAsync(DataHost host, AttachmentRecord row)
    {
        await using var stream = (await host.Files.OpenReadAsync(row.Sha256, 0, CancellationToken.None))!;
        var info = await Image.IdentifyAsync(stream);
        return (info.Width, info.Height);
    }

    [Test]
    public async Task Edit_FlipsRotatesCropsAndShrinks_InThatOrder_AndRecordsTheNewSize()
    {
        using var host = await DataHost.StartAsync();
        var task = await NewTaskAsync(host);
        var original = await AddAsync(host, task, "wide.png", Make(200, 100));

        // Rotating 90 degrees makes it 100 x 200; the crop is in that rotated image.
        var edited = (await host.Attachments.EditImageAsync(new EditImageCommand(DataHost.Op(), original.Id, original.Version,
            new ImageEdit(false, false, 90, new ImageCrop(10, 20, 60, 120), null)))).Attachment;
        await Assert.That(edited.Width).IsEqualTo(60);
        await Assert.That(edited.Height).IsEqualTo(120);
        await Assert.That(edited.Sha256).IsNotEqualTo(original.Sha256);
        await Assert.That(edited.Version).IsEqualTo(original.Version + 1);
        await Assert.That(await SizeOfAsync(host, edited)).IsEqualTo((60, 120));
        await Assert.That(edited.ByteSize).IsEqualTo((await host.Files.GetInfoAsync(edited.Sha256, CancellationToken.None))!.Size);

        var shrunk = (await host.Attachments.EditImageAsync(new EditImageCommand(DataHost.Op(), original.Id, edited.Version, new ImageEdit(false, false, 0, null, 60)))).Attachment;
        await Assert.That((shrunk.Width, shrunk.Height)).IsEqualTo((30, 60));
        await Assert.That((await host.Attachments.GetAsync(original.Id)).Preview).IsEqualTo(AttachmentPreview.Image);
    }

    [Test]
    public async Task Edit_FlipMovesPixels_AndUndoBringsTheOriginalFileBack()
    {
        using var host = await DataHost.StartAsync();
        var task = await NewTaskAsync(host);
        var original = await AddAsync(host, task, "dot.png", Make(4, 2, image => image[0, 0] = new Rgba32(255, 0, 0, 255)));
        var operation = DataHost.Op();

        var edited = (await host.Attachments.EditImageAsync(new EditImageCommand(operation, original.Id, original.Version, new ImageEdit(true, false, 0, null, null)))).Attachment;
        await using (var stream = (await host.Files.OpenReadAsync(edited.Sha256, 0, CancellationToken.None))!)
        {
            using var pixels = await Image.LoadAsync<Rgba32>(stream);
            await Assert.That(pixels[3, 0]).IsEqualTo(new Rgba32(255, 0, 0, 255));
            await Assert.That(pixels[0, 0]).IsEqualTo(new Rgba32(10, 20, 30, 255));
        }

        await new UndoCommands(host.Store, host.Ids, host.Clock).UndoAsync(new(DataHost.Op(), operation));
        var back = (await host.Attachments.GetAsync(original.Id)).Attachment;
        await Assert.That(back.Sha256).IsEqualTo(original.Sha256);
        await Assert.That(await host.Files.ExistsAsync(original.Sha256, CancellationToken.None)).IsTrue();
        await Assert.That((back.Width, back.Height)).IsEqualTo((4, 2));
    }

    [Test]
    public async Task Edit_RefusesBadRequests_AndRepeatsOfTheSameOperationReplay()
    {
        using var host = await DataHost.StartAsync();
        var task = await NewTaskAsync(host);
        var image = await AddAsync(host, task, "a.png", Make(40, 40));
        var text = await AddAsync(host, task, "a.txt", "hello"u8.ToArray());
        ImageEdit Rotate(int degrees) => new(false, false, degrees, null, null);

        await Assert.That(async () => await host.Attachments.EditImageAsync(new EditImageCommand(DataHost.Op(), image.Id, image.Version, Rotate(45)))).Throws<AppException>();
        await Assert.That(async () => await host.Attachments.EditImageAsync(new EditImageCommand(DataHost.Op(), image.Id, image.Version, Rotate(0)))).Throws<AppException>(); // nothing to change
        await Assert.That(async () => await host.Attachments.EditImageAsync(new EditImageCommand(DataHost.Op(), image.Id, image.Version, new ImageEdit(false, false, 0, new ImageCrop(30, 30, 20, 20), null)))).Throws<AppException>();
        await Assert.That(async () => await host.Attachments.EditImageAsync(new EditImageCommand(DataHost.Op(), image.Id, image.Version, new ImageEdit(false, false, 0, new ImageCrop(0, 0, 0, 10), null)))).Throws<AppException>();
        await Assert.That(async () => await host.Attachments.EditImageAsync(new EditImageCommand(DataHost.Op(), image.Id, image.Version, new ImageEdit(false, false, 0, null, 4)))).Throws<AppException>();
        await Assert.That(async () => await host.Attachments.EditImageAsync(new EditImageCommand(DataHost.Op(), text.Id, text.Version, Rotate(90)))).Throws<AppException>();
        await Assert.That(async () => await host.Attachments.EditImageAsync(new EditImageCommand(DataHost.Op(), image.Id, image.Version + 5, Rotate(90)))).Throws<AppException>();

        var operation = DataHost.Op();
        var first = await host.Attachments.EditImageAsync(new EditImageCommand(operation, image.Id, image.Version, Rotate(180)));
        var again = await host.Attachments.EditImageAsync(new EditImageCommand(operation, image.Id, image.Version, Rotate(180)));
        await Assert.That(again.Replayed).IsTrue();
        await Assert.That(again.Attachment.Version).IsEqualTo(first.Attachment.Version);
    }

    [Test]
    public async Task Edit_RefusesAnimatedGifs_AndTurnsAStillGifIntoAPng()
    {
        using var host = await DataHost.StartAsync();
        var task = await NewTaskAsync(host);

        byte[] animated;
        using (var image = new Image<Rgba32>(8, 8, new Rgba32(1, 2, 3, 255)))
        {
            using var second = new Image<Rgba32>(8, 8, new Rgba32(9, 9, 9, 255));
            image.Frames.AddFrame(second.Frames.RootFrame);
            using var output = new MemoryStream();
            image.Save(output, new GifEncoder());
            animated = output.ToArray();
        }

        var moving = await AddAsync(host, task, "moving.gif", animated);
        var refusal = await Assert.That(async () => await host.Attachments.EditImageAsync(new EditImageCommand(DataHost.Op(), moving.Id, moving.Version, new ImageEdit(false, false, 90, null, null)))).Throws<AppException>();
        await Assert.That(refusal!.Message).Contains("Animated");

        var still = await AddAsync(host, task, "still.gif", Make(8, 6, null, "gif"));
        var converted = (await host.Attachments.EditImageAsync(new EditImageCommand(DataHost.Op(), still.Id, still.Version, new ImageEdit(false, false, 90, null, null)))).Attachment;
        await Assert.That(converted.MediaType).IsEqualTo("image/png");
        await Assert.That(converted.FileName).IsEqualTo("still.png");
        await Assert.That((converted.Width, converted.Height)).IsEqualTo((6, 8));
    }

    [Test]
    public async Task Thumbnail_IsASmallUprightJpeg_KeptAsALocalFile_AndOnlyForImages()
    {
        using var host = await DataHost.StartAsync();
        var task = await NewTaskAsync(host);
        var big = await AddAsync(host, task, "big.png", Make(1600, 800));
        var text = await AddAsync(host, task, "a.txt", "hello"u8.ToArray());

        var thumbnail = await host.Attachments.ThumbnailAsync(big.Id);
        using (var decoded = Image.Load(thumbnail))
        {
            await Assert.That((decoded.Width, decoded.Height)).IsEqualTo((AttachmentCommands.ThumbnailSide, AttachmentCommands.ThumbnailSide / 2));
        }

        await Assert.That(thumbnail.Take(3).ToArray()).IsEquivalentTo(new byte[] { 0xFF, 0xD8, 0xFF });
        var cached = Path.Combine(host.Storage.Options.ThumbnailsDirectory, $"{big.Sha256}-{AttachmentCommands.ThumbnailSide}.jpg");
        await Assert.That(File.Exists(cached)).IsTrue();
        await File.WriteAllBytesAsync(cached, [1, 2, 3]); // a second call serves the cache, it does not decode again
        await Assert.That((await host.Attachments.ThumbnailAsync(big.Id)).Length).IsEqualTo(3);
        await Assert.That(async () => await host.Attachments.ThumbnailAsync(text.Id)).Throws<AppException>();
    }

    [Test]
    public async Task Purge_AlsoClearsOldFilesAnEditLeftBehind_ButNotYoungOnesOrReferencedOnes()
    {
        using var host = await DataHost.StartAsync();
        var task = await NewTaskAsync(host);
        var original = await AddAsync(host, task, "a.png", Make(30, 30));
        var edited = (await host.Attachments.EditImageAsync(new EditImageCommand(DataHost.Op(), original.Id, original.Version, new ImageEdit(false, false, 90, new ImageCrop(0, 0, 10, 10), null)))).Attachment;
        await Assert.That(await host.Files.ExistsAsync(original.Sha256, CancellationToken.None)).IsTrue(); // kept for undo

        await host.Purge.PurgeAsync(null, CancellationToken.None);
        await Assert.That(await host.Files.ExistsAsync(original.Sha256, CancellationToken.None)).IsTrue(); // too young

        var old = Path.Combine(host.Storage.Options.AttachmentsDirectory, original.Sha256[..2], original.Sha256);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-3));
        File.SetLastWriteTimeUtc(Path.Combine(host.Storage.Options.AttachmentsDirectory, edited.Sha256[..2], edited.Sha256), DateTime.UtcNow.AddHours(-3));
        await host.Purge.PurgeAsync(null, CancellationToken.None);
        await Assert.That(await host.Files.ExistsAsync(original.Sha256, CancellationToken.None)).IsFalse();
        await Assert.That(await host.Files.ExistsAsync(edited.Sha256, CancellationToken.None)).IsTrue(); // still referenced
    }
}
