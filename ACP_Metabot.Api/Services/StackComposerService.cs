using System.Text;
using System.Text.Json;
using ACP_Metabot.Api.Models;

namespace ACP_Metabot.Api.Services;

public class StackComposerService
{
    private readonly SearchService _search;
    private readonly IClaudeClient _claude;
    private readonly ILogger<StackComposerService> _logger;

    private const int CandidatePoolSize = 20;
    private const int MaxClaudeOutputTokens = 1500;

    private const string SystemPrompt = @"You are an expert in the Virtuals Protocol Agent Commerce Protocol (ACP) marketplace.
Given a buyer's use case and a list of candidate offerings already shortlisted by semantic search,
your job is to pick the right SUBSET of offerings that compose into a workable agent stack, in the
right call order, and explain why.

Rules:
- Only pick offerings from the candidate list. Never invent offering names or agent names.
- Prefer cheaper offerings when two candidates are functionally equivalent.
- Keep the stack as small as possible — no more than the maxOfferings the buyer specified.
- If the candidates don't actually solve the use case, say so honestly in the rationale and return an empty stack.
- Output JSON ONLY in this exact shape (no prose outside JSON, no markdown fences):

{
  ""rationale"": ""one short paragraph explaining the chain and why each piece is included"",
  ""stack"": [
    { ""offeringName"": ""..."", ""agentName"": ""..."", ""agentAddress"": ""0x..."", ""priceUsdc"": 0.0, ""role"": ""one-sentence role in the stack"" }
  ]
}";

    public StackComposerService(SearchService search, IClaudeClient claude,
        ILogger<StackComposerService> logger)
    {
        _search = search;
        _claude = claude;
        _logger = logger;
    }

    public async Task<ComposedStack> ComposeAsync(
        string useCase, double? budgetUsdc, int maxOfferings, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(useCase))
        {
            return new ComposedStack(
                Rationale: "No use case provided.",
                Stack: Array.Empty<StackEntry>(),
                TotalPriceUsdc: 0);
        }

        var candidates = await _search.SearchAsync(useCase, CandidatePoolSize, 0.0, ct);
        if (candidates.Count == 0)
        {
            return new ComposedStack(
                Rationale: "No offerings indexed yet — has the indexer run?",
                Stack: Array.Empty<StackEntry>(),
                TotalPriceUsdc: 0);
        }

        var userPrompt = BuildUserPrompt(useCase, budgetUsdc, maxOfferings, candidates);
        var raw = await _claude.CompleteAsync(SystemPrompt, userPrompt, MaxClaudeOutputTokens, ct);

        var parsed = TryParse(raw, candidates, maxOfferings);
        if (parsed is null)
        {
            _logger.LogWarning("[composeStack] Claude returned unparseable output: {Raw}", raw);
            return new ComposedStack(
                Rationale: "Composer model returned an unparseable response. Please retry.",
                Stack: Array.Empty<StackEntry>(),
                TotalPriceUsdc: 0);
        }
        return parsed;
    }

    private static string BuildUserPrompt(string useCase, double? budgetUsdc,
        int maxOfferings, IReadOnlyList<OfferingMatch> candidates)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Use case: {useCase}");
        if (budgetUsdc is not null) sb.AppendLine($"Budget cap: {budgetUsdc:0.##} USDC per full stack run");
        sb.AppendLine($"Max offerings in stack: {maxOfferings}");
        sb.AppendLine();
        sb.AppendLine("Candidates (pre-ranked by semantic similarity, descending):");
        foreach (var c in candidates)
        {
            sb.AppendLine($"- offeringName: {c.OfferingName}");
            sb.AppendLine($"  agentName: {c.AgentName}");
            sb.AppendLine($"  agentAddress: {c.AgentAddress}");
            sb.AppendLine($"  priceUsdc: {c.PriceUsdc}");
            sb.AppendLine($"  description: {c.Description}");
            sb.AppendLine($"  similarity: {c.Score:0.000}");
        }
        return sb.ToString();
    }

    private static ComposedStack? TryParse(string raw, IReadOnlyList<OfferingMatch> candidates, int maxOfferings)
    {
        // Strip optional ```json fences just in case
        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```"))
        {
            var firstNl = cleaned.IndexOf('\n');
            if (firstNl > 0) cleaned = cleaned[(firstNl + 1)..];
            if (cleaned.EndsWith("```")) cleaned = cleaned[..^3];
            cleaned = cleaned.Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var rationale = doc.RootElement.TryGetProperty("rationale", out var r) ? (r.GetString() ?? "") : "";
            var entries = new List<StackEntry>();
            if (doc.RootElement.TryGetProperty("stack", out var stack) && stack.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in stack.EnumerateArray())
                {
                    var name = el.TryGetProperty("offeringName", out var n) ? n.GetString() ?? "" : "";
                    var address = el.TryGetProperty("agentAddress", out var a) ? a.GetString() ?? "" : "";

                    // Defence: require the offering+agent pair to actually be in candidates.
                    var match = candidates.FirstOrDefault(c =>
                        string.Equals(c.OfferingName, name, StringComparison.Ordinal) &&
                        string.Equals(c.AgentAddress, address, StringComparison.OrdinalIgnoreCase));
                    if (match is null) continue;

                    var role = el.TryGetProperty("role", out var rl) ? rl.GetString() ?? "" : "";
                    entries.Add(new StackEntry(
                        OfferingName: match.OfferingName,
                        AgentName: match.AgentName,
                        AgentAddress: match.AgentAddress,
                        PriceUsdc: match.PriceUsdc,
                        Role: role));

                    if (entries.Count >= maxOfferings) break;
                }
            }
            var total = entries.Sum(e => e.PriceUsdc);
            return new ComposedStack(rationale, entries, Math.Round(total, 4));
        }
        catch
        {
            return null;
        }
    }
}
