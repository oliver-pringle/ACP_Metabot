using System.Text.Json;
using System.Text.Json.Serialization;

namespace ACP_Metabot.Api.Services;

public sealed record GlossaryEntry(
    [property: JsonPropertyName("canonical")] string Canonical,
    [property: JsonPropertyName("aliases")] IReadOnlyList<string> Aliases);

public sealed record ExpandedQuery(
    string Primary,
    IReadOnlyList<string> Synonyms,
    IReadOnlyList<string> GlossaryHits);

/// <summary>
/// v1.10 Phase 1: pure-CPU glossary lookup. Substring-matches a query against
/// the alias list and emits canonical synonyms when any alias appears inside
/// the original query. The dense retrieval leg embeds the original `Primary`
/// (no synonym averaging in Phase 1); the BM25 leg can OR-in the canonical
/// synonyms via the GlossaryHits list when present.
///
/// Loaded once at boot via LoadFromFile. Hot-reload is not supported in
/// Phase 1 — restart to pick up glossary edits.
/// </summary>
public sealed class QueryExpander
{
    private readonly Dictionary<string, string> _aliasToCanonical;

    public QueryExpander(IEnumerable<GlossaryEntry> entries)
    {
        _aliasToCanonical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            foreach (var alias in entry.Aliases)
            {
                if (string.IsNullOrWhiteSpace(alias)) continue;
                _aliasToCanonical[alias.Trim()] = entry.Canonical;
            }
        }
    }

    public static QueryExpander LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        var entries = JsonSerializer.Deserialize<GlossaryEntry[]>(json)
            ?? Array.Empty<GlossaryEntry>();
        return new QueryExpander(entries);
    }

    public ExpandedQuery Expand(string query)
    {
        var primary = (query ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(primary))
            return new ExpandedQuery(string.Empty, Array.Empty<string>(), Array.Empty<string>());

        var synonyms = new HashSet<string>(StringComparer.Ordinal);
        var hits = new List<string>();
        foreach (var (alias, canonical) in _aliasToCanonical)
        {
            if (primary.IndexOf(alias, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (synonyms.Add(canonical))
                    hits.Add($"{alias} → {canonical}");
            }
        }
        return new ExpandedQuery(primary, synonyms.ToArray(), hits);
    }
}
