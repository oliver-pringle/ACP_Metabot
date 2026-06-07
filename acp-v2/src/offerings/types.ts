import type { ValidationResult } from "../validators.js";
import type { ApiClient } from "../apiClient.js";
import type { JobSession, AcpAgent } from "@virtuals-protocol/acp-node-v2";

export interface OfferingContext {
  client: ApiClient;
  session: JobSession;
  // ACPPurchaser Path A: purchase_quote resolves the live downstream price via
  // the agent (C# can't speak ACP). Other offerings ignore it.
  agent: AcpAgent;
}

/// Marketplace tier  -  what app.virtuals.io's "Add Job - Subscription Tiers"
/// form takes. Each subscription offering MUST declare >=1 tier; multiple
/// tiers let buyers pick a duration/commitment (weekly/monthly/quarterly).
/// Marketplace UI restricts duration to {7, 15, 30, 90} days.
export interface SubscriptionTier {
  /// Tier name, <=20 chars, snake_case (e.g. "monthly", "30d_watch").
  name: string;
  /// Flat USD price for the entire tier duration (NOT per-tick).
  priceUsd: number;
  /// Tier duration in days; must be one of 7, 15, 30, or 90.
  durationDays: 7 | 15 | 30 | 90;
}

export interface SubscriptionConfig {
  // Internal billing fields  -  used by the bot's worker loop / per-tick HMAC
  // delivery. NOT shown on the marketplace; the marketplace shows `tiers`.
  pricePerTickUsdc: number;
  minIntervalSeconds: number;
  maxTicks: number;
  maxDurationDays: number;

  /// Marketplace registration tiers. At least one. Buyer picks one at hire time.
  /// Required since 2026-05-10.
  tiers: SubscriptionTier[];
}

export interface Offering {
  name: string;
  description: string;
  requirementSchema: Record<string, unknown>;
  // Required since 2026-05-10: realistic example request for marketplace docs / wire-shape validation.
  requirementExample: unknown;
  // Required since 2026-05-04: deliverable contract + realistic example for marketplace docs / wire-shape validation.
  deliverableSchema: Record<string, unknown>;
  deliverableExample: unknown;
  // Required since 2026-05-10: estimated max time in minutes from hire to deliverable (min 5).
  slaMinutes: number;
  validate(req: Record<string, unknown>): ValidationResult;

  // Exactly one of the following two MUST be set.
  execute?(req: Record<string, unknown>, ctx: OfferingContext): Promise<unknown>;
  subscription?: SubscriptionConfig;
}
