using System.Text;
using System.Text.RegularExpressions;
using Chaos.Application.Abstractions;

namespace Chaos.Infrastructure.Sites;

/// <summary>
/// Summarizes a page without a model: the most representative sentences (word-frequency scoring, in
/// their original order), the most frequent content words as tags, and a verdict from length and
/// promotional wording. It is the fallback when no local model answers, so it never throws for a page
/// that has readable text. It cannot judge originality; the reason says it is a quick estimate.
/// </summary>
public sealed partial class ExtractiveSiteSummarizer : ISiteSummarizer
{
    public const int MaxSummaryChars = 700;
    private const int MinSentenceWords = 6;
    private const int MaxSentenceWords = 60;

    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "are", "but", "not", "you", "your", "all", "any", "can", "had", "her", "was", "one", "our", "out",
        "has", "have", "him", "his", "how", "its", "may", "new", "now", "old", "see", "two", "who", "did", "get", "let", "put",
        "say", "she", "too", "use", "with", "that", "this", "from", "they", "will", "would", "there", "their", "what", "about",
        "which", "when", "make", "like", "time", "just", "know", "take", "into", "year", "good", "some", "could", "them", "than",
        "then", "also", "been", "were", "more", "other", "only", "over", "such", "these", "those", "very", "much", "many", "most",
        "each", "does", "doing", "done", "being", "because", "while", "where", "after", "before", "should", "still", "here",
        "even", "back", "well", "way", "want", "need", "first", "last", "own", "same", "both", "between", "through", "using",
        "used", "every", "really", "something", "things", "thing", "another", "however", "though", "often", "never", "always",
    };

    private static readonly string[] PromoPhrases =
    [
        "subscribe", "sign up", "free trial", "buy now", "limited time", "click here", "newsletter", "special offer",
        "discount", "you won't believe", "add to cart", "act now", "best price", "coupon",
    ];

    public Task<SiteAnalysis> AnalyzeAsync(FetchedPage page, CancellationToken ct)
    {
        var text = page.Text ?? "";
        var words = Words(text);
        if (words.Count == 0)
        {
            throw new SiteReadException("The page has no readable text to summarize.");
        }

        var frequency = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var word in words.Where(IsContent))
        {
            frequency[word] = frequency.GetValueOrDefault(word) + 1;
        }

        var titleWords = Words(page.Title ?? "").Where(IsContent).ToHashSet(StringComparer.Ordinal);
        var summary = Summarize(text, frequency, titleWords);
        if (summary.Length == 0)
        {
            summary = Truncate(Regex.Replace(text, @"\s+", " ").Trim());
        }

        var (verdict, reason) = Judge(text, words);
        return Task.FromResult(new SiteAnalysis(summary, verdict, reason, Keywords(frequency, titleWords)));
    }

    private static string Summarize(string text, Dictionary<string, int> frequency, HashSet<string> titleWords)
    {
        var sentences = SentenceSplit().Split(text)
            .Select(s => Regex.Replace(s, @"\s+", " ").Trim())
            .Where(s => s.Length > 0)
            .ToList();

        var candidates = new List<(int Index, string Sentence, double Score)>();
        for (var index = 0; index < sentences.Count; index++)
        {
            var sentence = sentences[index];
            var tokens = Words(sentence);
            if (tokens.Count is < MinSentenceWords or > MaxSentenceWords)
            {
                continue;
            }

            var content = tokens.Where(IsContent).ToList();
            if (content.Count == 0)
            {
                continue;
            }

            var score = content.Sum(w => frequency.GetValueOrDefault(w)) / Math.Sqrt(tokens.Count);
            score += content.Count(titleWords.Contains) * 2;
            score *= 1 + 0.5 / (1 + index / 3.0); // The opening sentences usually state the point.
            candidates.Add((index, sentence, score));
        }

        var wanted = text.Length > 6000 ? 4 : 3;
        var chosen = candidates.OrderByDescending(c => c.Score).Take(wanted).OrderBy(c => c.Index);
        var result = new StringBuilder();
        foreach (var (_, sentence, _) in chosen)
        {
            if (result.Length > 0)
            {
                result.Append(' ');
            }

            result.Append(sentence);
        }

        return Truncate(result.ToString());
    }

    private static (SiteVerdict, string) Judge(string text, List<string> words)
    {
        var lower = text.ToLowerInvariant();
        var promo = PromoPhrases.Count(lower.Contains);
        if (promo >= 3)
        {
            return (SiteVerdict.Skip, "Reads like promotion or a sign-up page (quick estimate, no model).");
        }

        if (words.Count < 250)
        {
            return (SiteVerdict.Skip, $"Only about {words.Count} words (quick estimate, no model).");
        }

        var diversity = (double)words.Distinct(StringComparer.Ordinal).Count() / words.Count;
        if (words.Count >= 600 && diversity >= 0.3 && promo == 0)
        {
            return (SiteVerdict.Read, $"Long and varied, about {words.Count} words (quick estimate, no model).");
        }

        return (SiteVerdict.Skim, $"Medium length, about {words.Count} words (quick estimate, no model).");
    }

    private static List<string> Keywords(Dictionary<string, int> frequency, HashSet<string> titleWords) =>
        [.. frequency
            .Where(pair => pair.Key.Length >= 4 && pair.Key.All(char.IsLetter) && pair.Value >= 2)
            .OrderByDescending(pair => pair.Value + (titleWords.Contains(pair.Key) ? 5 : 0))
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(3)
            .Select(pair => pair.Key)];

    private static string Truncate(string text)
    {
        if (text.Length <= MaxSummaryChars)
        {
            return text;
        }

        var cut = text[..MaxSummaryChars];
        var space = cut.LastIndexOf(' ');
        return (space > MaxSummaryChars / 2 ? cut[..space] : cut).TrimEnd(',', ';', ':') + "…";
    }

    private static bool IsContent(string word) => word.Length >= 3 && !Stopwords.Contains(word);

    private static List<string> Words(string text) =>
        [.. WordPattern().Matches(text).Select(m => m.Value.ToLowerInvariant())];

    [GeneratedRegex(@"(?<=[.!?])\s+|\n+")]
    private static partial Regex SentenceSplit();

    [GeneratedRegex(@"[\p{L}\p{N}][\p{L}\p{N}'’-]*")]
    private static partial Regex WordPattern();
}
