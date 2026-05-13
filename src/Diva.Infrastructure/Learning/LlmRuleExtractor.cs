using System.ClientModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic.SDK;
using Anthropic.SDK.Messaging;
using Diva.Core.Configuration;
using Diva.Infrastructure.LiteLLM;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using OpenAI.Chat;

namespace Diva.Infrastructure.Learning;

/// <summary>
/// Makes a single-shot LLM call to extract business rules from a conversation transcript.
/// Uses the same provider split as AnthropicAgentRunner and ResponseVerifier:
///   "Anthropic" -> native Anthropic.SDK (avoids ME.AI version conflict)
///   everything else -> OpenAI-compatible ChatClient
/// </summary>
public sealed class LlmRuleExtractor
{
    private readonly ILlmConfigResolver _resolver;
    private readonly int _extractorMaxTokens;
    private readonly ILogger<LlmRuleExtractor> _logger;

    public LlmRuleExtractor(
        IOptions<AgentOptions> agentOptions,
        ILlmConfigResolver resolver,
        ILogger<LlmRuleExtractor> logger)
    {
        _resolver           = resolver;
        _extractorMaxTokens = agentOptions.Value.RuleLearning.ExtractorMaxTokens;
        _logger             = logger;
    }

    public async Task<List<SuggestedRule>> ExtractAsync(
        string conversationTranscript,
        string sessionId,
        CancellationToken ct)
    {
        _logger.LogDebug("Extracting rules from transcript (session={SessionId})", sessionId);

        try
        {
            var prompt = BuildPrompt(conversationTranscript);
            var conf   = await _resolver.ResolveAsync(0, null, null, ct);
            var raw    = conf.Provider.Equals("Anthropic", StringComparison.OrdinalIgnoreCase)
                ? await CallAnthropicAsync(prompt, conf, ct)
                : await CallOpenAiCompatibleAsync(prompt, conf, ct);

            return ParseRules(raw, sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Rule extraction failed -- returning empty list");
            return [];
        }
    }

    // -- Provider calls ----------------------------------------------------

    private async Task<string> CallAnthropicAsync(string prompt, ResolvedLlmConfig conf, CancellationToken ct)
    {
        var client     = new AnthropicClient(new APIAuthentication(conf.ApiKey));
        var parameters = new MessageParameters
        {
            Model     = conf.Model,
            MaxTokens = _extractorMaxTokens,
            System    = [new SystemMessage("You are a JSON-only business rule detector. Respond ONLY with a valid JSON array.")],
            Messages  = [new Message
            {
                Role    = RoleType.User,
                Content = [new Anthropic.SDK.Messaging.TextContent { Text = prompt }]
            }]
        };

        var msg = await client.Messages.GetClaudeMessageAsync(parameters, ct);
        return msg.Content.OfType<Anthropic.SDK.Messaging.TextContent>().FirstOrDefault()?.Text ?? "[]";
    }

    private async Task<string> CallOpenAiCompatibleAsync(string prompt, ResolvedLlmConfig conf, CancellationToken ct)
    {
        var credential = new ApiKeyCredential(string.IsNullOrEmpty(conf.ApiKey) ? "no-key" : conf.ApiKey);
        var clientOpts = new OpenAIClientOptions();
        if (!string.IsNullOrEmpty(conf.Endpoint))
            clientOpts.Endpoint = new Uri(conf.Endpoint);

        var chatClient = new OpenAIClient(credential, clientOpts).GetChatClient(conf.Model);
        var messages   = new ChatMessage[]
        {
            new SystemChatMessage("You are a JSON-only business rule detector. Respond ONLY with a valid JSON array."),
            new UserChatMessage(prompt)
        };

        var result = await chatClient.CompleteChatAsync(messages, new ChatCompletionOptions { MaxOutputTokenCount = _extractorMaxTokens }, ct);
        return result.Value.Content.FirstOrDefault()?.Text ?? "[]";
    }

    // -- Prompt & parsing --------------------------------------------------

    private static string BuildPrompt(string transcript) =>
        "Analyze this conversation and identify any business rules the user is defining or correcting.\n\n" +
        "Look for patterns like:\n" +
        "- \"For us, X should be Y\"\n" +
        "- \"Actually, we don't count X in Y\"\n" +
        "- \"Our policy is X\"\n" +
        "- Corrections to agent behaviour\n" +
        "- Clarifications about business logic or terminology\n\n" +
        "## Conversation\n" +
        transcript + "\n\n" +
        "## Output\n" +
        "Return a JSON array. Each element:\n" +
        "  { \"agent_type\": \"*\", \"rule_category\": \"reporting\", \"rule_key\": \"snake_case_key\",\n" +
        "    \"prompt_injection\": \"text to inject into future agent prompts\", \"confidence\": 0.0 }\n\n" +
        "agent_type: \"*\" for all agents, or \"Analytics\", \"Reservation\", etc.\n" +
        "confidence: 0.0-1.0 (how certain you are this is a deliberate business rule).\n" +
        "Return [] if no rules found. Return ONLY valid JSON, no explanation.";

    private List<SuggestedRule> ParseRules(string raw, string sessionId)
    {
        var json = Regex.Replace(raw.Trim(), @"^```json?\s*|\s*```$", "", RegexOptions.Multiline).Trim();
        if (string.IsNullOrEmpty(json) || json == "[]") return [];

        var firstBracket = json.IndexOf('[');
        var lastBracket  = json.LastIndexOf(']');
        if (firstBracket >= 0 && lastBracket > firstBracket)
            json = json[firstBracket..(lastBracket + 1)];

        try
        {
            using var doc     = JsonDocument.Parse(json);
            var       results = new List<SuggestedRule>();

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var injection = el.TryGetProperty("prompt_injection", out var pi) ? pi.GetString() : null;
                if (string.IsNullOrWhiteSpace(injection)) continue;

                var confidence = el.TryGetProperty("confidence", out var c) ? (float)c.GetDouble() : 0f;

                results.Add(new SuggestedRule
                {
                    AgentType       = el.TryGetProperty("agent_type",    out var at) ? at.GetString() : "*",
                    RuleCategory    = el.TryGetProperty("rule_category", out var rc) ? rc.GetString() ?? "" : "",
                    RuleKey         = el.TryGetProperty("rule_key",      out var rk) ? rk.GetString() ?? "" : "",
                    PromptInjection = injection,
                    Confidence      = confidence,
                    SourceSessionId = sessionId
                });
            }

            _logger.LogDebug("Extracted {Count} rule candidates", results.Count);
            return results;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Failed to parse rule extractor response -- returning empty list");
            return [];
        }
    }
}
