using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Chaos.Application.Abstractions;

namespace Chaos.Infrastructure.Sites;

/// <summary>
/// Summarizes a page with a small open model served by a local Ollama (http://localhost:11434 by
/// default, model qwen2.5:3b). Override with CHAOS_LLM_URL and CHAOS_LLM_MODEL. The page text goes
/// to the local server only. The model is asked for JSON that matches a schema, so the answer
/// parses without guessing.
/// </summary>
public sealed class OllamaSiteSummarizer : ISiteSummarizer, IDisposable
{
    public const string DefaultUrl = "http://localhost:11434";
    public const string DefaultModel = "qwen2.5:3b";
    public const int MaxPageChars = 8000;
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(3);

    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly string _model;

    public OllamaSiteSummarizer(string? baseUrl = null, string? model = null)
        : this(new HttpClient { BaseAddress = new Uri(Normalize(baseUrl ?? Environment.GetEnvironmentVariable("CHAOS_LLM_URL") ?? DefaultUrl)), Timeout = System.Threading.Timeout.InfiniteTimeSpan },
            model ?? Environment.GetEnvironmentVariable("CHAOS_LLM_MODEL") ?? DefaultModel, true)
    {
    }

    public OllamaSiteSummarizer(HttpClient http, string model, bool ownsClient = false)
    {
        _http = http;
        _model = model;
        _ownsClient = ownsClient;
    }

    private static string Normalize(string url) => url.EndsWith('/') ? url : url + "/";

    private const string SchemaJson = """
        {
          "type": "object",
          "properties": {
            "summary": { "type": "string" },
            "verdict": { "type": "string", "enum": ["read", "skim", "skip"] },
            "reason": { "type": "string" },
            "tags": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["summary", "verdict", "reason", "tags"]
        }
        """;

    private const string SystemPrompt = """
        You help a busy person decide what to read. You get the text of one web page.
        Reply with JSON only:
        - summary: 3 to 5 plain sentences on what the page says. Do not invent facts that are not in the text.
        - verdict: "read" if it is substantive and original, "skim" if it is partly useful or mostly repeats common knowledge, "skip" if it is thin, promotional, clickbait or a list of links.
        - reason: one short sentence that justifies the verdict.
        - tags: 1 to 3 lowercase topic tags, each a single word or hyphenated words (for example "rust", "machine-learning").
        """;

    public async Task<SiteAnalysis> AnalyzeAsync(FetchedPage page, CancellationToken ct)
    {
        var text = page.Text.Length > MaxPageChars ? page.Text[..MaxPageChars] : page.Text;
        var body = new JsonObject
        {
            ["model"] = _model,
            ["stream"] = false,
            ["format"] = JsonNode.Parse(SchemaJson),
            ["options"] = new JsonObject { ["temperature"] = 0.2, ["num_ctx"] = 4096 },
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = $"Title: {page.Title}\nAddress: {page.Address}\n\nPage text:\n{text}" }),
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var response = await _http.PostAsJsonAsync("api/chat", body, timeout.Token);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                throw new SiteReadException($"The local model {_model} is not installed. Run: ollama pull {_model}");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new SiteReadException($"The local model server answered {(int)response.StatusCode}.");
            }

            var reply = await response.Content.ReadFromJsonAsync<JsonObject>(timeout.Token);
            return Parse(reply?["message"]?["content"]?.GetValue<string>());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new SiteReadException("The local model took too long to answer.");
        }
        catch (HttpRequestException)
        {
            throw new SiteReadException($"Chaos could not reach the local model. Install Ollama (ollama.com), run \"ollama pull {_model}\" and keep it running.");
        }
    }

    /// <summary>Reads the model's JSON answer; a missing summary is a failure, everything else has a safe default.</summary>
    public static SiteAnalysis Parse(string? content)
    {
        JsonObject? json;
        try
        {
            json = JsonNode.Parse(content ?? "") as JsonObject;
        }
        catch (JsonException)
        {
            json = null;
        }

        var summary = Text(json?["summary"]);
        if (summary.Length == 0)
        {
            throw new SiteReadException("The local model gave no usable summary. Try again.");
        }

        var tags = json?["tags"] is JsonArray array ? [.. array.Select(Text)] : new List<string>();
        var verdict = Text(json?["verdict"]).ToLowerInvariant() switch
        {
            "read" => SiteVerdict.Read,
            "skip" => SiteVerdict.Skip,
            _ => SiteVerdict.Skim,
        };
        return new(summary, verdict, Text(json?["reason"]), tags);
    }

    private static string Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text.Trim() : "";

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
