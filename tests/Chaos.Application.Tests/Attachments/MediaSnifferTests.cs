using System.Text;
using Chaos.Application.Attachments;

namespace Chaos.Application.Tests.Attachments;

/// <summary>PLAN2 P9: media types come from the bytes, never from the extension alone; code-running files never open.</summary>
public sealed class MediaSnifferTests
{
    private static byte[] Png(int width, int height)
    {
        var bytes = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        bytes[11] = 13;
        "IHDR"u8.CopyTo(bytes.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), (uint)height);
        return bytes;
    }

    private static byte[] Ftyp(string brand)
    {
        var bytes = new byte[32];
        "ftyp"u8.CopyTo(bytes.AsSpan(4));
        Encoding.ASCII.GetBytes(brand).CopyTo(bytes, 8);
        return bytes;
    }

    [Test]
    public async Task Sniff_RecognisesTheListedFormatsByTheirBytes()
    {
        await Assert.That(MediaSniffer.Sniff(Png(2, 3), "x.bin")).IsEqualTo("image/png");
        await Assert.That(MediaSniffer.Sniff(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 0 }, "x")).IsEqualTo("image/jpeg");
        await Assert.That(MediaSniffer.Sniff("GIF89a\x01\0\x01\0"u8, "x")).IsEqualTo("image/gif");
        await Assert.That(MediaSniffer.Sniff("%PDF-1.7\n"u8, "x")).IsEqualTo("application/pdf");
        await Assert.That(MediaSniffer.Sniff(Ftyp("isom"), "x")).IsEqualTo("video/mp4");
        await Assert.That(MediaSniffer.Sniff(Ftyp("qt  "), "x")).IsEqualTo("video/quicktime");
        await Assert.That(MediaSniffer.Sniff(Ftyp("M4A "), "x")).IsEqualTo("audio/mp4");
        await Assert.That(MediaSniffer.Sniff(Ftyp("heic"), "x")).IsEqualTo("image/heic");
        await Assert.That(MediaSniffer.Sniff(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0, 0 }, "x")).IsEqualTo("video/webm");
        await Assert.That(MediaSniffer.Sniff("OggS\0\0"u8, "x")).IsEqualTo("audio/ogg");
        await Assert.That(MediaSniffer.Sniff("ID3\x03\0\0"u8, "x")).IsEqualTo("audio/mpeg");
        await Assert.That(MediaSniffer.Sniff("RIFF\x24\0\0\0WAVEfmt "u8, "x")).IsEqualTo("audio/wav");
        await Assert.That(MediaSniffer.Sniff("RIFF\x24\0\0\0WEBPVP8 "u8, "x")).IsEqualTo("image/webp");
    }

    [Test]
    public async Task Sniff_IgnoresALyingExtension()
    {
        await Assert.That(MediaSniffer.Sniff("%PDF-1.4"u8, "holiday.png")).IsEqualTo("application/pdf");
        await Assert.That(MediaSniffer.Sniff("MZ\x90\0\x03\0\0\0\x04\0\0\0"u8, "photo.jpg")).IsEqualTo(MediaSniffer.Octet);
    }

    [Test]
    public async Task Sniff_ZipIsOfficeOnlyWhenTheNameSaysSo_AndTextIsTextButSvgIsNeverAnImage()
    {
        var zip = new byte[] { (byte)'P', (byte)'K', 3, 4, 0, 0 };
        await Assert.That(MediaSniffer.Sniff(zip, "plan.docx")).Contains("wordprocessingml");
        await Assert.That(MediaSniffer.Sniff(zip, "plan.xlsx")).Contains("spreadsheetml");
        await Assert.That(MediaSniffer.Sniff(zip, "archive.zip")).IsEqualTo(MediaSniffer.Octet);
        await Assert.That(MediaSniffer.Sniff("Hello, wörld\nsecond line\t"u8, "notes.txt")).IsEqualTo("text/plain");
        await Assert.That(MediaSniffer.Sniff("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>"u8, "logo.svg")).IsEqualTo("text/plain");
        await Assert.That(MediaSniffer.Sniff(new byte[] { 1, 2, 0, 4, 5 }, "data.bin")).IsEqualTo(MediaSniffer.Octet);
        await Assert.That(MediaSniffer.Sniff([], "empty")).IsEqualTo(MediaSniffer.Octet);
    }

    [Test]
    public async Task TryReadSize_ReadsPngGifAndJpegHeaders()
    {
        await Assert.That(MediaSniffer.TryReadSize("image/png", Png(640, 480))).IsEqualTo(new ImageSize(640, 480));
        await Assert.That(MediaSniffer.TryReadSize("image/gif", new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x20, 0x01, 0xF0, 0x00 })).IsEqualTo(new ImageSize(288, 240));
        var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 4, 0, 0, 0xFF, 0xC0, 0, 11, 8, 0x01, 0xE0, 0x02, 0x80, 1, 0, 0 };
        await Assert.That(MediaSniffer.TryReadSize("image/jpeg", jpeg)).IsEqualTo(new ImageSize(640, 480));
        await Assert.That(MediaSniffer.TryReadSize("image/png", [1, 2, 3])).IsNull();
        await Assert.That(MediaSniffer.TryReadSize("application/pdf", Png(1, 1))).IsNull();
    }

    [Test]
    [Arguments("tool.exe")]
    [Arguments("TOOL.EXE")]
    [Arguments("run.ps1")]
    [Arguments("setup.msi")]
    [Arguments("report.pdf.exe")]
    [Arguments("trailing.bat.")]
    [Arguments("spaced.cmd ")]
    [Arguments("shortcut.lnk")]
    [Arguments("page.url")]
    [Arguments("app.msix")]
    [Arguments("script.js")]
    public async Task IsBlocked_RefusesEveryScriptLikeOrExecutableName(string name)
    {
        await Assert.That(AttachmentSafety.IsBlocked(name)).IsTrue();
    }

    [Test]
    [Arguments("report.pdf")]
    [Arguments("photo.png")]
    [Arguments("notes.txt")]
    [Arguments("exe")]
    [Arguments("data.json")]
    public async Task IsBlocked_AllowsOrdinaryFiles(string name)
    {
        await Assert.That(AttachmentSafety.IsBlocked(name)).IsFalse();
    }

    [Test]
    public async Task BlockedExtensions_AreExactlyThePlannedList()
    {
        string[] planned = [".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msi", ".msp", ".scr", ".hta", ".cpl", ".jar", ".lnk", ".url", ".reg", ".dll", ".appx", ".msix"];
        await Assert.That(AttachmentSafety.BlockedExtensions.Order().ToArray()).IsEquivalentTo(planned.Order().ToArray());
    }

    [Test]
    public async Task SanitizeFileName_RemovesPathsReservedCharactersAndDeviceNames()
    {
        await Assert.That(AttachmentSafety.SanitizeFileName(@"C:\Users\me\secret.txt")).IsEqualTo("secret.txt");
        await Assert.That(AttachmentSafety.SanitizeFileName("../../etc/passwd")).IsEqualTo("passwd");
        await Assert.That(AttachmentSafety.SanitizeFileName("a<b>c:d?.png")).IsEqualTo("a_b_c_d_.png");
        await Assert.That(AttachmentSafety.SanitizeFileName("CON.txt")).IsEqualTo("_CON.txt");
        await Assert.That(AttachmentSafety.SanitizeFileName("  ")).IsEqualTo("file");
        await Assert.That(AttachmentSafety.SanitizeFileName(null)).IsEqualTo("file");
        await Assert.That(AttachmentSafety.SanitizeFileName(new string('a', 400) + ".pdf").Length).IsLessThanOrEqualTo(180);
    }

    [Test]
    public async Task AttachmentHash_AcceptsOnlyLowercaseSha256Hex()
    {
        await Assert.That(AttachmentHash.IsValid(new string('a', 64))).IsTrue();
        await Assert.That(AttachmentHash.IsValid(new string('A', 64))).IsFalse();
        await Assert.That(AttachmentHash.IsValid("../" + new string('a', 61))).IsFalse();
        await Assert.That(AttachmentHash.IsValid(new string('a', 63))).IsFalse();
        await Assert.That(AttachmentHash.IsValid(null)).IsFalse();
    }
}
