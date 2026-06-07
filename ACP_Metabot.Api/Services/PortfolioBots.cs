// R12 Tier 1.2 — Portfolio bot catalogue.
//
// Static, hand-curated metadata for every bot Oliver runs. Consumed by
// PortfolioRollupService to build the /v1/resources/portfolioRollup envelope.
//
// Why static + hardcoded:
//   * The 15-bot list is stable (portfolio is considered complete per CLAUDE.md);
//     edits are once-per-quarter at most.
//   * Sibling bots are reachable from this container only through the acp-shared
//     docker bridge, which doesn't exist on a dev laptop. Hardcoding the
//     counts keeps `dotnet run` smoke-testable without standing up every bot.
//   * Buyer orchestrators that hit portfolioRollup care about the SHAPE of the
//     portfolio, not 1-minute-stale per-bot counts. Stale-by-the-day is fine.
//
// Per-bot counts come from each bot's `npm run print-offerings` / print-resources
// totals on the day this file was last touched. When a bot ships a new
// offering, bump its OfferingCount + SubscriptionTierCount here and let the
// 5-minute Service cache flush carry the change.
//
// Sources of truth (per CLAUDE.md bot inventory table + memory/project_acp_*.md):
//   - Wallet 1 (5/5):  TheMetaBot, DeFiEval, AgentEval, LiquidGuard, MEVProtect
//   - Wallet 2 (5/5):  ChainlinkBot, ArenaBot, RevokeBot, EASIssuer, OracleBot
//   - Wallet 3 (5/100): WitnessBot, SolanaBot, ButlerBridgeBot, SecurityBot, ConciergeBot

namespace ACP_Metabot.Api.Services;

public sealed record PortfolioBot(
    string Slug,
    string DisplayName,
    string AgentAddress,
    string? AgentId,
    string[] Chains,
    string Category,
    int OfferingCount,
    int ResourceCount,
    int SubscriptionTierCount);

public static class PortfolioBots
{
    public static readonly PortfolioBot[] All = new[]
    {
        // ── Wallet 1 ──────────────────────────────────────────────────────────
        new PortfolioBot(
            Slug: "metabot",
            DisplayName: "TheMetaBot",
            AgentAddress: "0xecf9773b50f01f3a97b087a6ecdf12a71afc558c",
            AgentId: null,
            Chains: new[] { "base" },
            Category: "marketplace-indexer",
            OfferingCount: 19,
            ResourceCount: 16,
            SubscriptionTierCount: 2),

        new PortfolioBot(
            Slug: "defieval",
            DisplayName: "TheDeFiEval",
            AgentAddress: "0x997163304142c3a3ff660ad03069b7d78485ca95",
            AgentId: null,
            Chains: new[] { "base" },
            Category: "agent-evaluation",
            OfferingCount: 3,
            ResourceCount: 2,
            SubscriptionTierCount: 0),

        new PortfolioBot(
            Slug: "agenteval",
            DisplayName: "TheAgentEval",
            AgentAddress: "0xb97552998e7ee94ef2a260fdc25529ed93e4902b",
            AgentId: null,
            Chains: new[] { "base" },
            Category: "agent-evaluation",
            OfferingCount: 6,
            ResourceCount: 2,
            SubscriptionTierCount: 0),

        new PortfolioBot(
            Slug: "liquidguard",
            DisplayName: "LiquidGuard",
            AgentAddress: "0x18362cdc11247ee9e37dea29a1cf21f378ec619f",
            AgentId: null,
            Chains: new[] { "base", "ethereum" },
            Category: "defi-risk",
            OfferingCount: 14,
            ResourceCount: 3,
            SubscriptionTierCount: 2),

        new PortfolioBot(
            Slug: "mevprotect",
            DisplayName: "MEVProtect",
            AgentAddress: "0x827b2c1de0922314f62bc19554044fd649291ca3",
            AgentId: null,
            Chains: new[] { "ethereum" },
            Category: "mev-protection",
            OfferingCount: 7,
            ResourceCount: 2,
            SubscriptionTierCount: 1),

        // ── Wallet 2 ──────────────────────────────────────────────────────────
        new PortfolioBot(
            Slug: "chainlinkbot",
            DisplayName: "TheChainlinkBot",
            AgentAddress: "0x6f28f51743b912197caeadbc3113c955bb80e738",
            AgentId: null,
            Chains: new[] { "base", "ethereum" },
            Category: "chainlink-primitives",
            OfferingCount: 28,
            ResourceCount: 6,
            SubscriptionTierCount: 2),

        new PortfolioBot(
            Slug: "arenabot",
            DisplayName: "TheArenaBot",
            AgentAddress: "0xa524de81819e213e8bb181fa0b3747a4a6c3a7e3",
            AgentId: null,
            Chains: new[] { "base" },
            Category: "arena-indexer",
            OfferingCount: 7,
            ResourceCount: 4,
            SubscriptionTierCount: 1),

        new PortfolioBot(
            Slug: "revokebot",
            DisplayName: "TheRevokeBot",
            AgentAddress: "0xbd9527bdbd61640f544bddd513ed9fcaf9387df8",
            AgentId: null,
            Chains: new[] { "base", "ethereum" },
            Category: "wallet-security",
            OfferingCount: 9,
            ResourceCount: 3,
            SubscriptionTierCount: 1),

        new PortfolioBot(
            Slug: "easissuer",
            DisplayName: "EASIssuerBot",
            AgentAddress: "0xe9b0f88f8f27a7033f4f9679e93ebcfe1a78f7fd",
            AgentId: null,
            Chains: new[] { "base" },
            Category: "attestations",
            OfferingCount: 9,
            ResourceCount: 3,
            SubscriptionTierCount: 1),

        new PortfolioBot(
            Slug: "oraclebot",
            DisplayName: "TheOracleBot",
            AgentAddress: "0x935e97046b10832664d007430c7b7fd310a6236e",
            AgentId: null,
            Chains: new[] { "base", "ethereum" },
            Category: "oracle-deviation",
            OfferingCount: 8,
            ResourceCount: 3,
            SubscriptionTierCount: 1),

        // ── Wallet 3 ──────────────────────────────────────────────────────────
        new PortfolioBot(
            Slug: "witnessbot",
            DisplayName: "TheWitnessBot",
            AgentAddress: "0xc834e81ebe0921fdf9458ac422861df441a6caf9",
            AgentId: null,
            Chains: new[] { "base" },
            Category: "catalogue-provenance",
            OfferingCount: 8,
            ResourceCount: 4,
            SubscriptionTierCount: 1),

        new PortfolioBot(
            Slug: "solanabot",
            DisplayName: "TheSolanaBot",
            AgentAddress: "0x34235a877ee2da8dc9649d46af6f7463bc2206c2",
            AgentId: null,
            Chains: new[] { "solana" },
            Category: "solana-primitives",
            OfferingCount: 5,
            ResourceCount: 2,
            SubscriptionTierCount: 0),

        new PortfolioBot(
            Slug: "butlerbridge",
            DisplayName: "TheButlerBridgeBot",
            AgentAddress: "0xbbd08418d78c0fd4b26117c15221c4cee015f492",
            AgentId: null,
            Chains: new[] { "base" },
            Category: "payment-bridge",
            OfferingCount: 3,
            ResourceCount: 2,
            SubscriptionTierCount: 0),

        new PortfolioBot(
            Slug: "securitybot",
            DisplayName: "TheSecurityBot",
            AgentAddress: "0xa42b7122126245858c3cb0dcd0e4c151f3ea48d5",
            AgentId: "019e7852-a08b-7f65-9ee4-20444e03e5e4",
            Chains: new[] { "base" },
            Category: "agent-security",
            OfferingCount: 2,
            ResourceCount: 2,
            SubscriptionTierCount: 1),

        // R18 (2026-06-07): 15th bot, was missing from the rollup (totalBots showed 14).
        new PortfolioBot(
            Slug: "conciergebot",
            DisplayName: "TheConciergeBot",
            AgentAddress: "0xe7068d66905adb9d266d0dc0612d3b3658242b61",
            AgentId: "019e8a23-e185-73cf-acf4-7aff3e0fca84",
            Chains: new[] { "base" },
            Category: "concierge-router",
            OfferingCount: 2,            // route_stack + portfolio_run (live count overrides)
            ResourceCount: 0,            // echoStatus deleted R18; live count overrides once re-indexed
            SubscriptionTierCount: 0)
    };

