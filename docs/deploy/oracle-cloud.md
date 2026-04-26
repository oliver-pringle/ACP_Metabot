# Deploying ACP_Metabot to Oracle Cloud Infrastructure

Target stack: Always-Free Ampere A1 (ARM64) running Ubuntu 22.04, single
VM, two Docker containers on a private bridge network. **No public ports
are exposed by the application** — the bot dials out to ACP chat servers
and listens via that persistent connection. SSH is the only inbound port
needed, and it should be locked to your IP.

---

## 1. Provision the VM (OCI console)

1. **Create instance** → "Compute → Instances → Create instance".
2. **Image**: Canonical Ubuntu 22.04 (latest). Pick the ARM build
   (`Canonical-Ubuntu-22.04-aarch64-...`).
3. **Shape**: `VM.Standard.A1.Flex`. **2 OCPU / 12 GB RAM** is plenty.
4. **Networking**: default VCN, public subnet, "Assign a public IPv4
   address". Generate or upload your SSH public key.
5. **Boot volume**: default 47 GB is fine.
6. Click **Create**.

Once the instance is RUNNING, copy its **public IP**.

## 2. Lock down SSH

OCI's default security list allows SSH from `0.0.0.0/0`. Narrow it:

1. **Networking → Virtual Cloud Networks → \<your VCN\> → Default Security List**.
2. Edit the ingress rule for port 22: change source CIDR from `0.0.0.0/0`
   to **\<your-ip\>/32** (find yours with `curl ifconfig.me`).
3. Save.

No other ingress rules are needed. The bot does not accept incoming
connections from buyers — buyer interactions happen via outbound chat
to ACP infrastructure.

## 3. SSH in and bootstrap

```bash
ssh ubuntu@<public-ip>

# Once on the VM:
git clone <your-repo-url> ~/ACP_Metabot
cd ~/ACP_Metabot/ACP_Metabot   # the inner project folder
bash deploy/bootstrap.sh
# log out and back in so the docker group takes effect:
exit
ssh ubuntu@<public-ip>
cd ~/ACP_Metabot/ACP_Metabot
docker --version   # sanity check, should not need sudo
```

## 4. Create production .env files

Two `.env` files are needed, both gitignored. Generate the shared API
key fresh — do NOT reuse the dev value.

### `~/ACP_Metabot/ACP_Metabot/.env` (top-level, used by docker-compose):

```env
VOYAGE_API_KEY=<your real Voyage key>
ANTHROPIC_API_KEY=<your real Anthropic key>
INTERNAL_API_KEY=<run: openssl rand -hex 32>
```

### `~/ACP_Metabot/ACP_Metabot/acp-v2/.env` (sidecar):

```env
ACP_WALLET_ADDRESS=0xecf9773b50f01f3a97b087a6ecdf12a71afc558c
ACP_WALLET_ID=<your wallet id>
ACP_SIGNER_PRIVATE_KEY=<your signer key, base64 PKCS8>
ACP_BUILDER_CODE=<your builder code or omit>
ACP_CHAIN=base
ACP_METABOT_API_URL=http://acp-metabot-api:5000
INTERNAL_API_KEY=<same value as above>
```

`INTERNAL_API_KEY` MUST be identical in both files.

Tighten file permissions before bringing the stack up:

```bash
chmod 600 .env acp-v2/.env
```

## 5. Bring up the stack

```bash
docker compose up -d --build
docker compose logs -f
```

Expected log lines:

- `acp-metabot-api  | [indexer] starting, interval=600s`
- `acp-metabot-api  | Now listening on: http://[::]:5000`
- `acp-metabot-api  | [watch-poller] started; tick=00:30:00`
- `acp-metabot-acp  | [seller] chain=base wallet=0x...`
- `acp-metabot-acp  | [seller] offerings registered (in code): search, composeStack, watchOffering`
- `acp-metabot-acp  | [seller] running — waiting for jobs`

The API container has a healthcheck against `/health`; the sidecar
waits for `service_healthy` before starting, so a slow indexer first-fetch
won't break the boot order.

## 6. Smoke test from your local machine

The ACP_Tester MCP tools call the live ACP network, not the VM directly,
so once the bot is running on Oracle, `acp_browse_agent` and `acp_hire`
should work the same as before.

From your local machine:

```text
acp_health
acp_browse_agent walletAddress=0xecf9773b50f01f3a97b087a6ecdf12a71afc558c
acp_hire sellerWalletAddress=0xecf9...8c offeringName=search
         requirement={"query":"wallet intelligence","limit":2}
```

Last call costs $0.01 USDC and should return a deliverable in ~25s.

## 7. Operations

### Tail logs

```bash
docker compose logs -f acp-metabot-api
docker compose logs -f acp-metabot-acp
```

### Restart after a code change

```bash
git pull
docker compose up -d --build
```

### Stop everything

```bash
docker compose down
```

### Backup the SQLite DB

```bash
sudo tar czf metabot-db-$(date +%F).tgz data/
```

The DB lives at `~/ACP_Metabot/ACP_Metabot/data/acp_metabot.db`. Bind-mounted
from host so it persists across `docker compose down`.

### Disk pressure

Container logs are capped at 100 MB per service (5 × 20 MB rotation),
so they won't fill the disk. The SQLite DB grows with the indexer; at
~35K offerings × small embeddings the file is in the low hundreds of MB.

## 8. What's NOT configured

- **Inbound HTTPS / public web** — the bot has no public surface; nothing
  to terminate TLS for.
- **Reverse proxy / nginx** — not needed.
- **Monitoring / alerts** — out of scope for v1. `docker compose ps` and
  log tailing cover most ops needs at low volume.
- **Auto-deploy on git push** — manual `git pull && docker compose up -d --build`
  for now. Worth adding GH Actions / a deploy webhook once the bot is
  earning meaningfully.

## 9. Rollback

If a deploy goes wrong:

```bash
git log --oneline -5             # find the last good commit
git checkout <good-sha>
docker compose up -d --build
```

The SQLite DB is forward-compatible across the schema migrations we ship
(`CREATE TABLE IF NOT EXISTS`), so rolling back code does not require
restoring the DB. If you ever need to wipe state and re-index from scratch:

```bash
docker compose down
rm data/acp_metabot.db data/acp_metabot.db-journal
docker compose up -d
```

The first `[indexer] api source: total reported=...` log line confirms
the corpus is being repopulated. Embeddings take ~5 minutes for the
full marketplace at default batch size.
