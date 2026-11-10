using Chaos.Infrastructure.Search;

namespace Chaos.Infrastructure.Tests.Search;

public sealed class SearchTextTests
{
    [Test]
    [Arguments("السبّاك", "السباك")]          // shadda removed
    [Arguments("مـــكتب", "مكتب")]            // tatweel removed
    [Arguments("غرفة ٣٤", "غرفة 34")]         // Arabic-Indic digits
    [Arguments("اتاق ۱۲", "اتاق 12")]          // Persian digits
    [Arguments("Café crème", "Cafe creme")]   // Latin accents
    [Arguments("ｗｏｒｋ", "work")]             // compatibility forms
    public async Task Normalize_FoldsMarksDigitsAndCompatibilityForms(string input, string expected)
    {
        await Assert.That(SearchText.Normalize(input)).IsEqualTo(expected);
    }

    [Test]
    public async Task ToMatchExpression_QuotesOperatorsAndPunctuation_BecomeQuotedPrefixTerms()
    {
        var expression = SearchText.ToMatchExpression("he said \"hi\" (x) NEAR -y* e-mail");

        await Assert.That(expression).IsEqualTo("\"he\"* AND \"said\"* AND \"hi\"* AND \"x\"* AND \"NEAR\"* AND \"y\"* AND \"e\"* AND \"mail\"*");
    }

    [Test]
    [Arguments("")]
    [Arguments("   ")]
    [Arguments("\"(*)-^:")]
    public async Task ToMatchExpression_NoWordCharacters_ReturnsNull(string input)
    {
        await Assert.That(SearchText.ToMatchExpression(input)).IsNull();
    }

    [Test]
    public async Task Terms_KeepsEmojiOutAndSurrogateLettersWhole()
    {
        var terms = SearchText.Terms(SearchText.Normalize("plan 🚀 launch 𝐀𝐁"));

        await Assert.That(string.Join("|", terms)).IsEqualTo("plan|launch|AB");
    }
}
