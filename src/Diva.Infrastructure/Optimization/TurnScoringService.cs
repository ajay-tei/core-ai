using System.ClientModel;
using System.Text.Json;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Diva.Core.Configuration;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.LiteLLM;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace Diva.Infrastructure.Optimization;

public sealed class TurnScoringService : ITurnScoringService
{
    private readonly LlmOptions _llm;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AgentOptions _opts;
    private readonly ILlmConfigResolver _resolver;
    private readonly ILogger<TurnScoringService> _logger;

    public TurnScoringService(
        IOptions<LlmOptions> llm,
        IOptions<AgentOptions> opts,
        IServiceScopeFactory scopeFactory,
        ILlmConfigResolver resolver,
        ILogger<TurnScoringService> logger)
    {
        _llm = llm.Value;
        _opts = opts.Value;
        _scopeFactory = scopeFactory;
        _resolver = resolver;
        _logger = logger;
    }

    public async Task ScoreTurnAsync(
        string sessionId,
        int turnNumber,
        string agentId,
        string userMessage,
        string assistantResponse,
        string toolEvidence,
        CancellationToken ct)
    {
        if (!_opts.Optimization.EnablePerTurnScoring) return;

        try
        {
            var prompt = BuildScoringPrompt(userMessage, assistantResponse, toolEvidence);
            var llmConf = await ResolveLlmConfigAsync(agentId, ct);
            var raw = llmConf.Provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
                ? await CallAnthropicAsync(prompt, llmConf, ct)
                : await CallOpenAiCompatibleAsync(prompt, llmConf, ct);

            var scores = ParseScores(raw);
            if (scores is null) return;

            await using var scope = _scopeFactory.CreateAsyncScope();
            var traceDb = scope.ServiceProvider.GetRequiredService<SessionTraceDbContext>();
            var turn = await traceDb.TraceSessionTurns
                .FirstOrDefaultAsync(t => t.SessionId == sessionId && t.TurnNumber == turnNumber, ct);
            if (turn is null) return;

            turn.FaithfulnessScore = scores.Faithfulness;
            turn.CompletenessScore = scores.Completeness;
            turn.ToolEfficiencyScore = scores.ToolEfficiency;
            turn.CoherenceScore = scores.Coherence;
            turn.ScoresAvailable = true;

            await traceDb.SaveChangesAsync(ct);
            _logger.LogDebug(
                "Scored turn {Turn}/{Session}: faithfulness={F:F2} completeness={C:F2} toolEff={T:F2} coherence={Co:F2}",
                turnNumber, sessionId, scores.Faithfulness, scores.Completeness, scores.ToolEfficiency, scores.Coherence);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Turn scoring failed for session={Session} turn={Turn}", sessionId, turnNumber);
        }
    }

    private string BuildScoringPrompt(string userMessage, string assistantResponse, string toolEvidence)
    {
        var truncatedResponse = assistantResponse.Length > 1500
            ? assistantResponse[..1500] + "..."
            : assistantResponse;
        var truncatedEvidence = toolEvidence.Length > 500
            ? toolEvidence[..500] + "..."
            : toolEvidence;

        return
            $"Rate this AI agent response on 4 dimensions from 0.0 to 1.0.\n\n" +
            $"User query: {userMessage}\n" +
            $"Agent response: {truncatedResponse}\n" +
            $"Tool evidence available: {truncatedEvidence}\n\n" +
            "Score each dimension:\n" +
            "- faithfulness: Did every factual claim come from the tool evidence? (1.0=fully grounded, 0.0=hallucinated claims)\n" +
            "- completeness: Did the response address all parts of the query? (1.0=fully addressed, 0.0=only partially answered)\n" +
            "- tool_efficiency: Were tool calls appropriate? (1.0=optimal, 0.0=wrong tools or none when needed)\n" +
            "- coherence: Is the response clear and well-structured? (1.0=clear, 0.0=confusing/disorganized)\n\n" +
            "Return ONLY JSON: {\"faithfulness\":0.0,\"completeness\":0.0,\"tool_efficiency\":0.0,\"coherence\":0.0}";
    }

    private async Task<ResolvedLlmConfig> ResolveLlmConfigAsync(string agentId, CancellationToken ct)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<DivaDbContext>();
            var agent = await db.AgentDefinitions
                .AsNoTracking()
                .FirstOrDefaultAsync(a => a.Id == agentId, ct);
            if (agent is not null)
                return await _resolver.ResolveAsync(agent.TenantId, agent.LlmConfigId, agent.ModelId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve LLM config for agent {AgentId} — falling back to global defaults", agentId);
        }
        // Fallback: build a ResolvedLlmConfig from global DirectProvider options
        var d = _llm.DirectProvider;
        return new ResolvedLlmConfig(d.Provider, d.ApiKey, d.Model, d.Endpoint, null, []);
    }

    private async Task<string> CallAnthropicAsync(string prompt, ResolvedLlmConfig conf, CancellationToken ct)
    {
        var client = new AnthropicClient(new APIAuthentication(conf.ApiKey));
        var parameters = new MessageParameters
        {
            Model = conf.Model,
            MaxTokens = _opts.Optimization.ScorerMaxTokens,
            System = [new SystemMessage("You are a JSON-only scorer. Respond ONLY with the JSON object.")],
            Messages = [new Message { Role = RoleType.User, Content = [new TextContent { Text = prompt }] }]
        };
        var msg = await client.Messages.GetClaudeMessageAsync(parameters, ct);
        return msg.Content.OfType<TextContent>().FirstOrDefault()?.Text ?? "{}";
    }

    private async Task<string> CallOpenAiCompatibleAsync(string prompt, ResolvedLlmConfig conf, CancellationToken ct)
    {
        var credential = new ApiKeyCredential(string.IsNullOrEmpty(conf.ApiKey) ? "no-key" : conf.ApiKey);
        var clientOpts = new OpenAIClientOptions();
        if (!string.IsNullOrEmpty(conf.Endpoint))
            clientOpts.Endpoint = new Uri(conf.Endpoint);
        var chatClient = new OpenAIClient(credential, clientOpts).GetChatClient(conf.Model);
        var messages = new ChatMessage[]
        {
            new SystemChatMessage("You are a JSON-only scorer. Respond ONLY with the JSON object."),
            new UserChatMessage(prompt)
        };
        var result = await chatClient.CompleteChatAsync(messages,
            new ChatCompletionOptions { MaxOutputTokenCount = _opts.Optimization.ScorerMaxTokens }, ct);
        return result.Value.Content.FirstOrDefault()?.Text ?? "{}";
    }

    private ScoringResult? ParseScores(string raw)
    {
        try
        {
            var json = raw.Trim().TrimStart('`').TrimEnd('`');
            var start = json.IndexOf('{');
            var end = json.LastIndexOf('}');
            if (start < 0 || end < start) return null;
            json = json[start..(end + 1)];

            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return new ScoringResult
            {
                Faithfulness = GetFloat(r, "faithfulness"),
                Completeness = GetFloat(r, "completeness"),
                ToolEfficiency = GetFloat(r, "tool_efficiency"),
                Coherence = GetFloat(r, "coherence")
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to parse scoring response");
            return null;
        }
    }

    private static float GetFloat(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Number)
            return Math.Clamp((float)prop.GetDouble(), 0f, 1f);
        return 0f;
    }

    private sealed record ScoringResult
    {
        public float Faithfulness { get; init; }
        public float Completeness { get; init; }
        public float ToolEfficiency { get; init; }
        public float Coherence { get; init; }
    }
}
