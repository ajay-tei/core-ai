using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Diva.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Diva.Infrastructure.LiteLLM;

/// <summary>Real implementation — wraps AnthropicClient for production use.</summary>
public sealed class AnthropicProvider : IAnthropicProvider
{
    // Anthropic beta flag that lets the model interleave (and, for supported models, expose)
    // thinking blocks between tool calls. Applied only to requests that opt in via
    // ThinkingParameters.UseInterleavedThinking so non-thinking agents are never affected.
    private const string InterleavedThinkingBeta = "interleaved-thinking-2025-05-14";

    private readonly AnthropicClient _client;      // default client, no beta headers
    private readonly AnthropicClient _betaClient;  // same auth, interleaved-thinking beta header set
    private readonly string _defaultApiKey;
    private readonly HttpClient _httpClient;

    public AnthropicProvider(IOptions<LlmOptions> opts, HttpClient httpClient)
    {
        _httpClient     = httpClient;
        _defaultApiKey  = opts.Value.DirectProvider.ApiKey;
        _client         = new AnthropicClient(new APIAuthentication(_defaultApiKey), httpClient);
        _betaClient     = new AnthropicClient(new APIAuthentication(_defaultApiKey), httpClient)
        {
            AnthropicBetaVersion = InterleavedThinkingBeta
        };
    }

    /// <summary>Selects the beta-enabled client only when the request opts into interleaved thinking.</summary>
    private AnthropicClient ResolveClient(MessageParameters parameters, string? apiKeyOverride)
    {
        var wantsBeta = parameters.Thinking?.UseInterleavedThinking == true;

        if (apiKeyOverride is not null)
            return new AnthropicClient(new APIAuthentication(apiKeyOverride), _httpClient)
            {
                AnthropicBetaVersion = wantsBeta ? InterleavedThinkingBeta : null
            };

        return wantsBeta ? _betaClient : _client;
    }

    public Task<MessageResponse> GetClaudeMessageAsync(MessageParameters parameters, CancellationToken ct, string? apiKeyOverride = null)
        => ResolveClient(parameters, apiKeyOverride).Messages.GetClaudeMessageAsync(parameters, ct);

    public IAsyncEnumerable<MessageResponse> StreamClaudeMessageAsync(MessageParameters parameters, CancellationToken ct, string? apiKeyOverride = null)
        => ResolveClient(parameters, apiKeyOverride).Messages.StreamClaudeMessageAsync(parameters, ct);
}
