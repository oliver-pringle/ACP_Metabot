import type { ValidationResult } from "../validators.js";
import type { ApiClient } from "../apiClient.js";
import type { JobSession } from "@virtuals-protocol/acp-node-v2";

export interface OfferingContext {
  client: ApiClient;
  session: JobSession;
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
  execute(req: Record<string, unknown>, ctx: OfferingContext): Promise<unknown>;
}
