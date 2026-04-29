using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ACP_Metabot.Api.Services;

// Wraps Voyage's /v1/rerank endpoint. Takes a (query, list-of-documents)
// pair and returns the documents reordered by relevance score. Used by
// SearchService as a refinement pass over the top-K cosine candidates.
public class VoyageRerankProvider
{
    public string ModelId { get; }

    private readonly HttpClient _http;
    private readonly ILogger<VoyageRerankProvider> _logger;

    public VoyageRerankProvider(IConfiguration config, IHttpClientFactory httpFactory,
        ILogger<VoyageRerankProvider> logger)
    {
        ModelId = config["Reranker:Model"] ?? "rerank-2";
        var apiKey = Environment.GetEnvironmentVariable("VOYAGE_API_KEY")
            ?? throw new InvalidOperationException("VOYAGE_API_KEY not set");
        _http = httpFactory.CreateClient(nameof(VoyageRerankProvider));
        _http.BaseAddress ??= new Uri("https://api.voyageai.com/v1/");
        // Reranker is fast — most calls finish under 500ms. Keep the timeout
        // tight so a stalled call doesn't gum up search latency.
        _http.Timeout = TimeSpan.FromSeconds(10);
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        _logger = logger;
    }

    // Returns indices into `documents` sorted by relevance score descending.
    // Length of result == documents.Count.
    public async Task<int[]> RerankAsync(string query, IReadOnlyList<string> documents,
        CancellationToken ct)
    {
        if (documents.Count == 0) return Array.Empty<int>();

        var req = new RerankRequest
        {
            Query = query,
            Documents = documents as string[] ?? documents.ToArray(),
            Model = ModelId,
            ReturnDocuments = false,
            // truncation=true silently caps long docs to the model's window
            // instead of returning a 400. Our docs are short anyway.
            Truncation = true
        };

        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsJsonAsync("rerank", req, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex)
        {
            throw new VoyageApiException(0, null,
                $"Voyage rerank network failure: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is TaskCanceledException || ex is OperationCanceledException)
        {
            throw new VoyageApiException(0, null,
                $"Voyage rerank timed out: {ex.Message}", ex);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new VoyageApiException((int)resp.StatusCode, body,
                    $"Voyage rerank call failed: {(int)resp.StatusCode} {resp.StatusCode} — {body}");
            }
            var parsed = await resp.Content.ReadFromJsonAsync<RerankResponse>(cancellationToken: ct)
                ?? throw new InvalidOperationException("Voyage rerank returned empty body");

            return parsed.Data
                .OrderByDescending(d => d.RelevanceScore)
                .Select(d => d.Index)
                .ToArray();
        }
    }

    private class RerankRequest
    {
        [JsonPropertyName("query")]            public string Query { get; set; } = "";
        [JsonPropertyName("documents")]        public string[] Documents { get; set; } = Array.Empty<string>();
        [JsonPropertyName("model")]            public string Model { get; set; } = "";
        [JsonPropertyName("return_documents")] public bool ReturnDocuments { get; set; }
        [JsonPropertyName("truncation")]       public bool Truncation { get; set; }
    }

    private class RerankResponse
    {
        [JsonPropertyName("data")] public List<RerankDatum> Data { get; set; } = new();
    }

    private class RerankDatum
    {
        [JsonPropertyName("index")]           public int Index { get; set; }
        [JsonPropertyName("relevance_score")] public double RelevanceScore { get; set; }
    }
}
