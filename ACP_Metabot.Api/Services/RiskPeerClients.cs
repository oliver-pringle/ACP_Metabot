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
