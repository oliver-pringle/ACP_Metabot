using System.Globalization;
using System.Text.Json;

namespace ACP_Metabot.Api.Services;

// v1.8 Portfolio Risk Bot — typed HTTP clients to the four peer bots that
// risk_snapshot fans out to over the acp-shared docker network:
//
//   LiquidGuard  GET  /v1/internal/hf?wallet=&chain=
//   RevokeBot    GET  /v1/internal/quote?wallet=&chain=
//                POST /v1/internal/revoke_calldata
//   MEVProtect   GET  /v1/internal/mev_score?wallet=
//   EASIssuer    POST /v1/internal/attest
//
// Every method swallows transport errors and returns null. Per-bot result
// shape stays opaque (JsonDocument) — RiskSynthesisService inspects the
// fields it knows about and treats anything missing as "unavailable" rather
// than throwing. This is the core graceful-degradation primitive for the
// portfolio risk offerings.
//
// Auth: every peer's INTERNAL_API_KEY is loaded under a disambiguated env
// var per the cross-bot-key-sync convention (e.g. LIQUIDGUARD_API_KEY in
// Metabot's .env carries LiquidGuard's key). Empty key + unreachable URL are
// tolerated — calls just return null at runtime.

public interface IRiskPeerClients
{
    Task<JsonDocument?> GetHealthFactorAsync(string wallet, string chain, CancellationToken ct);
    Task<JsonDocument?> GetApprovalsQuoteAsync(string wallet, string chain, CancellationToken ct);
    Task<JsonDocument?> GetMevScoreAsync(string wallet, CancellationToken ct);
    Task<JsonDocument?> GetRevokeCalldataAsync(string wallet, string chain, string spender, string token, CancellationToken ct);
    Task<JsonDocument?> PublishAttestationAsync(object payload, CancellationToken ct);

    // v1.0.2 — schema bootstrap helpers. Lookup is a read-only GET against
    // EASIssuer's local DB (free); RegisterSchema burns mainnet ETH from
    // EAS_OPERATOR_PRIVATE_KEY and is only invoked when an operator opts in
    // via RISK_ATTEST_PRO_ENABLE_SCHEMA_REGISTER=true.
    Task<JsonDocument?> LookupEasSchemaByStringAsync(string schemaString, CancellationToken ct);
    Task<JsonDocument?> RegisterEasSchemaAsync(string schemaString, CancellationToken ct);
}

