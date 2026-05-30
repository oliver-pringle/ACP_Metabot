// Walks every offering's requirementSchema + deliverableSchema (and nested
// items.properties when items are objects, and oneOf/anyOf/allOf branches) to
// surface any property that lacks a `description` string. Implements portfolio
// convention P32 (Marketplace operator dashboard flags missing descriptions
// with a yellow warning; Butler Pro Mode routing weights schema completeness).
//
// Two usages:
//   - CLI: `tsx scripts/check-property-descriptions.ts` (exits 1 if any miss)
//   - Library: `findMissingDescriptions(OFFERINGS)` returns the issue list,
//     called from `print-offerings-for-registration.ts` as a pre-flight gate.
import { OFFERINGS } from "../src/offerings/registry.js";
import type { Offering } from "../src/offerings/types.js";

export interface DescriptionIssue {
  offering: string;
  offeringKey: string;
  schemaName: "requirementSchema" | "deliverableSchema";
  path: string;
}

function isNonEmptyString(v: unknown): boolean {
  return typeof v === "string" && v.trim().length > 0;
}

function walk(
  node: any,
  path: string,
  schemaName: DescriptionIssue["schemaName"],
  offering: string,
  offeringKey: string,
  issues: DescriptionIssue[]
): void {
  if (!node || typeof node !== "object") return;

  // properties: each child needs description; recurse regardless of type
  if (node.properties && typeof node.properties === "object") {
    for (const [key, val] of Object.entries(node.properties)) {
      const v = val as any;
      const childPath = path ? `${path}.${key}` : key;
      if (!isNonEmptyString(v?.description)) {
        issues.push({ offering, offeringKey, schemaName, path: childPath });
      }
      walk(v, childPath, schemaName, offering, offeringKey, issues);
    }
  }

  // array of objects — recurse with [*] segment
  if (node.items && typeof node.items === "object") {
    walk(node.items, `${path}[*]`, schemaName, offering, offeringKey, issues);
  }

  // composition keywords
  for (const kw of ["oneOf", "anyOf", "allOf"] as const) {
    const arr = (node as any)[kw];
    if (Array.isArray(arr)) {
      for (let i = 0; i < arr.length; i++) {
        walk(arr[i], `${path}[${kw}#${i}]`, schemaName, offering, offeringKey, issues);
      }
    }
  }
}

// Library entry point — used by print-offerings as a pre-flight gate.
export function findMissingDescriptions(
  offerings: Record<string, Offering>
): DescriptionIssue[] {
  const issues: DescriptionIssue[] = [];
  for (const [offeringKey, offering] of Object.entries(offerings)) {
    walk(offering.requirementSchema, "", "requirementSchema", offering.name, offeringKey, issues);
    walk(offering.deliverableSchema, "", "deliverableSchema", offering.name, offeringKey, issues);
  }
  return issues;
}

// CLI entry point — only runs when invoked directly via tsx.
function isMainModule(): boolean {
  const entryArg = process.argv[1] ?? "";
  return entryArg.endsWith("check-property-descriptions.ts") || entryArg.endsWith("check-property-descriptions.js");
}

if (isMainModule()) {
  const issues = findMissingDescriptions(OFFERINGS);
  if (issues.length === 0) {
    const total = Object.keys(OFFERINGS).length;
    console.log(`ALL OK: every property in every schema has a description (${total} offering${total === 1 ? "" : "s"} scanned).`);
    process.exit(0);
  }
  const byOffering = new Map<string, DescriptionIssue[]>();
  for (const iss of issues) {
    const key = iss.offering;
    if (!byOffering.has(key)) byOffering.set(key, []);
    byOffering.get(key)!.push(iss);
  }
  const sortedNames = Array.from(byOffering.keys()).sort();
  for (const name of sortedNames) {
    const list = byOffering.get(name)!;
    console.log("");
    console.log(`${name} — ${list.length} missing description${list.length === 1 ? "" : "s"}`);
    for (const iss of list) {
      console.log(`  ${iss.schemaName}.${iss.path}`);
    }
  }
  console.log("");
  console.log("=".repeat(72));
  console.log(`TOTAL: ${issues.length} propert${issues.length === 1 ? "y" : "ies"} missing description across ${byOffering.size} offering${byOffering.size === 1 ? "" : "s"}.`);
  process.exit(1);
}
