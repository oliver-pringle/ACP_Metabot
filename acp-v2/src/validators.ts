export interface ValidationResult {
  valid: boolean;
  reason?: string;
}

export function requireString(
  value: unknown,
  name: string,
  maxLen?: number
): ValidationResult {
  if (typeof value !== "string" || value.trim() === "") {
    return { valid: false, reason: `${name} is required` };
  }
  if (maxLen !== undefined && value.length > maxLen) {
    return {
      valid: false,
      reason: `${name} must be at most ${maxLen} characters (got ${value.length})`,
    };
  }
  return { valid: true };
}

export function requireOneOf(
  value: unknown,
  name: string,
  allowed: readonly string[]
): ValidationResult {
  if (value === undefined || value === null) return { valid: true };
  if (typeof value !== "string" || !allowed.includes(value)) {
    return { valid: false, reason: `${name} must be one of: ${allowed.join(", ")}` };
  }
  return { valid: true };
}

export function requirePositiveIntOrNothing(
  value: unknown,
  name: string,
  max: number
): ValidationResult {
  if (value === undefined || value === null) return { valid: true };
  if (typeof value !== "number" || !Number.isInteger(value) || value <= 0 || value > max) {
    return { valid: false, reason: `${name} must be a positive integer <= ${max}` };
  }
  return { valid: true };
}

export function requirePositiveNumberOrNothing(
  value: unknown,
  name: string
): ValidationResult {
  if (value === undefined || value === null) return { valid: true };
  if (typeof value !== "number" || value <= 0) {
    return { valid: false, reason: `${name} must be a positive number` };
  }
  return { valid: true };
}
