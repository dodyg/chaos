using Chaos.Domain.Validation;
using Chaos.Domain.Views;
using TUnit.Assertions;
using TUnit.Assertions.Extensions;

namespace Chaos.Domain.Tests.Views;

public sealed class ViewDefinitionTests
{
    private const string Spec = """
        {"v":1,"match":"all","items":[
          {"field":"status","op":"notIn","value":["Done","Cancelled"]},
          {"match":"any","items":[
            {"field":"tag","op":"hasAnyOf","value":["t1"],"descendants":true},
            {"field":"priority","op":"atLeast","value":"High"}]},
          {"field":"due","op":"inRange","value":{"relative":"next","days":7}}]}
        """;

    [Test]
    public async Task Parse_SpecExample_ReadsGroupsAndConditions()
    {
        var definition = ViewDefinition.Parse(Spec);

        await Assert.That(definition.ConditionCount).IsEqualTo(4);
        await Assert.That(definition.Root.Items.Count).IsEqualTo(3);
        var group = (ViewGroup)definition.Root.Items[1];
        await Assert.That(group.Match).IsEqualTo(ViewMatch.Any);
        await Assert.That(((ViewCondition)group.Items[0]).Descendants).IsTrue();
    }

    [Test]
    public async Task ToJson_ThenParse_RoundTrips()
    {
        var first = ViewDefinition.Parse(Spec);
        var second = ViewDefinition.Parse(first.ToJson());

        await Assert.That(second.ToJson()).IsEqualTo(first.ToJson());
    }

    [Test]
    [Arguments("{\"v\":2,\"items\":[]}")]
    [Arguments("{\"items\":[]}")]
    [Arguments("not json")]
    [Arguments("[]")]
    [Arguments("")]
    public async Task Parse_BadEnvelope_IsRejected(string json)
    {
        await Assert.That(() => ViewDefinition.Parse(json)).Throws<DomainValidationException>();
    }

    [Test]
    [Arguments("{\"field\":\"nope\",\"op\":\"is\",\"value\":\"x\"}")]
    [Arguments("{\"field\":\"status\",\"op\":\"is\",\"value\":\"Done\"}")]
    [Arguments("{\"field\":\"status\",\"op\":\"in\",\"value\":[\"Finished\"]}")]
    [Arguments("{\"field\":\"status\",\"op\":\"in\",\"value\":[]}")]
    [Arguments("{\"field\":\"priority\",\"op\":\"atLeast\",\"value\":\"Huge\"}")]
    [Arguments("{\"field\":\"priority\",\"op\":\"atLeast\",\"value\":3}")]
    [Arguments("{\"field\":\"due\",\"op\":\"before\",\"value\":\"2026-13-01\"}")]
    [Arguments("{\"field\":\"due\",\"op\":\"inRange\",\"value\":{\"from\":\"2026-05-02\",\"to\":\"2026-05-01\"}}")]
    [Arguments("{\"field\":\"due\",\"op\":\"inRange\",\"value\":{\"relative\":\"next\",\"days\":0}}")]
    [Arguments("{\"field\":\"due\",\"op\":\"inRange\",\"value\":{\"relative\":\"someday\"}}")]
    [Arguments("{\"field\":\"planned\",\"op\":\"inRange\",\"value\":{\"relative\":\"overdue\"}}")]
    [Arguments("{\"field\":\"due\",\"op\":\"before\",\"value\":{\"relative\":\"overdue\"}}")]
    [Arguments("{\"field\":\"estimate\",\"op\":\"moreThan\",\"value\":-1}")]
    [Arguments("{\"field\":\"estimate\",\"op\":\"moreThan\",\"value\":\"ten\"}")]
    [Arguments("{\"field\":\"text\",\"op\":\"contains\",\"value\":\"  \"}")]
    [Arguments("{\"field\":\"recurring\",\"op\":\"is\",\"value\":\"yes\"}")]
    [Arguments("{\"field\":\"category\",\"op\":\"is\",\"value\":\"Hobby\"}")]
    [Arguments("{\"field\":\"container\",\"op\":\"is\",\"value\":[\"a\",\"b\"]}")]
    [Arguments("{\"field\":\"status\",\"op\":\"in\",\"value\":[\"Done\"],\"descendants\":true}")]
    [Arguments("{\"field\":\"githubState\",\"op\":\"is\",\"value\":\"Merged\"}")]
    [Arguments("{\"field\":\"githubState\",\"op\":\"in\",\"value\":[\"Open\"]}")]
    [Arguments("{\"field\":\"githubLabel\",\"op\":\"is\",\"value\":\"bug\"}")]
    [Arguments("{\"field\":\"githubPrReview\",\"op\":\"contains\",\"value\":\"x\"}")]
    [Arguments("{\"field\":\"githubPrReview\",\"op\":\"is\",\"value\":\"Merged\"}")]
    [Arguments("{\"field\":\"githubLinkedPr\",\"op\":\"contains\",\"value\":\"x\"}")]
    [Arguments("{\"field\":\"githubLinkedPr\",\"op\":\"is\",\"value\":\"yes\"}")]
    [Arguments("{\"field\":\"githubRepository\",\"op\":\"is\",\"value\":[\"a\",\"b\"]}")]
    [Arguments("{\"field\":\"tag\",\"op\":\"hasAnyOf\"}")]
    public async Task Parse_BadCondition_IsRejected(string condition)
    {
        var json = "{\"v\":1,\"match\":\"all\",\"items\":[" + condition + "]}";

        await Assert.That(() => ViewDefinition.Parse(json)).Throws<DomainValidationException>();
    }

