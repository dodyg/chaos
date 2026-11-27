using System.Buffers.Binary;
using System.Text;

namespace Chaos.Application.Attachments;

/// <summary>Width and height read from an image's header.</summary>
public readonly record struct ImageSize(int Width, int Height);

/// <summary>
/// Decides a file's media type from its leading bytes, never from the extension alone (PLAN2
/// §9.1), and reads image dimensions from headers, so no imaging library is needed. SVG is
/// deliberately plain text: it can carry script, so it is never shown as an image.
/// </summary>
public static class MediaSniffer
{
    public const string Octet = "application/octet-stream";

    /// <summary>How many leading bytes <see cref="Sniff"/> and <see cref="TryReadSize"/> want to see.</summary>
    public const int HeadBytes = 256 * 1024;

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static bool HasCodec(ReadOnlySpan<byte> head, ReadOnlySpan<byte> prefix)
    {
        // Codec ids are upper-case ASCII, so the prefix also has to be followed by a letter to rule out binary noise.
        for (var at = head.IndexOf(prefix); at >= 0;)
        {
            var next = at + prefix.Length;
            if (next < head.Length && head[next] is >= (byte)'A' and <= (byte)'Z')
            {
                return true;
            }

            var rest = head[next..].IndexOf(prefix);
            at = rest < 0 ? -1 : next + rest;
        }

        return false;
    }

    public static string Sniff(ReadOnlySpan<byte> head, string fileName)
    {
        if (head.Length >= 8 && head[..8].SequenceEqual(PngSignature))
        {
            return "image/png";
        }

        if (head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (head.Length >= 6 && (head[..6].SequenceEqual("GIF87a"u8) || head[..6].SequenceEqual("GIF89a"u8)))
        {
            return "image/gif";
        }

        if (head.Length >= 12 && head[..4].SequenceEqual("RIFF"u8))
        {
            if (head.Slice(8, 4).SequenceEqual("WEBP"u8))
            {
                return "image/webp";
            }

            if (head.Slice(8, 4).SequenceEqual("WAVE"u8))
            {
                return "audio/wav";
            }
        }

        if (head.Length >= 5 && head[..5].SequenceEqual("%PDF-"u8))
        {
            return "application/pdf";
        }

        if (head.Length >= 12 && head.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            var brand = Encoding.ASCII.GetString(head.Slice(8, 4));
            return brand switch
            {
                "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis" or "mif1" or "msf1" or "heif" => "image/heic",
                "qt  " => "video/quicktime",
                "M4A " or "M4B " or "M4P " => "audio/mp4",
                _ => "video/mp4",
            };
        }

        if (head.Length >= 4 && head[0] == 0x1A && head[1] == 0x45 && head[2] == 0xDF && head[3] == 0xA3)
        {
            // A voice note recorded by the WebView is WebM too, but with only an audio track; its codec id
            // ("A_OPUS") is in the track header, a video one ("V_VP8") is not.
            return HasCodec(head, "A_"u8) && !HasCodec(head, "V_"u8) ? "audio/webm" : "video/webm";
        }

        if (head.Length >= 4 && head[..4].SequenceEqual("OggS"u8))
        {
            return "audio/ogg";
        }

        if (head.Length >= 3 && head[..3].SequenceEqual("ID3"u8))
        {
            return "audio/mpeg";
        }

        if (head.Length >= 2 && head[0] == 0xFF && (head[1] & 0xE0) == 0xE0 && (head[1] & 0x06) != 0)
        {
            return "audio/mpeg";
        }

        if (head.Length >= 4 && head[0] == 'P' && head[1] == 'K' && head[2] == 3 && head[3] == 4)
        {
            return Path.GetExtension(fileName).ToLowerInvariant() switch
            {
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                _ => Octet,
            };
        }

        return LooksLikeText(head) ? "text/plain" : Octet;
    }

    /// <summary>Dimensions of a PNG, JPEG, GIF or WebP from its header, or null when they cannot be read.</summary>
    public static ImageSize? TryReadSize(string mediaType, ReadOnlySpan<byte> head)
    {
        var size = mediaType switch
        {
            "image/png" => Png(head),
            "image/gif" => Gif(head),
            "image/jpeg" => Jpeg(head),
            "image/webp" => WebP(head),
            _ => null,
        };
        return size is { Width: > 0, Height: > 0 } ? size : null;
    }

    private static bool LooksLikeText(ReadOnlySpan<byte> head)
    {
        if (head.Length == 0)
        {
            return false;
        }

        var sample = head[..Math.Min(head.Length, 8192)];
        foreach (var value in sample)
        {
            if (value == 0 || (value < 0x20 && value is not (byte)'\t' and not (byte)'\n' and not (byte)'\r' and not 0x0C))
            {
                return false;
            }
        }

        // A multi-byte character cut by the sample end is fine; anything else invalid is not text.
        var text = new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(sample);
        var bad = text.Count(ch => ch == '�');
        return bad <= (sample.Length == head.Length ? 0 : 1);
    }

    private static ImageSize? Png(ReadOnlySpan<byte> head) =>
        head.Length >= 24 && head.Slice(12, 4).SequenceEqual("IHDR"u8)
            ? new ImageSize((int)BinaryPrimitives.ReadUInt32BigEndian(head[16..]), (int)BinaryPrimitives.ReadUInt32BigEndian(head[20..]))
            : null;

    private static ImageSize? Gif(ReadOnlySpan<byte> head) =>
        head.Length >= 10 ? new ImageSize(BinaryPrimitives.ReadUInt16LittleEndian(head[6..]), BinaryPrimitives.ReadUInt16LittleEndian(head[8..])) : null;

    private static ImageSize? Jpeg(ReadOnlySpan<byte> head)
    {
        var index = 2;
        while (index + 9 < head.Length)
        {
            if (head[index] != 0xFF)
            {
                index++;
                continue;
            }

            var marker = head[index + 1];
            if (marker == 0xFF)
            {
                index++;
                continue;
            }

            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7)
            {
                index += 2;
                continue;
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(head[(index + 2)..]);
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                return new ImageSize(BinaryPrimitives.ReadUInt16BigEndian(head[(index + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(head[(index + 5)..]));
            }

            if (length < 2)
            {
                return null;
            }

            index += 2 + length;
        }

        return null;
    }

    private static ImageSize? WebP(ReadOnlySpan<byte> head)
    {
        if (head.Length < 30)
        {
            return null;
        }

        var kind = head.Slice(12, 4);
        if (kind.SequenceEqual("VP8 "u8))
        {
            return new ImageSize(BinaryPrimitives.ReadUInt16LittleEndian(head[26..]) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(head[28..]) & 0x3FFF);
        }

        if (kind.SequenceEqual("VP8L"u8) && head[20] == 0x2F)
        {
            var bits = BinaryPrimitives.ReadUInt32LittleEndian(head[21..]);
            return new ImageSize((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
        }

        if (kind.SequenceEqual("VP8X"u8))
        {
            return new ImageSize((head[24] | (head[25] << 8) | (head[26] << 16)) + 1, (head[27] | (head[28] << 8) | (head[29] << 16)) + 1);
        }

        return null;
    }
}
