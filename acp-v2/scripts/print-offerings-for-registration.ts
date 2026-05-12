import { OFFERINGS } from "../src/offerings/registry.js";
import { priceFor } from "../src/pricing.js";

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
  console.log(`Paste each block into app.virtuals.io → ACP_Metabot agent → Offerings → New offering.`);
}

main();
