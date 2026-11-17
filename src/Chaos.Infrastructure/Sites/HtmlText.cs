using System.Net;
using System.Text.RegularExpressions;

namespace Chaos.Infrastructure.Sites;

/// <summary>Reduces an HTML page to its title and readable text. Not a parser: good enough to feed a small model.</summary>
public static partial class HtmlText
{
    public static (string Title, string Text) Extract(string html)
    {
        var title = "";
        if (TitleTag().Match(html) is { Success: true } match)
        {
            title = Collapse(WebUtility.HtmlDecode(Tags().Replace(match.Groups[1].Value, " ")));
        }

        if (title.Length == 0 && OgTitle().Match(html) is { Success: true } og)
        {
            title = Collapse(WebUtility.HtmlDecode(og.Groups[1].Value));
        }

        var body = Noise().Replace(html, " ");
        body = Comments().Replace(body, " ");
        // Prefer the article itself over menus and footers when the page marks it.
        var focus = Article().Match(body);
        if (!focus.Success || focus.Groups[1].Value.Length < 400)
        {
            focus = Main().Match(body);
        }

        if (focus.Success && focus.Groups[1].Value.Length >= 400)
        {
            body = focus.Groups[1].Value;
        }

        body = Blocks().Replace(body, "\n");
        body = Tags().Replace(body, " ");
        body = WebUtility.HtmlDecode(body);
        var lines = body.Split('\n').Select(Collapse).Where(line => line.Length > 0);
        return (title, string.Join('\n', lines));
    }

    private static string Collapse(string text) => Spaces().Replace(text, " ").Trim();

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleTag();

    [GeneratedRegex("""<meta[^>]+property=["']og:title["'][^>]+content=["']([^"']*)["']""", RegexOptions.IgnoreCase)]
    private static partial Regex OgTitle();

    [GeneratedRegex(@"<(script|style|noscript|svg|nav|footer|header|aside|form|iframe|template)\b.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Noise();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex Comments();

    [GeneratedRegex(@"<article\b[^>]*>(.*)</article\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Article();

    [GeneratedRegex(@"<main\b[^>]*>(.*)</main\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex Main();

    [GeneratedRegex(@"</?(p|div|br|li|ul|ol|h[1-6]|tr|section|blockquote|pre|table)\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Blocks();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
