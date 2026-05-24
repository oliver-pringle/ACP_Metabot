using System.Text.Json;

namespace ACP_Metabot.Api.Services;

/// <summary>
/// v1.10 Phase 2: extract top-level field names from a requirement or
/// deliverable schema. Schemas in the V2 marketplace arrive as either a
/// JSON object OR a JSON-encoded string (string is legacy V1 shape that
/// some V2 registrations still emit); both forms are supported.
/// </summary>
public static class SchemaFacetExtractor
{
    private const int MaxFieldNamesPerSchema = 30;

    public static IReadOnlyList<string> Extract(string? schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson)) return Array.Empty<string>();
        try
        {
            using var doc = JsonDocument.Parse(schemaJson);
            return ExtractFromElement(doc.RootElement);
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    public static IReadOnlyList<string> ExtractFromElement(JsonElement el)
    {
        // Unwrap one level of "string containing JSON" if necessary
        if (el.ValueKind == JsonValueKind.String)
        {
            var inner = el.GetString();
            if (string.IsNullOrWhiteSpace(inner)) return Array.Empty<string>();
            try
            {
                using var doc = JsonDocument.Parse(inner);
                return ExtractFromElement(doc.RootElement);
            }
            catch (JsonException)
            {
                return Array.Empty<string>();
            }
        }
        if (el.ValueKind != JsonValueKind.Object) return Array.Empty<string>();
        if (!el.TryGetProperty("properties", out var props)
            || props.ValueKind != JsonValueKind.Object)
            return Array.Empty<string>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>();
        foreach (var p in props.EnumerateObject())
        {
            var name = p.Name?.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(name)) continue;
            if (seen.Add(name))
            {
                list.Add(name);
                if (list.Count >= MaxFieldNamesPerSchema) break;
            }
        }
        return list;
    }
}
