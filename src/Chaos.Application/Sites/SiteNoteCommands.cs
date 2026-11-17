using System.Text;
using Chaos.Application.Abstractions;
using Chaos.Application.Contracts;
using Chaos.Application.Pad;
using Chaos.Application.Tags;
using Chaos.Domain.Tags;

namespace Chaos.Application.Sites;

public sealed record AddSiteNoteCommand(string OperationId, string Url);

/// <summary>The stored note (placeholder text) and the background reading that will replace it.</summary>
public sealed record SiteNoteResult(PadItemRecord Item, Task Completed);

/// <summary>
/// "New site" on the pad: stores the address at once, then in the background fetches the page, has the local model summarize it and judge whether it
/// is worth reading, then stores the result as a pad note tagged with its topics and the verdict.
/// </summary>
public sealed class SiteNoteCommands(ISitePageFetcher fetcher, ISiteSummarizer summarizer, PadCommands pad, TagCommands tags)
{
    public const int MaxTopicTags = 3;

    /// <summary>Tag path for the verdict, e.g. reading/worth-it.</summary>
    public static string VerdictTag(SiteVerdict verdict) => verdict switch
    {
        SiteVerdict.Read => "reading/worth-it",
        SiteVerdict.Skim => "reading/skim",
        _ => "reading/skip",
    };

    public static string VerdictLabel(SiteVerdict verdict) => verdict switch
    {
        SiteVerdict.Read => "Worth reading",
        SiteVerdict.Skim => "Skim it",
        _ => "Skip it",
    };

    /// <summary>Accepts "example.com/x" too; only http and https addresses are read.</summary>
    public static Uri ParseAddress(string? text)
    {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            throw AppException.Validation("url", "Paste the address of a web page.");
        }

        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "https://" + trimmed;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
        {
            throw AppException.Validation("url", "Use a web address that starts with http:// or https://.");
        }

        return uri;
    }

    /// <summary>The marker the pad watches for: a note holding it is still being read in the background.</summary>
    public const string PendingMarker = "Summarizing in the background";

    public const string UnreadTag = "reading/unread";

    /// <summary>
    /// Stores the note at once with just the address, then reads the page in the background and
    /// replaces the placeholder. <see cref="SiteNoteResult.Completed"/> finishes when that is done.
    /// </summary>
    public async Task<SiteNoteResult> AddAsync(AddSiteNoteCommand command, CancellationToken ct = default)
    {
        var address = ParseAddress(command.Url);
        var placeholder = $"{address.AbsoluteUri}\n\n_{PendingMarker}…_\n";
        var created = await pad.CreateAsync(new(command.OperationId, placeholder), ct);
        var item = created.Item;
        var work = created.Replayed ? Task.CompletedTask : Task.Run(() => FillAsync(item, address, command.OperationId));
        return new(item, work);
    }

    private async Task FillAsync(PadItemRecord item, Uri address, string operationId)
    {
        try
        {
            string markdown;
            IReadOnlyList<string> paths;
            FetchedPage? page = null;
            try
            {
                page = await fetcher.FetchAsync(address, CancellationToken.None);
                var analysis = await summarizer.AnalyzeAsync(page, CancellationToken.None);
                markdown = Markdown(page, analysis);
                paths = [VerdictTag(analysis.Verdict), .. CleanTags(analysis.Tags)];
            }
            catch (SiteReadException error)
            {
                markdown = FailureMarkdown(address, page, error.Message);
                paths = [UnreadTag];
            }

            if (!await ReplaceAsync(item, markdown))
            {
                return;
            }

            for (var index = 0; index < paths.Count; index++)
            {
                try
                {
                    await tags.LinkAsync(new($"{operationId}:tag:{index}", TagTargetKind.Pad, item.Id, null, paths[index]));
                }
                catch (AppException)
                {
                    // A tag the model made up that is not a valid name must not lose the summary.
                }
            }
        }
        catch (Exception)
        {
            // The note keeps its address; a deleted note or a closing store ends the background work quietly.
        }
    }

    /// <summary>Replaces the placeholder unless the person already edited or deleted the note.</summary>
    private async Task<bool> ReplaceAsync(PadItemRecord item, string markdown)
    {
        var current = (await pad.GetAsync(item.Id)).Item;
        if (current.DeletedAtUtc is not null || current.RawMarkdown != item.RawMarkdown)
        {
            return false;
        }

        await pad.SaveTextAsync(new(item.Id, current.DraftVersion + 1, markdown));
        return true;
    }

    public static string FailureMarkdown(Uri address, FetchedPage? page, string reason)
    {
        var text = new StringBuilder();
        if (page is not null && !string.IsNullOrWhiteSpace(page.Title))
        {
            text.Append("# ").Append(page.Title.Trim().ReplaceLineEndings(" ")).Append("\n\n");
        }

        text.Append(address.AbsoluteUri).Append("\n\n");
        text.Append(page is null ? "Could not read this page: " : "Could not summarize this page: ").Append(reason.Trim()).Append('\n');
        return text.ToString();
    }

    /// <summary>Lowercase single-word-ish topics the tag rules accept, without duplicates, at most <see cref="MaxTopicTags"/>.</summary>
    public static IReadOnlyList<string> CleanTags(IEnumerable<string> raw)
    {
        var result = new List<string>();
        foreach (var tag in raw)
        {
            var cleaned = new StringBuilder();
            foreach (var ch in (tag ?? "").Trim().TrimStart('#').ToLowerInvariant())
            {
                cleaned.Append(char.IsLetterOrDigit(ch) ? ch : '-');
            }

            var text = string.Join('-', cleaned.ToString().Split('-', StringSplitOptions.RemoveEmptyEntries));
            if (text.Length is 0 or > 30 || result.Contains(text, StringComparer.Ordinal) || text.StartsWith("reading", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                _ = TagName.Path(text);
            }
            catch (Exception)
            {
                continue;
            }

            result.Add(text);
            if (result.Count == MaxTopicTags)
            {
                break;
            }
        }

        return result;
    }

    public static string Markdown(FetchedPage page, SiteAnalysis analysis)
    {
        var title = string.IsNullOrWhiteSpace(page.Title) ? page.Address.Host : page.Title.Trim();
        var text = new StringBuilder();
        text.Append("# ").Append(title.ReplaceLineEndings(" ")).Append("\n\n");
        text.Append(page.Address.AbsoluteUri).Append("\n\n");
        text.Append("**").Append(VerdictLabel(analysis.Verdict)).Append(".**");
        if (!string.IsNullOrWhiteSpace(analysis.Reason))
        {
            text.Append(' ').Append(analysis.Reason.Trim());
        }

        text.Append("\n\n").Append(analysis.Summary.Trim()).Append('\n');
        return text.ToString();
    }
}
