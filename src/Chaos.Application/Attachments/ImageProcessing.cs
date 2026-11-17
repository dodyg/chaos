namespace Chaos.Application.Attachments;

/// <summary>A crop rectangle in pixels of the image after flipping and rotating.</summary>
public sealed record ImageCrop(int X, int Y, int Width, int Height);

/// <summary>
/// What to do to an image, in this order: flip, rotate clockwise (0, 90, 180 or 270 degrees), crop
/// (coordinates of the rotated image), then shrink so neither side exceeds <see cref="MaxSide"/>.
/// </summary>
public sealed record ImageEdit(bool FlipHorizontal, bool FlipVertical, int RotateClockwise, ImageCrop? Crop, int? MaxSide)
{
    public bool IsNoOp => !FlipHorizontal && !FlipVertical && RotateClockwise == 0 && Crop is null && MaxSide is null;
}

public sealed record ProcessedImage(byte[] Bytes, string MediaType, int Width, int Height);

/// <summary>Image editing and thumbnails, kept behind an interface so the imaging library can change.</summary>
public interface IImageProcessor
{
    Task<ProcessedImage> EditAsync(Stream input, string mediaType, ImageEdit edit, CancellationToken ct);

    /// <summary>A JPEG no larger than <paramref name="maxSide"/> on either side, upright.</summary>
    Task<ProcessedImage> ThumbnailAsync(Stream input, int maxSide, CancellationToken ct);
}