    [Test]
    [Arguments("{\"field\":\"due\",\"op\":\"isEmpty\"}")]
    [Arguments("{\"field\":\"due\",\"op\":\"on\",\"value\":\"2026-05-01\"}")]
    [Arguments("{\"field\":\"due\",\"op\":\"inRange\",\"value\":{\"relative\":\"overdue\"}}")]
    [Arguments("{\"field\":\"created\",\"op\":\"after\",\"value\":{\"relative\":\"lastMonth\"}}")]
    [Arguments("{\"field\":\"completed\",\"op\":\"isNotEmpty\"}")]
    [Arguments("{\"field\":\"estimate\",\"op\":\"isEmpty\"}")]
    [Arguments("{\"field\":\"waiting\",\"op\":\"contains\",\"value\":\"Sam\"}")]
    [Arguments("{\"field\":\"hasPadLink\",\"op\":\"is\",\"value\":true}")]
    [Arguments("{\"field\":\"container\",\"op\":\"in\",\"value\":[\"a\",\"b\"]}")]
    [Arguments("{\"field\":\"source\",\"op\":\"is\",\"value\":\"GitHub\"}")]
    [Arguments("{\"field\":\"source\",\"op\":\"is\",\"value\":\"Agent\"}")]
    [Arguments("{\"field\":\"githubState\",\"op\":\"is\",\"value\":\"Open\"}")]
    [Arguments("{\"field\":\"githubRepository\",\"op\":\"in\",\"value\":[\"a\",\"b\"]}")]
    [Arguments("{\"field\":\"githubLabel\",\"op\":\"contains\",\"value\":\"bug\"}")]
    [Arguments("{\"field\":\"githubMilestone\",\"op\":\"isEmpty\"}")]
    [Arguments("{\"field\":\"githubAssignee\",\"op\":\"contains\",\"value\":\"octocat\"}")]
    [Arguments("{\"field\":\"githubAuthor\",\"op\":\"contains\",\"value\":\"octo\"}")]
    [Arguments("{\"field\":\"githubAuthor\",\"op\":\"isEmpty\"}")]
    [Arguments("{\"field\":\"githubLinkedPr\",\"op\":\"is\",\"value\":true}")]
    [Arguments("{\"field\":\"githubPrReview\",\"op\":\"is\",\"value\":\"Changes requested\"}")]
    [Arguments("{\"field\":\"githubIssueType\",\"op\":\"isEmpty\"}")]
    [Arguments("{\"field\":\"tag\",\"op\":\"hasNone\",\"value\":[\"x\"]}")]
    public async Task Parse_GoodCondition_IsAccepted(string condition)
    {
        var json = "{\"v\":1,\"match\":\"any\",\"items\":[" + condition + "]}";

        var definition = ViewDefinition.Parse(json);

        await Assert.That(definition.ConditionCount).IsEqualTo(1);
        await Assert.That(ViewDefinition.Parse(definition.ToJson()).ToJson()).IsEqualTo(definition.ToJson());
    }

    private static string Nest(int levels) => levels == 1
        ? "{\"match\":\"all\",\"items\":[{\"field\":\"recurring\",\"op\":\"is\",\"value\":true}]}"
        : "{\"match\":\"all\",\"items\":[" + Nest(levels - 1) + "]}";

