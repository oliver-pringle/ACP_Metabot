import { OFFERINGS } from "../src/offerings/registry.js";
import { priceFor } from "../src/pricing.js";

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
    console.log(`Description:`);
    console.log(`  ${offering.description}`);
    console.log(`Requirement schema (JSON):`);
    console.log(JSON.stringify(offering.requirementSchema, null, 2));
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
