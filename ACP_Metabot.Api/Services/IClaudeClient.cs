namespace ACP_Metabot.Api.Services;

public interface IClaudeClient
{
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, int maxTokens, CancellationToken ct);
}
