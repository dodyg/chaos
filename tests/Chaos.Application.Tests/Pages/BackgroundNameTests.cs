using Chaos.Application.Pages;

namespace Chaos.Application.Tests.Pages;

/// <summary>
/// PLAN2 P9: which `background:` values name an attached image. The front end parses the same
/// table in Pages.test.tsx (attachmentBackgroundName); change both together.
/// </summary>
public sealed class BackgroundNameTests
{
    [Test]
    [Arguments("attachments/plan.jpg", "plan.jpg")]
    [Arguments("Attachments/Plan Final.PNG", "Plan Final.PNG")]
    [Arguments("  attachments/a.png  ", "a.png")]
    [Arguments("attachments/photo (1).webp", "photo (1).webp")]
    public async Task AttachmentBackgroundName_ReturnsTheFileName(string value, string expected)
    {
        await Assert.That(PageFrontMatter.AttachmentBackgroundName(value)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("attachments/")]
    [Arguments("attachments/../x.png")]
    [Arguments("attachments/sub/x.png")]
    [Arguments("attachments/sub\\x.png")]
    [Arguments("attachments/a:b.png")]
    [Arguments("attachments/x.png.")]
    [Arguments("https://example.com/x.png")]
    [Arguments("//example.com/x.png")]
    [Arguments("C:/x.png")]
    [Arguments("sunset")]
    [Arguments("#fff")]
    [Arguments("")]
    public async Task AttachmentBackgroundName_RefusesAnythingElse(string value)
    {
        await Assert.That(PageFrontMatter.AttachmentBackgroundName(value)).IsNull();
    }

    [Test]
    public async Task AttachmentBackgroundName_NullIsNull_AndFrontMatterExposesIt()
    {
        await Assert.That(PageFrontMatter.AttachmentBackgroundName(null)).IsNull();
        var parsed = PageFrontMatter.Parse("---\nbackground: attachments/sky.jpg\n---\n\nText");
        await Assert.That(parsed.BackgroundAttachmentName).IsEqualTo("sky.jpg");
    }
}
