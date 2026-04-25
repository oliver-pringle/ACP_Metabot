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
  try {
    const result = await offering.execute(requirement, ctx);
    return { ok: true, result };
  } catch (err) {
    return { ok: false, reason: err instanceof Error ? err.message : String(err) };
  }
}