    [Test]
    public async Task Parse_FourLevelsOfGroups_IsAccepted_FiveIsRejected()
    {
        await Assert.That(ViewDefinition.Parse("{\"v\":1," + Nest(4)[1..]).ConditionCount).IsEqualTo(1);
        await Assert.That(() => ViewDefinition.Parse("{\"v\":1," + Nest(5)[1..])).Throws<DomainValidationException>();
    }

    private static string Many(int count) =>
        "{\"v\":1,\"items\":[" + string.Join(",", Enumerable.Repeat("{\"field\":\"recurring\",\"op\":\"is\",\"value\":true}", count)) + "]}";

    [Test]
    public async Task Parse_FiftyConditions_IsAccepted_FiftyOneIsRejected()
    {
        await Assert.That(ViewDefinition.Parse(Many(50)).ConditionCount).IsEqualTo(50);
        await Assert.That(() => ViewDefinition.Parse(Many(51))).Throws<DomainValidationException>();
    }

    [Test]
    public async Task Parse_MatchOmitted_DefaultsToAll()
    {
        var definition = ViewDefinition.Parse("{\"v\":1,\"items\":[]}");

        await Assert.That(definition.Root.Match).IsEqualTo(ViewMatch.All);
        await Assert.That(definition.ConditionCount).IsEqualTo(0);
    }

    // 2026-11-18 is a Wednesday.
    private static readonly DateOnly Today = new(2026, 11, 18);

    [Test]
    [Arguments(ViewRelative.Today, 0, "2026-11-18", "2026-11-18")]
    [Arguments(ViewRelative.Tomorrow, 0, "2026-11-19", "2026-11-19")]
    [Arguments(ViewRelative.Yesterday, 0, "2026-11-17", "2026-11-17")]
    [Arguments(ViewRelative.ThisWeek, 0, "2026-11-16", "2026-11-22")]
    [Arguments(ViewRelative.NextWeek, 0, "2026-11-23", "2026-11-29")]
    [Arguments(ViewRelative.LastWeek, 0, "2026-11-09", "2026-11-15")]
    [Arguments(ViewRelative.ThisMonth, 0, "2026-11-01", "2026-11-30")]
    [Arguments(ViewRelative.NextMonth, 0, "2026-12-01", "2026-12-31")]
    [Arguments(ViewRelative.LastMonth, 0, "2026-10-01", "2026-10-31")]
    [Arguments(ViewRelative.NextDays, 7, "2026-11-18", "2026-11-24")]
    [Arguments(ViewRelative.LastDays, 3, "2026-11-16", "2026-11-18")]
    public async Task Resolve_MondayWeeks_ReturnsTheDocumentedRange(ViewRelative relative, int days, string from, string to)
    {
        var range = ViewDates.Resolve(new ViewDateValue(null, null, relative, days), Today, DayOfWeek.Monday);

        await Assert.That(range.From).IsEqualTo(DateOnly.Parse(from));
        await Assert.That(range.To).IsEqualTo(DateOnly.Parse(to));
    }

    [Test]
    public async Task Resolve_SundayWeeks_StartTheWeekOnSunday()
    {
        var range = ViewDates.Resolve(new ViewDateValue(null, null, ViewRelative.ThisWeek, 0), Today, DayOfWeek.Sunday);

        await Assert.That(range.From).IsEqualTo(new DateOnly(2026, 11, 15));
        await Assert.That(range.To).IsEqualTo(new DateOnly(2026, 11, 21));
    }

    [Test]
    public async Task Resolve_MonthEdges_HandleYearAndLeapBoundaries()
    {
        var december = ViewDates.Resolve(new ViewDateValue(null, null, ViewRelative.NextMonth, 0), new DateOnly(2026, 12, 31), DayOfWeek.Monday);
        var march = ViewDates.Resolve(new ViewDateValue(null, null, ViewRelative.LastMonth, 0), new DateOnly(2028, 3, 1), DayOfWeek.Monday);

        await Assert.That(december.From).IsEqualTo(new DateOnly(2027, 1, 1));
        await Assert.That(december.To).IsEqualTo(new DateOnly(2027, 1, 31));
        await Assert.That(march.To).IsEqualTo(new DateOnly(2028, 2, 29));
    }

    [Test]
    public async Task Resolve_Absolute_PassesThrough()
    {
        var range = ViewDates.Resolve(new ViewDateValue(new DateOnly(2026, 1, 2), null, null, 0), Today, DayOfWeek.Monday);

        await Assert.That(range.From).IsEqualTo(new DateOnly(2026, 1, 2));
        await Assert.That(range.To).IsNull();
    }
}
