using System.Net;
using System.Text;
using Chaos.Application.Abstractions;
using Chaos.Application.Pad;
using Chaos.Application.Sites;
using Chaos.Application.Tags;
using Chaos.Infrastructure.Bootstrap;
using Chaos.Infrastructure.Sites;
using Chaos.Infrastructure.Tests.Testing;

namespace Chaos.Infrastructure.Tests.Sites;

/// <summary>"New site": page text extraction, the fetcher's rules, the model's JSON, and the stored tagged note against real SQLite.</summary>
public sealed class SiteNoteTests
{
    private sealed class GuidIds : IIdGenerator
    {
        public Guid NewId() => Guid.NewGuid();
    }

    private sealed class FakeFetcher(FetchedPage? page = null, string? failure = null) : ISitePageFetcher
    {
        public Task<FetchedPage> FetchAsync(Uri address, CancellationToken ct) =>
            failure is not null ? throw new SiteReadException(failure) : Task.FromResult(page ?? new FetchedPage(address, "A page", "Body"));
    }

    private sealed class FakeSummarizer(SiteAnalysis analysis) : ISiteSummarizer
    {
        public Task<SiteAnalysis> AnalyzeAsync(FetchedPage page, CancellationToken ct) => Task.FromResult(analysis);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    [Test]
    public async Task HtmlText_KeepsTheArticleAndDropsScriptsAndMenus()
    {
        var filler = string.Concat(Enumerable.Repeat("Real sentence about rust ownership. ", 20));
        var html = $"<html><head><title>Rust &amp; You</title><script>var x = 'secret';</script></head><body><nav>Home About</nav><article><h1>Ownership</h1><p>{filler}</p></article><footer>Copyright</footer></body></html>";

        var (title, text) = HtmlText.Extract(html);

        await Assert.That(title).IsEqualTo("Rust & You");
        await Assert.That(text).Contains("Real sentence about rust ownership.");
        await Assert.That(text).DoesNotContain("secret");
        await Assert.That(text).DoesNotContain("Home About");
        await Assert.That(text).DoesNotContain("Copyright");
    }

