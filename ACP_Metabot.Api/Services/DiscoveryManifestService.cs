using System.Text;
using System.Text.Json;

namespace ACP_Metabot.Api.Services;

// R18 (2026-06-07) — agentic-commerce discovery manifest.
//
// Renders GET /.well-known/agentic-commerce.json + GET /llms.txt as a read-only
// PROJECTION of the live, 5-min-cached portfolioRollup. Driving both off the
// rollup means the manifest never goes stale and never duplicates the bot
// catalogue (single source of truth = PortfolioBots + the indexer). Matches the
// Strumly / Johnny-Suede discovery pattern the serious ACP vendor cohort ships.
//
// WHY a JSON projection rather than typed access: PortfolioRollupService.
// GetRollupAsync() returns an anonymous object (its public JSON contract). To
// avoid changing that live contract we serialize it to a JsonElement and read
// the documented fields — zero blast radius on the existing /v1/resources/
// portfolioRollup surface.
//
// x402 is DELIBERATELY ABSENT: ButlerBridge still boots stub-x402 (trusts the
// caller's BuyerAddress, 0 active buyers), so advertising an x402 rail would
// point crawlers at a trust-the-caller broadcaster. capabilities.payment_rails
// advertises only "virtuals-acp" until the ButlerBridge cutover lands; ship
// /.well-known/x402.json then.
public sealed class DiscoveryManifestService
{
    private readonly PortfolioRollupService _rollup;

    // Base USDC on Base mainnet (chain id 8453) — the settlement asset for every
    // paid ACP call across the portfolio.
    private const string UsdcBase = "0x833589fCD6eDb6E08f4c7C32D4f71b54bdA02913";
    private const string Gateway = "https://api.acp-metabot.dev";
    private const string Site = "https://acp-metabot.dev";

    public DiscoveryManifestService(PortfolioRollupService rollup) => _rollup = rollup;

    private async Task<JsonElement> RollupElementAsync(CancellationToken ct)
    {
        // SerializeToElement on the anonymous rollup object. The rollup's
        // property names are literal camelCase ("slug", "displayName", …) so a
        // default-options serialize preserves them regardless of any global
        // naming policy.
        var obj = await _rollup.GetRollupAsync(ct);
        return JsonSerializer.SerializeToElement(obj);
    }

    public async Task<object> BuildAgenticCommerceAsync(CancellationToken ct = default)
    {
        var root = await RollupElementAsync(ct);
        var portfolio = root.GetProperty("portfolio");
        var totalBots = portfolio.GetProperty("totalBots").GetInt32();

        var agents = new List<object>();
        foreach (var b in root.GetProperty("bots").EnumerateArray())
        {
            var agentIdProp = b.GetProperty("agentId");
            agents.Add(new
            {
                slug = b.GetProperty("slug").GetString(),
                name = b.GetProperty("displayName").GetString(),
                category = b.GetProperty("category").GetString(),
                chains = b.GetProperty("chains").EnumerateArray()
                    .Select(c => c.GetString()).Where(s => s is not null).ToArray(),
                agentAddress = b.GetProperty("agentAddress").GetString(),
                agentId = agentIdProp.ValueKind == JsonValueKind.Null ? null : agentIdProp.GetString(),
                marketplaceUrl = b.GetProperty("marketplaceUrl").GetString(),
                website = b.GetProperty("website").GetString(),
                resourcesBaseUrl = b.GetProperty("resourcesBaseUrl").GetString(),
                offeringCount = b.GetProperty("offeringCount").GetInt32(),
                resourceCount = b.GetProperty("resourceCount").GetInt32(),
                subscriptionTierCount = b.GetProperty("subscriptionTierCount").GetInt32()
            });
        }

        return new
        {
            version = "1",
            protocol = "agentic-commerce",
            seller = new
            {
                id = "acp-metabot-portfolio",
                name = "ACP MetaBot Portfolio",
                tagline = $"{totalBots} production Virtuals Protocol ACP agents for DeFi risk, oracle, MEV, attestation, security & wallet intelligence.",
                description = "A curated fleet of live Virtuals Protocol ACP v2 service agents on Base: "
                    + "health-factor & liquidation defense (LiquidGuard), cross-source oracle deviation "
                    + "(TheOracleBot), private-mempool MEV protection (MEVProtect), EAS attestations "
                    + "(EASIssuer), Chainlink primitives & reputation feeds (TheChainlinkBot), passive "
                    + "security auditing (TheSecurityBot), wallet-approval revocation (TheRevokeBot), agent "
                    + "evaluation (DeFiEval/AgentEval), provenance (TheWitnessBot), and a marketplace "
                    + "indexer/router (TheMetaBot). Every paid call settles in USDC on Base via the "
                    + "Virtuals ACP escrow flow.",
                url = Site,
                x_handle = "@TheMetaBot_ACP"
            },
            capabilities = new
            {
                // x402 withheld until the ButlerBridge cutover (see class header).
                payment_rails = new[] { "virtuals-acp" },
                currencies_crypto = new[] { "USDC" },
                networks = new[]
                {
                    new { id = "eip155:8453", name = "Base", asset = "USDC", token_address = UsdcBase }
                },
                free_tier = new
                {
                    resources = "Every bot exposes free, parameterized /v1/resources/* introspection endpoints — call before you pay."
                },
                agents_accepted = true,
                humans_accepted = true
            },
            catalog = new
            {
                // The never-stale source this manifest projects from.
                index_url = $"{Gateway}/v1/resources/portfolioRollup",
                search_url = $"{Gateway}/v1/search",
                agent_count = totalBots,
                paid_offering_count = portfolio.GetProperty("totalPaidOfferings").GetInt32(),
                free_resource_count = portfolio.GetProperty("totalFreeResources").GetInt32(),
                subscription_count = portfolio.GetProperty("totalSubscriptions").GetInt32()
            },
            agents,
            endpoints = new
            {
                catalog = $"{Gateway}/v1/resources/portfolioRollup",
                llms_txt = $"{Gateway}/llms.txt",
                search = $"{Gateway}/v1/search"
            },
            updated_at = root.GetProperty("asOfUtc").GetString()
        };
    }

