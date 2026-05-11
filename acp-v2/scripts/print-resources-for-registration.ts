import { RESOURCES } from "../src/resources.js";

function main() {
  const names = Object.keys(RESOURCES).sort();
  if (names.length === 0) {
    console.log("=".repeat(72));
    console.log("(no resources registered)");
    console.log("");
    console.log("Resources are public, free, parameterised endpoints that buyer / orchestrator");
    console.log("agents (Butler etc.) call BEFORE paying for an offering. Register entries in");
    console.log("acp-v2/src/resources.ts and wire matching handlers in ACP_Metabot.Api/Program.cs.");
    return;
  }
  for (const name of names) {
    const r = RESOURCES[name]!;
    console.log("=".repeat(72));
    console.log(`Resource name:        ${r.name}`);
    console.log(`URL:                  ${r.url}`);
    console.log(`Description:`);
    console.log(`  ${r.description}`);
    console.log(`Params schema (JSON):`);
    console.log(JSON.stringify(r.params, null, 2));
    console.log("");
  }
  console.log("=".repeat(72));
  console.log(`Total: ${names.length} resource(s).`);
  console.log(`Paste each block into app.virtuals.io → TheMetaBot agent → Resources → New resource.`);
  console.log(`Resources are FREE — no price field. The marketplace form takes name + URL +`);
  console.log(`params schema + description. Butler-style buyer agents will call the URL to`);
  console.log(`introspect TheMetaBot pre-hire — make sure each URL responds 200 in production.`);
}

main();
