using System.Text.Json;

namespace ACP_Metabot.Api.Services;

// Thin HTTP client that talks to ArenaBot's free Resources surface over the
// acp-shared docker network. We DO NOT pay for ArenaBot's offerings here —
// only consume its public Resources (arenaLeaderboardStatus, arenaWindow,
// hip3Catalog, capabilities). All endpoints are GET, no auth required when
// reached over the private bridge network.
//
// Config keys:
//   Arena:BaseUrl       (e.g. "http://arenabot-api:5000")
//   Arena:ApiKey        (optional, only when ArenaBot has X-API-Key on)
//   Arena:Enabled       (set "false" to disable Arena ingestion entirely)
public class TheArenaBotClient
{
    private readonly HttpClient _http;
    private readonly ILogger<TheArenaBotClient> _logger;
    private readonly bool _enabled;
    private readonly string? _apiKey;

    public TheArenaBotClient(HttpClient http, IConfiguration config, ILogger<TheArenaBotClient> logger)
    {
        _http = http;
        _logger = logger;
        var baseUrl = config["Arena:BaseUrl"];
        _apiKey = config["Arena:ApiKey"];
        _enabled = !string.IsNullOrWhiteSpace(baseUrl)
                   && !string.Equals(config["Arena:Enabled"], "false", StringComparison.OrdinalIgnoreCase);
        if (_enabled)
        {
            _http.BaseAddress = new Uri(baseUrl!);
            _http.Timeout = TimeSpan.FromSeconds(15);
        }
    }

    public bool Enabled => _enabled;

    public async Task<JsonDocument?> GetLeaderboardStatusAsync(CancellationToken ct)
        => await GetJsonAsync("/v1/resources/arenaLeaderboardStatus", ct);

    public async Task<JsonDocument?> GetArenaWindowAsync(CancellationToken ct)
        => await GetJsonAsync("/v1/resources/arenaWindow", ct);

    public async Task<JsonDocument?> GetCapabilitiesAsync(CancellationToken ct)
        => await GetJsonAsync("/v1/resources/capabilities", ct);

    public async Task<JsonDocument?> GetHip3CatalogAsync(CancellationToken ct)
        => await GetJsonAsync("/v1/resources/hip3Catalog", ct);

    private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken ct)
    {
        if (!_enabled) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            if (!string.IsNullOrEmpty(_apiKey)) req.Headers.Add("X-API-Key", _apiKey);
            var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("ArenaBot {Path} -> {Status}", path, (int)resp.StatusCode);
                return null;
            }
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ArenaBot {Path} failed", path);
            return null;
        }
    }
}
