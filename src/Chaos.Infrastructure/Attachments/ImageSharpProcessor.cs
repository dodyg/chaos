using Chaos.Application.Abstractions;
using Chaos.Application.Attachments;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace Chaos.Infrastructure.Attachments;

/// <summary>
/// ImageSharp behind <see cref="IImageProcessor"/> (Six Labors Split License; see
/// docs/dependency-versions.md): PNG, JPEG and WebP keep their format, a single-frame GIF comes out as PNG.
/// </summary>
public sealed class ImageSharpProcessor : IImageProcessor
{
    public async Task<ProcessedImage> EditAsync(Stream input, string mediaType, ImageEdit edit, CancellationToken ct)
    {
        try
        {
            using var image = await Image.LoadAsync(input, ct);
            if (image.Frames.Count > 1)
            {
                throw AppException.Validation("image", "Animated images cannot be edited, because editing would keep only the first frame.");
            }

            image.Mutate(context =>
            {
                if (edit.FlipHorizontal)
                {
                    context.Flip(FlipMode.Horizontal);
                }

                if (edit.FlipVertical)
                {
                    context.Flip(FlipMode.Vertical);
                }

                if (edit.RotateClockwise != 0)
                {
                    context.Rotate(edit.RotateClockwise);
                }
            });

            if (edit.Crop is { } crop)
            {
                if (crop.Width < 1 || crop.Height < 1 || crop.X < 0 || crop.Y < 0 || crop.X + crop.Width > image.Width || crop.Y + crop.Height > image.Height)
                {
                    throw AppException.Validation("crop", "The crop is outside the image.");
                }

                image.Mutate(context => context.Crop(new Rectangle(crop.X, crop.Y, crop.Width, crop.Height)));
            }

            if (edit.MaxSide is { } side && Math.Max(image.Width, image.Height) > side)
            {
                image.Mutate(context => context.Resize(new ResizeOptions { Size = new Size(side, side), Mode = ResizeMode.Max }));
            }

            return await WriteAsync(image, mediaType, ct);
        }
        catch (Exception error) when (error is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            throw AppException.Validation("image", "This image cannot be edited.");
        }
    }

    public async Task<ProcessedImage> ThumbnailAsync(Stream input, int maxSide, CancellationToken ct)
    {
        try
        {
            using var image = await Image.LoadAsync(input, ct);
            image.Mutate(context => context.AutoOrient().Resize(new ResizeOptions { Size = new Size(maxSide, maxSide), Mode = ResizeMode.Max }));
            return await WriteAsync(image, "image/jpeg", ct);
        }
        catch (Exception error) when (error is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            throw AppException.Validation("image", "This image cannot be previewed.");
        }
    }

    private static async Task<ProcessedImage> WriteAsync(Image image, string mediaType, CancellationToken ct)
    {
        using var output = new MemoryStream();
        IImageEncoder encoder;
        string type;
        switch (mediaType)
        {
            case "image/jpeg":
                encoder = new JpegEncoder { Quality = 90 };
                type = "image/jpeg";
                break;
            case "image/webp":
                encoder = new WebpEncoder();
                type = "image/webp";
                break;
            default:
                encoder = new PngEncoder();
                type = "image/png";
                break;
        }

        await image.SaveAsync(output, encoder, ct);
        return new ProcessedImage(output.ToArray(), type, image.Width, image.Height);
    }
}
