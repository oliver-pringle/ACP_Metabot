import { OFFERINGS } from "../src/offerings/registry.js";
import { priceFor } from "../src/pricing.js";
import { findMissingDescriptions } from "./check-property-descriptions.js";

// Marketplace pre-flight check: app.virtuals.io caps offering names at 20 chars.
// `npm run print-offerings` is the last gate before pasting blocks into the
// dashboard, so fail fast here rather than discover the cap at registration.
{
  const MAX_NAME_LEN = 20;
  const violations = Object.values(OFFERINGS)
    .filter(o => o.name.length > MAX_NAME_LEN)
    .map(o => ({ name: o.name, len: o.name.length, over: o.name.length - MAX_NAME_LEN }));
  if (violations.length > 0) {
    console.error(`ERROR: ${violations.length} offering name(s) exceed the ${MAX_NAME_LEN}-char marketplace cap:`);
    for (const v of violations) console.error(`  - ${v.name} (${v.len} chars, ${v.over} over)`);
    console.error("");
    console.error("app.virtuals.io rejects offering names > 20 chars at registration time.");
    console.error("Rename in acp-v2/src/offerings/*.ts (offering 'name' field + export const + file),");
    console.error("then update entries in registry.ts and pricing.ts, then rerun.");
    process.exit(1);
  }
}

// v1.7.4: app.virtuals.io ALSO caps offering descriptions at 500 chars. Same
// pattern as the Resource Description cap (separate memory note); marketplace
// rejects at registration time but doesn't surface the limit in the dashboard
// hint. Trip caught by the `today` description bloating during the newResources
// addition.
{
  const MAX_DESC_LEN = 500;
  const violations = Object.values(OFFERINGS)
    .filter(o => (o.description ?? "").length > MAX_DESC_LEN)
    .map(o => ({
      name: o.name,
      len: o.description.length,
      over: o.description.length - MAX_DESC_LEN
    }));
  if (violations.length > 0) {
    console.error(`ERROR: ${violations.length} offering description(s) exceed the ${MAX_DESC_LEN}-char marketplace cap:`);
    for (const v of violations) console.error(`  - ${v.name} (${v.len} chars, ${v.over} over)`);
    console.error("");
    console.error("Trim the description in acp-v2/src/offerings/<name>.ts.");
    console.error("Keep core delivery info; move verbose rationale to the .md spec.");
    process.exit(1);
  }
}

// v1.10.x: portfolio convention P32 — every property in every
// requirementSchema / deliverableSchema (including nested items.properties)
// MUST carry a description. The marketplace operator dashboard flags missing
// descriptions with a yellow warning + Butler Pro Mode routing weights schema
// completeness. Walks recursively; calls findMissingDescriptions() which
// shares its walker with the standalone `npm run` scanner.
{
  const issues = findMissingDescriptions(OFFERINGS);
  if (issues.length > 0) {
    const byOffering = new Map<string, typeof issues>();
    for (const iss of issues) {
      const k = iss.offering;
      if (!byOffering.has(k)) byOffering.set(k, []);
      byOffering.get(k)!.push(iss);
    }
    console.error(`ERROR: ${issues.length} propert${issues.length === 1 ? "y" : "ies"} missing description across ${byOffering.size} offering${byOffering.size === 1 ? "" : "s"}:`);
    for (const [name, list] of Array.from(byOffering.entries()).sort((a, b) => a[0].localeCompare(b[0]))) {
      console.error(`  ${name}:`);
      for (const iss of list) console.error(`    ${iss.schemaName}.${iss.path}`);
    }
    console.error("");
    console.error("Add a `description` field to each flagged property in acp-v2/src/offerings/<name>.ts.");
    console.error("Run `tsx scripts/check-property-descriptions.ts` for the standalone scan.");
    process.exit(1);
  }
}

function main() {
  const names = Object.keys(OFFERINGS).sort();
  if (names.length === 0) {
    console.log("(no offerings registered)");
    return;
  }
  for (const name of names) {
    const offering = OFFERINGS[name]!;
    const price = priceFor(name);
    console.log("=".repeat(72));
    console.log(`Offering name:        ${offering.name}`);
    console.log(`Price:                ${price.amount} ${price.token}`);
    console.log(`SLA:                  ${offering.slaMinutes} min`);
    console.log(`Description:`);
    console.log(`  ${offering.description}`);
    console.log(`Requirement schema (JSON):`);
    console.log(JSON.stringify(offering.requirementSchema, null, 2));
    console.log(`Example request (JSON):`);
    console.log(JSON.stringify(offering.requirementExample, null, 2));
    console.log(`Deliverable schema (JSON):`);
    console.log(JSON.stringify(offering.deliverableSchema, null, 2));
    console.log(`Example deliverable (JSON):`);
    console.log(JSON.stringify(offering.deliverableExample, null, 2));
    console.log("");
  }
  console.log("=".repeat(72));
  console.log(`Total: ${names.length} offering(s).`);
  console.log(`Paste each block into app.virtuals.io -> ACP_Metabot agent -> Offerings -> New offering.`);
}

main();
