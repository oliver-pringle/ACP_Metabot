import type { ValidationResult } from "../validators.js";
import type { ApiClient } from "../apiClient.js";

export interface OfferingContext {
  client: ApiClient;
}

export interface Offering {
  name: string;
  description: string;
  requirementSchema: Record<string, unknown>;
  validate(req: Record<string, unknown>): ValidationResult;
  execute(req: Record<string, unknown>, ctx: OfferingContext): Promise<unknown>;
}
