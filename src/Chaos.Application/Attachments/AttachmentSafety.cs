namespace Chaos.Application.Attachments;

public enum AttachmentPreview
{
    None = 0,
    Image = 1,
    Pdf = 2,
    Video = 3,
    Audio = 4,
    Text = 5,
}

/// <summary>
/// What may be opened in another app (PLAN2 §9.1). Files that can run code never open from
/// Chaos: they offer "Show in folder" and "Save a copy" only. The list lives here, once.
/// </summary>
public static class AttachmentSafety
{
    public static readonly IReadOnlyList<string> BlockedExtensions =
    [
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msi", ".msp",
        ".scr", ".hta", ".cpl", ".jar", ".lnk", ".url", ".reg", ".dll", ".appx", ".msix",
    ];

    private static readonly HashSet<string> Blocked = new(BlockedExtensions, StringComparer.OrdinalIgnoreCase);

    /// <summary>True when the file name's extension is one that runs code (trailing dots and spaces, which Windows ignores, do not hide it).</summary>
    public static bool IsBlocked(string fileName)
    {
        var name = fileName.Trim().TrimEnd('.', ' ');
        var dot = name.LastIndexOf('.');
        return dot >= 0 && Blocked.Contains(name[dot..]);
    }

    /// <summary>Whether a media type is shown in place or previewed; everything else is a file card.</summary>
    public static AttachmentPreview PreviewOf(string mediaType) => mediaType switch
    {
        "image/png" or "image/jpeg" or "image/gif" or "image/webp" => AttachmentPreview.Image,
        "application/pdf" => AttachmentPreview.Pdf,
        "video/mp4" or "video/webm" or "video/quicktime" => AttachmentPreview.Video,
        "audio/mpeg" or "audio/webm" or "audio/mp4" or "audio/ogg" or "audio/wav" => AttachmentPreview.Audio,
        "text/plain" => AttachmentPreview.Text,
        _ => AttachmentPreview.None,
    };

    /// <summary>
    /// Like <see cref="PreviewOf(string)"/>, but a recorded voice note stored before audio-only WebM was recognised
    /// (<c>voice-note.webm</c> as <c>video/webm</c>) is still shown with an audio player.
    /// </summary>
    public static AttachmentPreview PreviewOf(string mediaType, string fileName) =>
        mediaType == "video/webm" && fileName.StartsWith("voice-note.", StringComparison.OrdinalIgnoreCase)
            ? AttachmentPreview.Audio
            : PreviewOf(mediaType);

    /// <summary>A display name that is safe as a file name on Windows: no path parts, no reserved characters or names.</summary>
    public static string SanitizeFileName(string? raw, string fallback = "file")
    {
        var last = (raw ?? "").Replace('\\', '/').Split('/').LastOrDefault() ?? "";
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. last.Select(ch => invalid.Contains(ch) || char.IsControl(ch) ? '_' : ch)]).Trim().TrimEnd('.', ' ');
        if (cleaned.Length > 180)
        {
            var ext = Path.GetExtension(cleaned);
            ext = ext.Length > 20 ? "" : ext;
            cleaned = cleaned[..(180 - ext.Length)] + ext;
        }

        if (cleaned.Length == 0)
        {
            return fallback;
        }

        return IsReservedDeviceName(Path.GetFileNameWithoutExtension(cleaned)) ? "_" + cleaned : cleaned;
    }

    private static bool IsReservedDeviceName(string stem)
    {
        var upper = stem.ToUpperInvariant();
        return upper is "CON" or "PRN" or "AUX" or "NUL"
            || (upper.Length == 4 && (upper.StartsWith("COM", StringComparison.Ordinal) || upper.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(upper[3]));
    }
}
