using Chaos.Domain.GitHub;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Chaos.Domain.Tests.GitHub;

public sealed class LabelNamesTests
{
    [Test]
    public async Task ToTagPath_SlashDelimitedLabel_ProducesNestedSegments()
    {
        var path = LabelNames.ToTagPath("area/ui");
        await Assert.That(path.Select(part => part.DisplayName).ToArray()).IsEquivalentTo(new[] { "area", "ui" });
        await Assert.That(LabelNames.FromTagPath(path.Select(part => part.DisplayName))).IsEqualTo("area/ui");
    }

    [Test]
    public async Task Matches_CaseDiffers_MatchesNormalizedName()
    {
        await Assert.That(LabelNames.Matches("Area/UI", "area/ui")).IsTrue();
    }
}
