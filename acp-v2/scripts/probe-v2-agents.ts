// One-off probe: authenticate as TheMetaBot and call api.acp.virtuals.io
// endpoints that need a Bearer to determine the V2 enumeration strategy.
//
// Background: spec 2026-04-30-v2-marketplace-source-design.md, "Risks /
// open questions" item 1. We need to know whether `GET /agents` returns
// (i) every V2 agent, (ii) only own-bot records, or (iii) something else
// before we pick Plan A vs Plan B.
//
// Usage: cd acp-v2 && npx tsx --env-file=.env scripts/probe-v2-agents.ts

import { AcpAgent } from "@virtuals-protocol/acp-node-v2";
import { loadEnv } from "../src/env.js";
import { createProvider } from "../src/provider.js";

async function main() {
  const env = loadEnv();
  console.log(`[probe] wallet=${env.walletAddress} chain=${env.chain}`);

  const provider = await createProvider(env);
  const agent = await AcpAgent.create({ provider });
  const api = agent.getApi() as any;

  // Force a fresh token. ensureAuthenticated() short-circuits if a valid
  // token is already cached; we want to see the auth call succeed too.
  await api.ensureAuthenticated();
  const token: string = api.token;
  console.log(`[probe] auth ok  -  token length=${token.length}`);

  const serverUrl = "https://api.acp.virtuals.io";
  const fetches: { label: string; url: string }[] = [
    { label: "agents (no params)", url: `${serverUrl}/agents` },
    { label: "agents?chainIds=8453", url: `${serverUrl}/agents?chainIds=8453` },
    { label: "agents?chainIds=8453&pageSize=200", url: `${serverUrl}/agents?chainIds=8453&pageSize=200` },
    { label: "agents?page=1&limit=50", url: `${serverUrl}/agents?page=1&limit=50` },
  ];

  for (const f of fetches) {
    try {
      const res = await fetch(f.url, {
        headers: { Authorization: `Bearer ${token}` },
      });
      console.log(`[probe] ${f.label}  status=${res.status}`);
      if (res.ok) {
        const body: any = await res.json();
        const dataLen = Array.isArray(body?.data) ? body.data.length : "n/a";
        const total = body?.pagination?.total ?? body?.total ?? "n/a";
        console.log(
          `       data.length=${dataLen}  pagination.total=${total}  topKeys=[${Object.keys(body).join(",")}]`
        );
        if (Array.isArray(body?.data) && body.data.length > 0) {
          const sample = body.data.slice(0, 5).map((a: any) => ({
            name: a.name,
            wallet: a.walletAddress,
            offerings: Array.isArray(a.offerings) ? a.offerings.length : "n/a",
          }));
          console.log(`       sample:`, JSON.stringify(sample));
        }
      } else {
        const text = await res.text();
        console.log(`       body: ${text.slice(0, 400)}`);
      }
    } catch (err) {
      console.log(`[probe] ${f.label}  threw: ${(err as Error).message}`);
    }
  }

  // Also confirm /agents/wallet/{addr} works with auth (we already know it
  // works without auth  -  this just rules out an auth-induced regression).
  try {
    const res = await fetch(
      `${serverUrl}/agents/wallet/${env.walletAddress.toLowerCase()}`,
      { headers: { Authorization: `Bearer ${token}` } }
    );
    console.log(`[probe] /agents/wallet/{self}  status=${res.status}`);
    if (res.ok) {
      const body: any = await res.json();
      const offerings = body?.data?.offerings ?? [];
      console.log(
        `       self has ${offerings.length} offerings: [${offerings
          .map((o: any) => o.name)
          .join(", ")}]`
      );
    }
  } catch (err) {
    console.log(`[probe] self-fetch threw: ${(err as Error).message}`);
  }

  console.log("[probe] done  -  agent.stop() not called (transport idle)");
  process.exit(0);
}

main().catch((err) => {
  console.error("[probe] fatal:", err);
  process.exit(1);
});