    [Test]
    public async Task Fetcher_RejectsPdfsAndPagesWithoutText()
    {
        using var pdf = new HttpSitePageFetcher(new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3]) { Headers = { ContentType = new("application/pdf") } } })));
        var notWeb = await Assert.That(async () => await pdf.FetchAsync(new("https://example.com/a.pdf"), CancellationToken.None)).Throws<SiteReadException>();
        await Assert.That(notWeb!.Message).Contains("not a web page");

        using var empty = new HttpSitePageFetcher(new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html><body><div id=app></div></body></html>", Encoding.UTF8, "text/html") })));
        var thin = await Assert.That(async () => await empty.FetchAsync(new("https://example.com/spa"), CancellationToken.None)).Throws<SiteReadException>();
        await Assert.That(thin!.Message).Contains("almost no readable text");
    }

    [Test]
    public async Task Fetcher_ReadsTitleAndText()
    {
        var body = $"<html><head><title>Hello</title></head><body><main><p>{string.Concat(Enumerable.Repeat("Plenty of readable words here. ", 20))}</p></main></body></html>";
        using var fetcher = new HttpSitePageFetcher(new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") })));

        var page = await fetcher.FetchAsync(new("https://example.com/hello"), CancellationToken.None);

        await Assert.That(page.Title).IsEqualTo("Hello");
        await Assert.That(page.Text).Contains("Plenty of readable words here.");
    }

    [Test]
    public async Task Summarizer_ParsesTheModelAnswerAndDefaultsTheVerdict()
    {
        var parsed = OllamaSiteSummarizer.Parse("""{"summary":" Ownership explained. ","verdict":"READ","reason":"Original.","tags":["Rust","memory"]}""");
        await Assert.That(parsed.Summary).IsEqualTo("Ownership explained.");
        await Assert.That(parsed.Verdict).IsEqualTo(SiteVerdict.Read);
        await Assert.That(parsed.Tags).IsEquivalentTo(["Rust", "memory"]);

        var unsure = OllamaSiteSummarizer.Parse("""{"summary":"Something.","verdict":"maybe"}""");
        await Assert.That(unsure.Verdict).IsEqualTo(SiteVerdict.Skim);
        await Assert.That(unsure.Tags.Count).IsEqualTo(0);

        await Assert.That(() => OllamaSiteSummarizer.Parse("not json")).Throws<SiteReadException>();
        await Assert.That(() => OllamaSiteSummarizer.Parse("""{"summary":"  "}""")).Throws<SiteReadException>();
    }

    [Test]
    public async Task Summarizer_ExplainsAMissingServerAndModel()
    {
        using var down = new OllamaSiteSummarizer(new HttpClient(new Handler(_ => throw new HttpRequestException("refused")) { }) { BaseAddress = new("http://localhost:11434/") }, "qwen2.5:3b");
        var page = new FetchedPage(new("https://example.com"), "T", "Text");
        var unreachable = await Assert.That(async () => await down.AnalyzeAsync(page, CancellationToken.None)).Throws<SiteReadException>();
        await Assert.That(unreachable!.Message).Contains("ollama pull qwen2.5:3b");

        using var missing = new OllamaSiteSummarizer(new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.NotFound))) { BaseAddress = new("http://localhost:11434/") }, "qwen2.5:3b");
        var notInstalled = await Assert.That(async () => await missing.AnalyzeAsync(page, CancellationToken.None)).Throws<SiteReadException>();
        await Assert.That(notInstalled!.Message).Contains("not installed");
    }

    [Test]
    public async Task Addresses_AcceptBareHostsAndRefuseOtherSchemes()
    {
        await Assert.That(SiteNoteCommands.ParseAddress(" example.com/a ").AbsoluteUri).IsEqualTo("https://example.com/a");
        await Assert.That(() => SiteNoteCommands.ParseAddress("file:///c:/secret.txt")).Throws<AppException>();
        await Assert.That(() => SiteNoteCommands.ParseAddress("  ")).Throws<AppException>();
    }

    [Test]
    public async Task TopicTags_AreCleanedDedupedAndLimited()
    {
        var tags = SiteNoteCommands.CleanTags(["#Rust", "rust", "Machine Learning", "reading/skip", "", "a", "b", "c"]);

        await Assert.That(tags).IsEquivalentTo(["rust", "machine-learning", "a"]);
    }

    private static async Task<(PadCommands Pad, TagCommands Tags, SiteNoteCommands Sites, IDisposable Storage)> StartAsync(ISitePageFetcher fetcher, ISiteSummarizer summarizer)
    {
        var ids = new GuidIds();
        var storage = await StorageBootstrap.StartAsync(StorageOptions.FromDataRoot(TestPaths.NewDataRoot()), "0.1.0", ids, TimeProvider.System, CancellationToken.None);
        var pad = new PadCommands(storage.Store, ids, TimeProvider.System);
        var tags = new TagCommands(storage.Store, ids, TimeProvider.System);
        return (pad, tags, new SiteNoteCommands(fetcher, summarizer, pad, tags), storage);
    }

    [Test]
    public async Task Add_StoresTheAddressAtOnceThenFillsInTheSummaryAndTags()
    {
        var gate = new TaskCompletionSource();
        var analysis = new SiteAnalysis("Ownership explained in five steps.", SiteVerdict.Read, "Original and concrete.", ["Rust", "memory safety"]);
        var slow = new SlowSummarizer(gate.Task, analysis);
        var (pad, tags, sites, storage) = await StartAsync(new FakeFetcher(new FetchedPage(new("https://example.com/own"), "Ownership", "text")), slow);
        using var _ = storage;

        var result = await sites.AddAsync(new(Guid.NewGuid().ToString(), "example.com/own"));

        await Assert.That(result.Item.RawMarkdown).Contains("https://example.com/own");
        await Assert.That(result.Item.RawMarkdown).Contains(SiteNoteCommands.PendingMarker);
        await Assert.That(result.Completed.IsCompleted).IsFalse();

        gate.SetResult();
        await result.Completed;

        var stored = (await pad.GetAsync(result.Item.Id)).Item.RawMarkdown;
        await Assert.That(stored).Contains("# Ownership");
        await Assert.That(stored).Contains("**Worth reading.** Original and concrete.");
        await Assert.That(stored).Contains("Ownership explained in five steps.");
        await Assert.That(stored).DoesNotContain(SiteNoteCommands.PendingMarker);
        var paths = (await tags.ListAsync()).Select(x => x.Path).ToList();
        await Assert.That(paths).Contains("reading/worth-it");
        await Assert.That(paths).Contains("rust");
        await Assert.That(paths).Contains("memory-safety");
        await Assert.That((await tags.ListTargetTagsAsync(TagTargetKind.Pad, result.Item.Id)).Count).IsEqualTo(3);
    }

    [Test]
    public async Task Add_WhenTheSiteRefuses_KeepsTheAddressWithAShortNote()
    {
        var (pad, tags, sites, storage) = await StartAsync(new FakeFetcher(failure: "The site refused access to the page."), new FakeSummarizer(new("x", SiteVerdict.Skim, "", [])));
        using var _ = storage;

        var result = await sites.AddAsync(new(Guid.NewGuid().ToString(), "https://example.com/blocked"));
        await result.Completed;

        var stored = (await pad.GetAsync(result.Item.Id)).Item.RawMarkdown;
        await Assert.That(stored).Contains("https://example.com/blocked");
        await Assert.That(stored).Contains("Could not read this page: The site refused access to the page.");
        await Assert.That((await tags.ListAsync()).Select(x => x.Path)).Contains("reading/unread");
    }

    [Test]
    public async Task Add_NeverOverwritesANoteThePersonEditedMeanwhile()
    {
        var gate = new TaskCompletionSource();
        var (pad, _, sites, storage) = await StartAsync(new FakeFetcher(), new SlowSummarizer(gate.Task, new("Summary.", SiteVerdict.Skim, "", [])));
        using var _ = storage;
        var result = await sites.AddAsync(new(Guid.NewGuid().ToString(), "https://example.com/x"));

        await pad.SaveTextAsync(new(result.Item.Id, 1, "my own words"));
        gate.SetResult();
        await result.Completed;

        await Assert.That((await pad.GetAsync(result.Item.Id)).Item.RawMarkdown).IsEqualTo("my own words");
    }

    [Test]
    public async Task Add_RefusesAnAddressThatIsNotAWebPageBeforeStoringAnything()
    {
        var (pad, _, sites, storage) = await StartAsync(new FakeFetcher(), new FakeSummarizer(new("x", SiteVerdict.Skim, "", [])));
        using var _ = storage;

        await Assert.That(async () => await sites.AddAsync(new(Guid.NewGuid().ToString(), "ftp://example.com/x"))).Throws<AppException>();

        await Assert.That((await pad.ListAsync(false, null)).Count).IsEqualTo(0);
    }

    private sealed class SlowSummarizer(Task gate, SiteAnalysis analysis) : ISiteSummarizer
    {
        public async Task<SiteAnalysis> AnalyzeAsync(FetchedPage page, CancellationToken ct)
        {
            await gate;
            return analysis;
        }
    }
}