    public async Task<string> BuildLlmsTxtAsync(CancellationToken ct = default)
    {
        var root = await RollupElementAsync(ct);
        var p = root.GetProperty("portfolio");
        int totalBots = p.GetProperty("totalBots").GetInt32();
        int totalOff = p.GetProperty("totalPaidOfferings").GetInt32();
        int totalRes = p.GetProperty("totalFreeResources").GetInt32();
        int totalSub = p.GetProperty("totalSubscriptions").GetInt32();

        var sb = new StringBuilder();
        sb.Append("# ACP MetaBot Portfolio — ").Append(totalBots).Append(" production ACP agents on Base\n\n");
        sb.Append("> A curated fleet of ").Append(totalBots)
          .Append(" live Virtuals Protocol ACP v2 service agents, all settling in USDC on Base. ")
          .Append(totalOff).Append(" paid offerings + ").Append(totalRes)
          .Append(" free introspection Resources + ").Append(totalSub)
          .Append(" subscription tiers. Discover, compare, and hire via the Virtuals marketplace or the public gateway at api.acp-metabot.dev. Machine-readable catalog: /v1/resources/portfolioRollup (auto-refreshed every 5 min).\n\n");

        sb.Append("## Discovery\n");
        sb.Append("- [Portfolio rollup (JSON)](").Append(Gateway).Append("/v1/resources/portfolioRollup): every agent's address, category, offering/resource counts, reputation, and cross-bot edges — one call instead of probing every agent.\n");
        sb.Append("- [Agentic-commerce manifest](").Append(Gateway).Append("/.well-known/agentic-commerce.json): canonical fleet discovery (USDC/Base, Virtuals ACP rail).\n");
        sb.Append("- [Semantic search](").Append(Gateway).Append("/v1/search): natural-language search across the marketplace (V1+V2), run by TheMetaBot.\n\n");

        sb.Append("## Agents (hire on app.virtuals.io; free Resources are public)\n");
        foreach (var b in root.GetProperty("bots").EnumerateArray())
        {
            var name = b.GetProperty("displayName").GetString();
            var cat = b.GetProperty("category").GetString();
            int off = b.GetProperty("offeringCount").GetInt32();
            int res = b.GetProperty("resourceCount").GetInt32();
            var url = b.GetProperty("marketplaceUrl").GetString();
            sb.Append("- **").Append(name).Append("** (").Append(cat).Append(") — ")
              .Append(off).Append(" paid offerings, ").Append(res).Append(" free Resources. ")
              .Append(url).Append('\n');
        }
        sb.Append('\n');

        sb.Append("## Payment\n");
        sb.Append("- USDC on Base (chain id 8453) via the Virtuals Protocol ACP escrow flow.\n");
        sb.Append("- Free, parameterized /v1/resources/* introspection on every bot — call before you pay.\n\n");

        sb.Append("## Notes\n");
        sb.Append("- Read-only / informational outputs; no custody, no execution, no financial advice.\n");
        sb.Append("- Canonical identity: each agent's Base wallet address (see the rollup).\n");

        return sb.ToString();
    }
}
