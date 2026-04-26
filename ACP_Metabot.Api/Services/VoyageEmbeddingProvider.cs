using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ACP_Metabot.Api.Services;

public class VoyageEmbeddingProvider : IEmbeddingProvider
{
    public string ModelId { get; }
    public int Dimension { get; }

    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly ILogger<VoyageEmbeddingProvider> _logger;

    public VoyageEmbeddingProvider(IConfiguration config, IHttpClientFactory httpFactory,
        ILogger<VoyageEmbeddingProvider> logger)
    {
        ModelId = config["Embeddings:Model"] ?? "voyage-finance-2";
        Dimension = config.GetValue<int?>("Embeddings:Dimension") ?? DimensionForModel(ModelId);
        _apiKey = Environment.GetEnvironmentVariable("VOYAGE_API_KEY")
            ?? throw new InvalidOperationException("VOYAGE_API_KEY not set");
        _http = httpFactory.CreateClient(nameof(VoyageEmbeddingProvider));
        _http.BaseAddress ??= new Uri("https://api.voyageai.com/v1/");
        _http.Timeout = TimeSpan.FromMinutes(5);
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _apiKey);
        _logger = logger;
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (texts.Count == 0) return Array.Empty<float[]>();

        var req = new VoyageRequest
        {
            Input = texts.ToArray(),
            Model = ModelId,
            InputType = "document"
        };

        using var resp = await _http.PostAsJsonAsync("embeddings", req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Voyage embeddings call failed: {(int)resp.StatusCode} {resp.StatusCode} — {body}");
        }
        var parsed = await resp.Content.ReadFromJsonAsync<VoyageResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Voyage returned empty body");
        return parsed.Data.Select(d => d.Embedding).ToArray();
    }

    /// <summary>
    /// Embeds a single QUERY string. Voyage uses input_type="query" for
    /// asymmetric retrieval (different from "document"). Use this for
    /// search queries; use EmbedAsync for stored docs.
    /// </summary>
    public async Task<float[]> EmbedQueryAsync(string text, CancellationToken ct)
    {
        var req = new VoyageRequest
        {
            Input = new[] { text },
            Model = ModelId,
            InputType = "query"
        };
        using var resp = await _http.PostAsJsonAsync("embeddings", req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new InvalidOperationException(
                $"Voyage query embedding failed: {(int)resp.StatusCode} — {body}");
        }
        var parsed = await resp.Content.ReadFromJsonAsync<VoyageResponse>(cancellationToken: ct)
            ?? throw new InvalidOperationException("Voyage returned empty body");
        return parsed.Data[0].Embedding;
    }

    private static int DimensionForModel(string model) => model switch
    {
        "voyage-3-large" => 1024,
        "voyage-3" => 1024,
        "voyage-3-lite" => 512,
        "voyage-finance-2" => 1024,
        _ => 1024
    };

    private class VoyageRequest
    {
        [JsonPropertyName("input")]    public string[] Input { get; set; } = Array.Empty<string>();
        [JsonPropertyName("model")]    public string Model { get; set; } = "";
        [JsonPropertyName("input_type")] public string InputType { get; set; } = "document";
    }

    private class VoyageResponse
    {
        [JsonPropertyName("data")] public List<VoyageDatum> Data { get; set; } = new();
    }

    private class VoyageDatum
    {
        [JsonPropertyName("embedding")] public float[] Embedding { get; set; } = Array.Empty<float>();
        [JsonPropertyName("index")]     public int Index { get; set; }
    }
}
