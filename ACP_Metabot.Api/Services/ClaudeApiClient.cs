using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace ACP_Metabot.Api.Services;

public class ClaudeApiClient : IClaudeClient
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly ILogger<ClaudeApiClient> _logger;

    public ClaudeApiClient(IConfiguration config, IHttpClientFactory httpFactory,
        ILogger<ClaudeApiClient> logger)
    {
        var apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")
            ?? throw new InvalidOperationException("ANTHROPIC_API_KEY not set");
        _model = config["Claude:Model"] ?? "claude-sonnet-4-6";
        _http = httpFactory.CreateClient(nameof(ClaudeApiClient));
        _http.BaseAddress ??= new Uri("https://api.anthropic.com/v1/");
        _http.DefaultRequestHeaders.Add("x-api-key", apiKey);
        _http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        _logger = logger;
    }

    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct)
    {
        var req = new MessagesRequest
        {
            Model = _model,
            MaxTokens = maxTokens,
            // Cache the system prompt (5-min TTL) so repeated composeStack calls
            // amortise the long instruction block.
            System = new[]
            {
                new SystemBlock { Type = "text", Text = systemPrompt, CacheControl = new CacheControl { Type = "ephemeral" } }
            },
            Messages = new[]
            {
                new MessageBlock { Role = "user", Content = userPrompt }
            }
        };

        HttpResponseMessage resp;
        try
        {
            resp = await _http.PostAsJsonAsync("messages", req, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (HttpRequestException ex)
        {
            throw new ClaudeApiException(0, null,
                $"Anthropic Messages network failure: {ex.Message}", ex);
        }
        catch (Exception ex) when (ex is TaskCanceledException || ex is OperationCanceledException)
        {
            throw new ClaudeApiException(0, null,
                $"Anthropic Messages timed out: {ex.Message}", ex);
        }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct);
                // P30/P11 (audit #6): keep the full upstream body in the structured .Body
                // field (debug-only access) but NOT in the exception MESSAGE, which is what
                // standard logging emits - an Anthropic error body can echo prompt fragments
                // / policy detail and must not land in error logs.
                throw new ClaudeApiException((int)resp.StatusCode, body,
                    $"Anthropic Messages call failed: HTTP {(int)resp.StatusCode} (body {body.Length} chars)");
            }
            var parsed = await resp.Content.ReadFromJsonAsync<MessagesResponse>(cancellationToken: ct)
                ?? throw new InvalidOperationException("Anthropic returned empty body");
            var text = parsed.Content?.FirstOrDefault(c => c.Type == "text")?.Text;
            return text ?? string.Empty;
        }
    }

    private class MessagesRequest
    {
        [JsonPropertyName("model")]      public string Model { get; set; } = "";
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; set; }
        [JsonPropertyName("system")]     public SystemBlock[]? System { get; set; }
        [JsonPropertyName("messages")]   public MessageBlock[]? Messages { get; set; }
    }

    private class SystemBlock
    {
        [JsonPropertyName("type")]          public string Type { get; set; } = "text";
        [JsonPropertyName("text")]          public string Text { get; set; } = "";
        [JsonPropertyName("cache_control")] public CacheControl? CacheControl { get; set; }
    }

    private class CacheControl
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "ephemeral";
    }

    private class MessageBlock
    {
        [JsonPropertyName("role")]    public string Role { get; set; } = "user";
        [JsonPropertyName("content")] public string Content { get; set; } = "";
    }

    private class MessagesResponse
    {
        [JsonPropertyName("content")] public List<ContentBlock>? Content { get; set; }
    }

    private class ContentBlock
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "";
        [JsonPropertyName("text")] public string Text { get; set; } = "";
    }
}
