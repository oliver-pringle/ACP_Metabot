using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ACP_Metabot.Api.Services;

// Fallback embedding provider for when Voyage is rate-limited or down during
// an indexer cycle. Mirrors VoyageEmbeddingProvider's shape and dimension
// (Cohere embed-english-v3.0 and Cohere embed-multilingual-v3.0 are both 1024
// dim, matching voyage-finance-2). NOT semantically equivalent: a Voyage
// embedding and a Cohere embedding share dimension but not vector space, so
// search results across mixed-model embeddings are garbage. The lifecycle
// re-embedder (v1.5) is expected to overwrite Cohere-tagged rows back to
// Voyage once the primary is healthy. Search-path queries (EmbedQueryAsync)
// still go through Voyage directly and fail loud — silently falling back
// there would return matchless results without telling anyone.
public class CohereEmbeddingProvider : IEmbeddingProvider
{
    public string ModelId { get; }
    public int Dimension { get; }

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly ILogger<CohereEmbeddingProvider> _logger;

    public CohereEmbeddingProvider(
        IConfiguration config,
        IHttpClientFactory httpFactory,
        ILogger<CohereEmbeddingProvider> logger)
    {
        ModelId = config["Embeddings:Fallback:Model"] ?? "embed-english-v3.0";
        Dimension = config.GetValue<int?>("Embeddings:Fallback:Dimension")
            ?? DimensionForModel(ModelId);
        _apiKey = Environment.GetEnvironmentVariable("COHERE_API_KEY")
            ?? throw new InvalidOperationException("COHERE_API_KEY not set");
        _http = httpFactory.CreateClient(nameof(CohereEmbeddingProvider));
        _http.BaseAddress ??= new Uri("https://api.cohere.com/v1/");
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        _logger = logger;
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (texts.Count == 0) return Array.Empty<float[]>();

        var req = new CohereRequest
        {
            Texts = texts.ToArray(),
            Model = ModelId,
            InputType = "search_document"
        };

        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsJsonAsync("embed", req, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex)
        {
            throw new CohereApiException(0, null,
                $"Cohere embeddings network failure: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is TaskCanceledException || ex is OperationCanceledException)
        {
            throw new CohereApiException(0, null,
                $"Cohere embeddings timed out: {ex.Message}", ex);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                throw new CohereApiException((int)resp.StatusCode, body,
                    $"Cohere embeddings call failed: {(int)resp.StatusCode} {resp.StatusCode} — {body}");
            }
            var parsed = await resp.Content.ReadFromJsonAsync<CohereResponse>(cancellationToken: ct)
                ?? throw new InvalidOperationException("Cohere returned empty body");
            return parsed.Embeddings;
        }
    }

    private static int DimensionForModel(string model) => model switch
    {
        "embed-english-v3.0" => 1024,
        "embed-multilingual-v3.0" => 1024,
        "embed-english-light-v3.0" => 384,
        "embed-multilingual-light-v3.0" => 384,
        _ => 1024
    };

    private class CohereRequest
    {
        [JsonPropertyName("texts")] public string[] Texts { get; set; } = Array.Empty<string>();
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("input_type")] public string InputType { get; set; } = "search_document";
    }

    private class CohereResponse
    {
        [JsonPropertyName("embeddings")] public float[][] Embeddings { get; set; } = Array.Empty<float[]>();
    }
}

public class CohereApiException : Exception
{
    public int StatusCode { get; }
    public string? Body { get; }
    public CohereApiException(int statusCode, string? body, string message, Exception? inner = null)
        : base(message, inner)
    {
        StatusCode = statusCode;
        Body = body;
    }
}