    // Cross-bot edges manually curated. Each edge captures "producer's data
    // flows into consumer via this offering / interface". Used to expose the
    // portfolio's integration graph to orchestrators that want to ladder
    // multi-bot stacks. `verified=true` means the wiring is live on prod
    // today; `verified=false` is documented intent.
    //
    // Source: per-bot project memories under memory/project_acp_*.md plus
    // CLAUDE.md "Cross-bot pipeline" notes.
    public static readonly CrossBotEdge[] Edges = new[]
    {
        // OracleBot is the canonical producer for verified-price reads
        new CrossBotEdge("oraclebot",  "mevprotect",   "mev_protect_oracle",     true),
        new CrossBotEdge("oraclebot",  "mevprotect",   "mev_check_oracle",       true),
        new CrossBotEdge("oraclebot",  "chainlinkbot", "price_feed_verified",    true),
        new CrossBotEdge("oraclebot",  "chainlinkbot", "peg_status_verified",    true),
        new CrossBotEdge("oraclebot",  "liquidguard",  "hf_check_oracle",        true),
        new CrossBotEdge("oraclebot",  "liquidguard",  "position_watch",         true),

        // Metabot's live reputation feeds DeFiEval's deep-eval tier
        new CrossBotEdge("metabot",    "defieval",     "agentReputation",        true),
        new CrossBotEdge("metabot",    "chainlinkbot", "agentReputation",        true),

        // ChainlinkBot publishes Metabot reputation as AggregatorV3 feeds
        new CrossBotEdge("metabot",    "chainlinkbot", "feed-address publish",   true),

        // ArenaBot supplies cross-section data via Metabot's ArenaSourceWorker
        new CrossBotEdge("arenabot",   "metabot",      "ArenaSourceWorker",      true),

        // EASIssuer publishes attestations for cross-bot results
        new CrossBotEdge("easissuer",  "oraclebot",    "oracle_attest",          true),
        new CrossBotEdge("easissuer",  "revokebot",    "revoke_with_attestation", false),
        new CrossBotEdge("easissuer",  "agenteval",    "attest_result",          true),
        new CrossBotEdge("easissuer",  "defieval",     "attest_result",          true),

        // WitnessBot signs every portfolio bot's catalogue (via witnessedCatalogue)
        new CrossBotEdge("witnessbot", "metabot",      "manifest_sign",          true),
        new CrossBotEdge("witnessbot", "oraclebot",    "manifest_sign",          true),

        // Metabot orchestrates the 4-peer risk fan-out
        new CrossBotEdge("liquidguard", "metabot",     "riskSnapshot fan-out",   true),
        new CrossBotEdge("revokebot",   "metabot",     "riskSnapshot fan-out",   true),
        new CrossBotEdge("mevprotect",  "metabot",     "riskSnapshot fan-out",   true)
    };
}

public sealed record CrossBotEdge(
    string Producer,
    string Consumer,
    string Via,
    bool Verified);