public sealed class RiskPeerClients : IRiskPeerClients
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<RiskPeerClients> _log;
    private readonly string _liquidGuardBase;
    private readonly string _revokeBotBase;
    private readonly string _mevProtectBase;
    private readonly string _easIssuerBase;
    private readonly string _liquidGuardKey;
    private readonly string _revokeBotKey;
    private readonly string _mevProtectKey;
    private readonly string _easIssuerKey;

    public RiskPeerClients(IHttpClientFactory http, IConfiguration config,
        ILogger<RiskPeerClients> log)
    {
        _http = http;
        _log = log;
        _liquidGuardBase = config["LiquidGuard:BaseUrl"]   ?? "http://liquidguard-api:5000/";
        _revokeBotBase   = config["RevokeBot:BaseUrl"]     ?? "http://revokebot-api:5000/";
        _mevProtectBase  = config["MEVProtect:BaseUrl"]    ?? "http://mevprotect-api:5000/";
        _easIssuerBase   = config["EASIssuer:BaseUrl"]     ?? "http://easissuer-api:5000/";
        // Security 2026-05-28 (audit HIGH-02): docker-compose.yml emits these
        // keys as LiquidGuard__ApiKey / RevokeBot__ApiKey / MEVProtect__ApiKey /
        // EASIssuer__ApiKey (config keys `LiquidGuard:ApiKey` etc.). The previous
        // code only read the flat LIQUIDGUARD_API_KEY-style names, so the keys
        // resolved empty in production and the X-API-Key header was silently
        // omitted on every cross-bot risk call. Read the hierarchical key first,
        // then fall back to the legacy flat name for any operator still setting it.
        _liquidGuardKey  = config["LiquidGuard:ApiKey"] ?? config["LIQUIDGUARD_API_KEY"] ?? "";
        _revokeBotKey    = config["RevokeBot:ApiKey"]   ?? config["REVOKEBOT_API_KEY"]   ?? "";
        _mevProtectKey   = config["MEVProtect:ApiKey"]  ?? config["MEVPROTECT_API_KEY"]  ?? "";
        _easIssuerKey    = config["EASIssuer:ApiKey"]   ?? config["EASISSUER_API_KEY"]   ?? "";
    }

    public Task<JsonDocument?> GetHealthFactorAsync(string wallet, string chain, CancellationToken ct)
        => GetJsonAsync(_liquidGuardBase, _liquidGuardKey,
            $"v1/internal/hf?wallet={Uri.EscapeDataString(wallet)}&chain={Uri.EscapeDataString(chain)}",
            "liquidguard", ct);

    public Task<JsonDocument?> GetApprovalsQuoteAsync(string wallet, string chain, CancellationToken ct)
        => GetJsonAsync(_revokeBotBase, _revokeBotKey,
            $"v1/internal/quote?wallet={Uri.EscapeDataString(wallet)}&chain={Uri.EscapeDataString(chain)}",
            "revokebot", ct);

    public Task<JsonDocument?> GetMevScoreAsync(string wallet, CancellationToken ct)
        => GetJsonAsync(_mevProtectBase, _mevProtectKey,
            $"v1/internal/mev_score?wallet={Uri.EscapeDataString(wallet)}",
            "mevprotect", ct);

    public Task<JsonDocument?> GetRevokeCalldataAsync(string wallet, string chain, string spender, string token, CancellationToken ct)
    {
        var body = new { wallet, chain, spender, token };
        return PostJsonAsync(_revokeBotBase, _revokeBotKey, "v1/internal/revoke_calldata", body, "revokebot", ct);
    }

    public Task<JsonDocument?> PublishAttestationAsync(object payload, CancellationToken ct)
        => PostJsonAsync(_easIssuerBase, _easIssuerKey, "v1/internal/attest", payload, "easissuer", ct);

    public Task<JsonDocument?> LookupEasSchemaByStringAsync(string schemaString, CancellationToken ct)
        => GetJsonAsync(_easIssuerBase, _easIssuerKey,
            $"v1/internal/schema/by-string?schemaString={Uri.EscapeDataString(schemaString)}",
            "easissuer", ct);

    public Task<JsonDocument?> RegisterEasSchemaAsync(string schemaString, CancellationToken ct)
        => PostJsonAsync(_easIssuerBase, _easIssuerKey, "v1/schema/register",
            new { schemaString }, "easissuer", ct);

    private async Task<JsonDocument?> GetJsonAsync(
        string baseUrl, string apiKey, string path, string peerName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        try
        {
            var http = _http.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            var url = baseUrl.EndsWith("/") ? baseUrl + path : baseUrl + "/" + path;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(apiKey)) req.Headers.Add("X-API-Key", apiKey);
            req.Headers.Add("X-Caller", "themetabot");
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug("[risk-peer:{Peer}] {Path} -> {Status}", peerName, path, (int)resp.StatusCode);
                return null;
            }
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[risk-peer:{Peer}] {Path} failed", peerName, path);
            return null;
        }
    }

    private async Task<JsonDocument?> PostJsonAsync(
        string baseUrl, string apiKey, string path, object body, string peerName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return null;
        try
        {
            var http = _http.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            var url = baseUrl.EndsWith("/") ? baseUrl + path : baseUrl + "/" + path;
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            if (!string.IsNullOrEmpty(apiKey)) req.Headers.Add("X-API-Key", apiKey);
            req.Headers.Add("X-Caller", "themetabot");
            req.Content = System.Net.Http.Json.JsonContent.Create(body);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug("[risk-peer:{Peer}] {Path} -> {Status}", peerName, path, (int)resp.StatusCode);
                return null;
            }
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[risk-peer:{Peer}] {Path} failed", peerName, path);
            return null;
        }
    }
}

// ── WitnessBot peer client (v1.0 riskAttestPro Task 3) ───────────────────────
//
// Distinct from IRiskPeerClients because:
//   - The response shape is a typed manifest (not opaque JsonDocument).
//   - The orchestrator (Task 6) needs to distinguish "fresh + IsAcpAgent=false"
//     (positive evidence a wallet has NOT been witnessed) from "unavailable"
//     (transport-class failure that counts against the 4-of-7 floor).
//
// Endpoint: GET WITNESSBOT_BASE_URL/v1/resources/manifestByAgent?agentAddress=
// Auth:    X-API-Key WITNESSBOT_API_KEY (portfolio cross-bot convention even
//          though manifestByAgent is a free Resource — keeps the lane symmetric
//          with LiquidGuard/RevokeBot/MEVProtect/EASIssuer).
// 200 →   populated manifest, Status="fresh", IsAcpAgent=true
// 404 →   no manifest, Status="fresh", IsAcpAgent=false (wallet not witnessed)
// other → Status="unavailable", IsAcpAgent=false, Details=HTTP code / exception

