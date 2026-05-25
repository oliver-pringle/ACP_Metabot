import { RESOURCES } from "../src/resources.js";

// Marketplace pre-flight check: Resource names cap at 30 chars on
// app.virtuals.io (per the Resource interface comment). Fail fast.
{
  const MAX_NAME_LEN = 30;
  const violations = Object.values(RESOURCES)
    .filter(r => r.name.length > MAX_NAME_LEN)
    .map(r => ({ name: r.name, len: r.name.length, over: r.name.length - MAX_NAME_LEN }));
  if (violations.length > 0) {
    console.error(`ERROR: ${violations.length} resource name(s) exceed the ${MAX_NAME_LEN}-char marketplace cap:`);
    for (const v of violations) console.error(`  - ${v.name} (${v.len} chars, ${v.over} over)`);
    console.error("");
    console.error("Rename them in acp-v2/src/resources.ts and rerun.");
    process.exit(1);
  }
}

// v1.7.4: Resource descriptions also cap at 500 chars on app.virtuals.io
// (caught 2026-05-12 registering RevokeBot.quote at 540 chars  -  marketplace
// rejects at registration time but the dashboard hint doesn't surface the
// limit). Same guard pattern as offering descriptions in print-offerings-for-registration.
{
  const MAX_DESC_LEN = 500;
  const violations = Object.values(RESOURCES)
    .filter(r => (r.description ?? "").length > MAX_DESC_LEN)
    .map(r => ({
      name: r.name,
      len: r.description.length,
      over: r.description.length - MAX_DESC_LEN
    }));
  if (violations.length > 0) {
    console.error(`ERROR: ${violations.length} resource description(s) exceed the ${MAX_DESC_LEN}-char marketplace cap:`);
    for (const v of violations) console.error(`  - ${v.name} (${v.len} chars, ${v.over} over)`);
    console.error("");
    console.error("Trim the description in acp-v2/src/resources.ts.");
    console.error("Keep core delivery info; move verbose rationale to the .md spec.");
    process.exit(1);
  }
}

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
  console.log(`Paste each block into app.virtuals.io -> TheMetaBot agent -> Resources -> New resource.`);
  console.log(`Resources are FREE  -  no price field. The marketplace form takes name + URL +`);
  console.log(`params schema + description. Butler-style buyer agents will call the URL to`);
  console.log(`introspect TheMetaBot pre-hire  -  make sure each URL responds 200 in production.`);
}

main();
