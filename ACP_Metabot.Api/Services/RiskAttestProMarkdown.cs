// riskAttestPro v1.0 Task 4 — deterministic markdown audit-report generator.
//
// Pure-static helper consumed by RiskAttestProService (Task 6) to render the
// buyer-facing `markdown_report` field of the $10 deliverable. No DI, no DB,
// no I/O — same inputs always produce identical output (snapshot tests in
// downstream callers rely on this).
//
// Render contract — sections appear in this order, exactly:
//   1. H1 title with lowercased wallet
//   2. Chain + Verdict + Grade + Score header line
//   3. Optional "## Executive summary" (omitted entirely if summary is
//      null/empty/whitespace)
//   4. Seven per-signal sections in fixed order:
//      Health factor → Approvals → MEV exposure → Reputation →
//      Arena overlap → Witness manifest → 30-day trajectory
//
// Per-component rendering rules:
//   * Component absent from JSON  →  "_(component absent from this report)_"
//   * Component status=="unavailable" →
//        "⚠ Source unavailable (<source>): <details>"
//     (single warning line, no score/status bullets — compliance readers see
//      graceful degradation rather than a misleading 50/100 placeholder)
//   * Otherwise → bulleted score/source/status (and details when non-empty)

using System.Text;
using System.Text.Json;

namespace ACP_Metabot.Api.Services;

public static class RiskAttestProMarkdown
{
    public static string Generate(
        string wallet,
        string chain,
        int scorePro,
        string verdict,
        string grade,
        JsonElement components,
        string executiveSummary)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# riskAttestPro report — {wallet.ToLowerInvariant()}");
        sb.AppendLine();
        sb.AppendLine($"**Chain:** {chain}");
        sb.AppendLine($"**Verdict:** {verdict} (grade {grade}, score {scorePro}/100)");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(executiveSummary))
        {
            sb.AppendLine("## Executive summary");
            sb.AppendLine(executiveSummary);
            sb.AppendLine();
        }

        AppendSection(sb, components, "healthFactor", "## Health factor");
        AppendSection(sb, components, "approvals", "## Approvals");
        AppendSection(sb, components, "mev", "## MEV exposure");
        AppendSection(sb, components, "reputation", "## Reputation");
        AppendSection(sb, components, "arena", "## Arena overlap");
        AppendSection(sb, components, "witness", "## Witness manifest");
        AppendSection(sb, components, "trajectory", "## 30-day trajectory");

        return sb.ToString();
    }

    private static void AppendSection(StringBuilder sb, JsonElement components, string key, string header)
    {
        sb.AppendLine(header);

        if (components.ValueKind != JsonValueKind.Object || !components.TryGetProperty(key, out var c))
        {
            sb.AppendLine("_(component absent from this report)_");
            sb.AppendLine();
            return;
        }

        var status = c.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String
            ? (st.GetString() ?? "")
            : "";
        var source = c.TryGetProperty("source", out var sr) && sr.ValueKind == JsonValueKind.String
            ? (sr.GetString() ?? "")
            : "";
        var score = c.TryGetProperty("score", out var sc) && sc.ValueKind == JsonValueKind.Number
            ? sc.GetInt32()
            : 0;
        var details = c.TryGetProperty("details", out var dt) && dt.ValueKind == JsonValueKind.String
            ? (dt.GetString() ?? "")
            : "";

        if (status == "unavailable")
        {
            sb.AppendLine($"⚠ Source unavailable ({source}): {details}");
        }
        else
        {
            sb.AppendLine($"- Score: {score}/100");
            sb.AppendLine($"- Source: {source}");
            sb.AppendLine($"- Status: {status}");
            if (!string.IsNullOrWhiteSpace(details))
            {
                sb.AppendLine($"- Detail: {details}");
            }
        }

        sb.AppendLine();
    }
}
