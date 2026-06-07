import { getOffering } from "./offerings/registry.js";
import type { OfferingContext } from "./offerings/types.js";

export type RouteResult =
  | { ok: true; result: unknown }
  | { ok: false; reason: string };

export async function route(
  offeringName: string,
  requirement: Record<string, unknown>,
  ctx: OfferingContext
): Promise<RouteResult> {
  const offering = getOffering(offeringName);
  if (!offering) {
    return { ok: false, reason: `unknown offering: ${offeringName}` };
  }
  const validation = offering.validate(requirement);
  if (!validation.valid) {
    return { ok: false, reason: validation.reason ?? "validation failed" };
  }
  if (!offering.execute) {
    return { ok: false, reason: `offering ${offeringName} has no execute() (subscription offering should be handled separately)` };
  }
  try {
    const result = await offering.execute(requirement, ctx);
    return { ok: true, result };
  } catch (err) {
    // P63: never forward upstream error text to the buyer — the internal API's
    // thrown Error embeds status + response body, which can carry RPC API keys
    // (P9) and internal route detail. Log server-side; return an opaque code.
    const detail = err instanceof Error ? err.message : String(err);
    console.error(`[router] offering "${offeringName}" execution failed: ${detail}`);
    return { ok: false, reason: "internal_error" };
  }
}
