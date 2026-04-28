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
  deliverableSchema?: Record<string, unknown>;
  validate(req: Record<string, unknown>): ValidationResult;
  execute(req: Record<string, unknown>, ctx: OfferingContext): Promise<unknown>;
}