public sealed record WitnessManifest(
    bool IsAcpAgent,
    string? CatalogueHash,
    string? SignerEoa,
    string? SignedAt,
    string? ManifestUid,
    string Status,
    string Details);

public interface IWitnessBotClient
{
    Task<WitnessManifest> ManifestByAgentAsync(string agentAddress, CancellationToken ct = default);
}

public sealed class WitnessBotClient : IWitnessBotClient
{
    private readonly IHttpClientFactory _http;
    private readonly ILogger<WitnessBotClient> _log;
    private readonly string _baseUrl;
    private readonly string _apiKey;

    public WitnessBotClient(
        IHttpClientFactory http,
        IConfiguration config,
        ILogger<WitnessBotClient> log)
    {
        _http = http;
        _log = log;
        // Match the neighbour-client pattern: hierarchical config key first,
        // flat env-style fallback for any operator still setting it that way.
        _baseUrl = config["WitnessBot:BaseUrl"]
            ?? config["WITNESSBOT_BASE_URL"]
            ?? "";
        _apiKey = config["WitnessBot:ApiKey"]
            ?? config["WITNESSBOT_API_KEY"]
            ?? "";
    }

    public async Task<WitnessManifest> ManifestByAgentAsync(string agentAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_baseUrl) || string.IsNullOrWhiteSpace(_apiKey))
        {
            return new WitnessManifest(
                IsAcpAgent: false,
                CatalogueHash: null,
                SignerEoa: null,
                SignedAt: null,
                ManifestUid: null,
                Status: "unavailable",
                Details: "WITNESSBOT_BASE_URL/API_KEY not set");
        }

        try
        {
            var http = _http.CreateClient();
            http.Timeout = TimeSpan.FromSeconds(15);
            var path = $"v1/resources/manifestByAgent?agentAddress={Uri.EscapeDataString(agentAddress)}";
            var url = _baseUrl.EndsWith("/") ? _baseUrl + path : _baseUrl + "/" + path;
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("X-API-Key", _apiKey);
            req.Headers.Add("X-Caller", "themetabot");
            using var resp = await http.SendAsync(req, ct);

            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new WitnessManifest(
                    IsAcpAgent: false,
                    CatalogueHash: null,
                    SignerEoa: null,
                    SignedAt: null,
                    ManifestUid: null,
                    Status: "fresh",
                    Details: "no manifest");
            }

            if (!resp.IsSuccessStatusCode)
            {
                var status = ((int)resp.StatusCode).ToString(CultureInfo.InvariantCulture);
                _log.LogWarning("[risk-peer:witnessbot] manifestByAgent -> {Status}", status);
                return new WitnessManifest(
                    IsAcpAgent: false,
                    CatalogueHash: null,
                    SignerEoa: null,
                    SignedAt: null,
                    ManifestUid: null,
                    Status: "unavailable",
                    Details: $"HTTP {status}");
            }

            var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
            var root = doc.RootElement;

            string? catalogueHash = TryGetString(root, "catalogueHash");
            string? signerEoa = TryGetString(root, "signerAddress")
                ?? TryGetString(root, "signerEoa");
            string? signedAt = TryGetString(root, "signedAt");
            string? manifestUid = TryGetString(root, "manifestUid");

            return new WitnessManifest(
                IsAcpAgent: true,
                CatalogueHash: catalogueHash,
                SignerEoa: signerEoa,
                SignedAt: signedAt,
                ManifestUid: manifestUid,
                Status: "fresh",
                Details: "manifest current");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[risk-peer:witnessbot] manifestByAgent failed");
            return new WitnessManifest(
                IsAcpAgent: false,
                CatalogueHash: null,
                SignerEoa: null,
                SignedAt: null,
                ManifestUid: null,
                Status: "unavailable",
                Details: ex.Message);
        }
    }

    private static string? TryGetString(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }
}

// Noop implementation — registered when env vars are unset OR for unit tests
// that exercise the orchestrator without a real HttpClient. Always returns
// the "unavailable" sentinel so the orchestrator treats it as a soft failure.
public sealed class NoopWitnessBotClient : IWitnessBotClient
{
    public Task<WitnessManifest> ManifestByAgentAsync(string agentAddress, CancellationToken ct = default)
        => Task.FromResult(new WitnessManifest(
            IsAcpAgent: false,
            CatalogueHash: null,
            SignerEoa: null,
            SignedAt: null,
            ManifestUid: null,
            Status: "unavailable",
            Details: "noop client"));
}
