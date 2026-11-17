using Chaos.Application.Abstractions;

namespace Chaos.Infrastructure.Sites;

/// <summary>
/// Tries the local model first and, when it is unreachable, missing or gives no usable answer, falls
/// back to the model-free summarizer so "New site" works without Ollama installed.
/// </summary>
public sealed class FallbackSiteSummarizer(ISiteSummarizer primary, ISiteSummarizer fallback) : ISiteSummarizer
{
    public async Task<SiteAnalysis> AnalyzeAsync(FetchedPage page, CancellationToken ct)
    {
        try
        {
            return await primary.AnalyzeAsync(page, ct);
        }
        catch (SiteReadException)
        {
            return await fallback.AnalyzeAsync(page, ct);
        }
    }
}
