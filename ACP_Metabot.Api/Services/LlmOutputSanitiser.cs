using System.Text;
using System.Text.RegularExpressions;

namespace ACP_Metabot.Api.Services;

/// P45 (audit 2026-06-08): EGRESS sanitiser for LLM-generated free text
/// (search narrative summary, per-result reasoning) that this bot returns to a
/// PAYING buyer and, via the marketplace, to downstream AGENTS. The input side
/// is guarded by StackComposerService.SanitizeForPrompt / SearchNarrator's
/// prompt wrap (P12 prompt-injection wrap); but prompt-injection defence is
/// imperfect by design, so the model's OUTPUT is still attacker-influenceable.
/// A downstream agent consuming this deliverable could treat an embedded link
/// or instruction as actionable. This neutralises the egress:
///   - strip ASCII/C1 control characters (keep tab/newline/carriage-return) so
///     no ANSI/escape smuggling,
///   - collapse markdown links [text](url) -> text and replace bare http(s) URLs
///     with [link removed] (the narrator prompt asks for plain prose anyway),
///   - hard length cap as defence-in-depth above the maxTokens bound.
/// Distinct from P30/P11 (which keeps upstream error BODIES out of LOGS); this
/// targets the buyer-facing WIRE. Lifted from ACP_DeFiEval LlmOutputSanitiser.
public static class LlmOutputSanitiser
{
    // [text](url) -> text. Balanced bracket+paren shape.
    private static readonly Regex MarkdownLink = new(
        @"\[([^\]]*)\]\([^)]*\)", RegexOptions.Compiled);
    private static readonly Regex BareUrl = new(
        @"https?://\S+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// Returns the sanitised text. Null/empty pass through unchanged so callers
    /// can keep their existing null-handling. maxChars &lt;= 0 disables the cap.
    public static string? Sanitise(string? raw, int maxChars)
    {
        if (string.IsNullOrEmpty(raw)) return raw;

        // Strip control characters (char.IsControl covers C0 0x00-0x1F + DEL +
        // C1 0x80-0x9F) but KEEP the three whitespace controls so prose layout
        // survives. Done in a loop to avoid escape-sequence regex literals.
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (char.IsControl(ch) && ch != '\t' && ch != '\n' && ch != '\r') continue;
            sb.Append(ch);
        }
        var s = sb.ToString();

        // Order matters: collapse markdown links FIRST (removes the (url) part),
        // then any remaining naked URL becomes [link removed].
        s = MarkdownLink.Replace(s, "$1");
        s = BareUrl.Replace(s, "[link removed]");

        if (maxChars > 0 && s.Length > maxChars)
            s = s.Substring(0, maxChars) + " [truncated]";
        return s;
    }
}
