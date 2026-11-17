namespace Chaos.Application.Abstractions;

/// <summary>A web page reduced to what a person (or a small model) reads: its title, address after redirects and plain text.</summary>
public sealed record FetchedPage(Uri Address, string Title, string Text);

/// <summary>A failure the person can read: bad address, unreachable, not a web page or too big.</summary>
public sealed class SiteReadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Downloads a web page and extracts its readable text.</summary>
public interface ISitePageFetcher
{
    /// <exception cref="SiteReadException">The page could not be fetched or is not readable text.</exception>
    Task<FetchedPage> FetchAsync(Uri address, CancellationToken ct);
}

/// <summary>How worth reading a page is, as judged by the local model.</summary>
public enum SiteVerdict
{
    Skip = 0,
    Skim = 1,
    Read = 2,
}

/// <summary>The model's reading of one page: a short summary, a recommendation with its reason, and a few topic tags.</summary>
public sealed record SiteAnalysis(string Summary, SiteVerdict Verdict, string Reason, IReadOnlyList<string> Tags);

/// <summary>Summarizes a page with a small local model. Nothing leaves the machine.</summary>
public interface ISiteSummarizer
{
    /// <exception cref="SiteReadException">The model is not running, not installed or gave no usable answer.</exception>
    Task<SiteAnalysis> AnalyzeAsync(FetchedPage page, CancellationToken ct);
}
