using System.Text.Json;
using System.Text.Json.Serialization;

namespace ACP_Metabot.Api.Services;

public record CategoryEntry(
    [property: JsonPropertyName("name")]        string Name,
    [property: JsonPropertyName("description")] string Description);

// Loads a static list of canonical marketplace categories, embeds them once
// via Voyage on first refresh, then classifies offerings by nearest-cosine
// category. Pure in-memory — no DB persistence — because the list is small
// and the embeddings re-derive from JSON if the bot restarts.
public class CategoryService
{
    private readonly VoyageEmbeddingProvider _embedder;
    private readonly ILogger<CategoryService> _logger;

    private List<CategoryEntry> _categories = new();
    private float[][]? _categoryEmbeddings;
    private string _modelTag = "";

    public IReadOnlyList<CategoryEntry> Categories => _categories;
    public bool IsReady => _categoryEmbeddings is not null && _modelTag == _embedder.ModelId;

    public CategoryService(VoyageEmbeddingProvider embedder, ILogger<CategoryService> logger)
    {
        _embedder = embedder;
        _logger = logger;
    }

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        // Idempotent: skip if we already embedded the current list with the
        // current model. CategoryService.RefreshAsync gets called every
        // indexer cycle so this short-circuits 99% of calls.
        if (IsReady) return;

        if (_categories.Count == 0)
        {
            _categories = LoadCategoriesFromDisk();
            if (_categories.Count == 0)
            {
                _logger.LogWarning("[categories] no categories.json found — classification disabled");
                return;
            }
        }

        var docs = _categories
            .Select(c => $"Category: {c.Name}\nDescription: {c.Description}")
            .ToArray();
        var vectors = await _embedder.EmbedAsync(docs, ct);
        _categoryEmbeddings = vectors.ToArray();
        _modelTag = _embedder.ModelId;
        _logger.LogInformation("[categories] embedded {N} categories with model={Model}",
            _categories.Count, _modelTag);
    }

    public string? Classify(float[] offeringEmbedding)
    {
        var embeddings = _categoryEmbeddings;
        if (embeddings is null || _categories.Count == 0) return null;

        int bestIdx = -1;
        double bestScore = double.NegativeInfinity;
        for (int i = 0; i < embeddings.Length; i++)
        {
            var score = CosineSimilarity(offeringEmbedding, embeddings[i]);
            if (score > bestScore)
            {
                bestScore = score;
                bestIdx = i;
            }
        }
        return bestIdx >= 0 ? _categories[bestIdx].Name : null;
    }

    private List<CategoryEntry> LoadCategoriesFromDisk()
    {
        // Search a few likely paths so this works in dev (next to the project)
        // and prod (next to the published assemblies in the docker image).
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "data", "seed", "categories.json"),
            Path.Combine(Directory.GetCurrentDirectory(), "data", "seed", "categories.json"),
        };
        foreach (var path in candidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                var json = File.ReadAllText(path);
                var list = JsonSerializer.Deserialize<List<CategoryEntry>>(json,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                _logger.LogInformation("[categories] loaded {N} from {Path}", list?.Count ?? 0, path);
                return list ?? new List<CategoryEntry>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[categories] failed to parse {Path}", path);
            }
        }
        return new List<CategoryEntry>();
    }

    private static double CosineSimilarity(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, na = 0, nb = 0;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        var denom = Math.Sqrt(na) * Math.Sqrt(nb);
        return denom == 0 ? 0 : dot / denom;
    }
}
