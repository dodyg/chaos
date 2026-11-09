using Chaos.Domain.Validation;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Chaos.Domain.Tests.Validation;

public sealed class TaskTitleTests
{
    [Test]
    public async Task Normalize_ExteriorWhitespace_IsTrimmed()
    {
        var title = TaskTitle.Normalize("  Buy milk\t ");

        await Assert.That(title).IsEqualTo("Buy milk");
    }

    [Test]
    public async Task Normalize_BlankInput_ThrowsWithGuidance()
    {
        await Assert.That(() => TaskTitle.Normalize("   "))
            .Throws<DomainValidationException>();
    }

    [Test]
    public async Task Normalize_Exactly500Scalars_IsAccepted()
    {
        var title = TaskTitle.Normalize(new string('a', TaskTitle.MaxUnicodeScalarValues));

        await Assert.That(title.Length).IsEqualTo(500);
    }

    [Test]
    public async Task Normalize_501Scalars_ThrowsStatingLimit()
    {
        var tooLong = new string('a', 501);

        await Assert.That(() => TaskTitle.Normalize(tooLong))
            .Throws<DomainValidationException>().And.HasMessageContaining("500");
    }

    [Test]
    public async Task Normalize_300Emoji_CountsScalarsNotUtf16Units()
    {
        // 300 emoji are 600 UTF-16 code units but only 300 scalar values: accepted.
        var emojiTitle = string.Concat(Enumerable.Repeat("\uD83D\uDE00", 300));

        var title = TaskTitle.Normalize(emojiTitle);

        await Assert.That(title.Length).IsEqualTo(600);
    }

    [Test]
    public async Task Normalize_251Emoji_ExceedsScalarLimit()
    {
        var emojiTitle = string.Concat(Enumerable.Repeat("\uD83D\uDE00", 501)); // 501 scalar values, 1002 UTF-16 units

        await Assert.That(() => TaskTitle.Normalize(emojiTitle))
            .Throws<DomainValidationException>();
    }
}
