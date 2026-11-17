using Chaos.Application.Abstractions;
using Chaos.Application.Attachments;
using SkiaSharp;

namespace Chaos.Infrastructure.Attachments;

/// <summary>
/// SkiaSharp (MIT) behind <see cref="IImageProcessor"/>: PNG, JPEG and WebP keep their format, a
/// single-frame GIF comes out as PNG (Skia decodes GIF but does not encode it).
/// </summary>
public sealed class SkiaImageProcessor : IImageProcessor
{
    public Task<ProcessedImage> EditAsync(Stream input, string mediaType, ImageEdit edit, CancellationToken ct)
    {
        using var source = Decode(input, requireSingleFrame: true);
        var current = source;
        SKBitmap? owned = null;
        try
        {
            if (edit.FlipHorizontal || edit.FlipVertical || edit.RotateClockwise != 0)
            {
                owned = Transform(current, edit.FlipHorizontal, edit.FlipVertical, edit.RotateClockwise);
                current = owned;
            }

            if (edit.Crop is { } crop)
            {
                if (crop.Width < 1 || crop.Height < 1 || crop.X < 0 || crop.Y < 0 || crop.X + crop.Width > current.Width || crop.Y + crop.Height > current.Height)
                {
                    throw AppException.Validation("crop", "The crop is outside the image.");
                }

                var cropped = new SKBitmap(crop.Width, crop.Height, current.ColorType, current.AlphaType);
                if (!current.ExtractSubset(cropped, new SKRectI(crop.X, crop.Y, crop.X + crop.Width, crop.Y + crop.Height)))
                {
                    cropped.Dispose();
                    throw AppException.Validation("crop", "The crop is outside the image.");
                }

                owned?.Dispose();
                owned = cropped;
                current = cropped;
            }

            if (edit.MaxSide is { } side && Math.Max(current.Width, current.Height) > side)
            {
                var resized = Resize(current, side);
                owned?.Dispose();
                owned = resized;
                current = resized;
            }

            return Task.FromResult(Encode(current, mediaType));
        }
        finally
        {
            owned?.Dispose();
        }
    }

    public Task<ProcessedImage> ThumbnailAsync(Stream input, int maxSide, CancellationToken ct)
    {
        using var source = Decode(input, requireSingleFrame: false);
        using var small = Math.Max(source.Width, source.Height) > maxSide ? Resize(source, maxSide) : source.Copy();
        return Task.FromResult(Encode(small, "image/jpeg"));
    }

    /// <summary>Decodes the first frame, upright (the EXIF orientation is applied), and refuses animations when asked.</summary>
    private static SKBitmap Decode(Stream input, bool requireSingleFrame)
    {
        using var data = SKData.Create(input);
        using var codec = SKCodec.Create(data) ?? throw AppException.Validation("image", "This image cannot be edited.");
        if (requireSingleFrame && codec.FrameCount > 1)
        {
            throw AppException.Validation("image", "Animated images cannot be edited, because editing would keep only the first frame.");
        }

        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        var bitmap = new SKBitmap(info);
        if (codec.GetPixels(info, bitmap.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bitmap.Dispose();
            throw AppException.Validation("image", "This image cannot be edited.");
        }

        var (flipH, flipV, rotation) = codec.EncodedOrigin switch
        {
            SKEncodedOrigin.TopRight => (true, false, 0),
            SKEncodedOrigin.BottomRight => (false, false, 180),
            SKEncodedOrigin.BottomLeft => (false, true, 0),
            SKEncodedOrigin.LeftTop => (true, false, 270),
            SKEncodedOrigin.RightTop => (false, false, 90),
            SKEncodedOrigin.RightBottom => (true, false, 90),
            SKEncodedOrigin.LeftBottom => (false, false, 270),
            _ => (false, false, 0),
        };
        if (!flipH && !flipV && rotation == 0)
        {
            return bitmap;
        }

        using (bitmap)
        {
            return Transform(bitmap, flipH, flipV, rotation);
        }
    }

    /// <summary>Flips, then rotates clockwise by a multiple of 90 degrees, into a new bitmap.</summary>
    private static SKBitmap Transform(SKBitmap source, bool flipH, bool flipV, int rotation)
    {
        var swap = rotation is 90 or 270;
        var result = new SKBitmap(swap ? source.Height : source.Width, swap ? source.Width : source.Height, source.ColorType, source.AlphaType);
        using var canvas = new SKCanvas(result);
        canvas.Clear(SKColors.Transparent);
        canvas.Translate(result.Width / 2f, result.Height / 2f);
        canvas.RotateDegrees(rotation);
        canvas.Scale(flipH ? -1 : 1, flipV ? -1 : 1);
        canvas.Translate(-source.Width / 2f, -source.Height / 2f);
        canvas.DrawBitmap(source, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
        return result;
    }

    private static SKBitmap Resize(SKBitmap source, int maxSide)
    {
        var k = (double)maxSide / Math.Max(source.Width, source.Height);
        var info = new SKImageInfo(Math.Max(1, (int)Math.Round(source.Width * k)), Math.Max(1, (int)Math.Round(source.Height * k)), source.ColorType, source.AlphaType);
        var target = new SKBitmap(info);
        using var pixels = source.PeekPixels();
        if (!pixels.ScalePixels(target.PeekPixels(), new SKSamplingOptions(SKCubicResampler.Mitchell)))
        {
            target.Dispose();
            throw AppException.Validation("image", "This image cannot be resized.");
        }

        return target;
    }

    private static ProcessedImage Encode(SKBitmap bitmap, string mediaType)
    {
        var (format, type, quality) = mediaType switch
        {
            "image/jpeg" => (SKEncodedImageFormat.Jpeg, "image/jpeg", 90),
            "image/webp" => (SKEncodedImageFormat.Webp, "image/webp", 90),
            _ => (SKEncodedImageFormat.Png, "image/png", 100), // PNG and GIF both come out as PNG
        };
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality) ?? throw AppException.Validation("image", "This image cannot be saved.");
        return new ProcessedImage(data.ToArray(), type, bitmap.Width, bitmap.Height);
    }
}
