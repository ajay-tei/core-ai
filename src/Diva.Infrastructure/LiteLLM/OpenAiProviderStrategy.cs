using System.Net.Http;
using System.Text;
using System.Text.Json;
using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Infrastructure.Context;
using Diva.Infrastructure.Sessions;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace Diva.Infrastructure.LiteLLM;

/// <summary>
/// <see cref="ILlmProviderStrategy"/> implementation for OpenAI-compatible providers (LM Studio, Ollama, LiteLLM, Azure OpenAI).
/// Uses raw <see cref="IChatClient"/> — NO UseFunctionInvocation(); the caller drives the manual ReAct loop.
/// Created per-execution — holds mutable <see cref="ChatMessage"/> list + provider refs.
/// </summary>
internal sealed class OpenAiProviderStrategy : ILlmProviderStrategy
{
    private readonly IOpenAiProvider _openAiRef;
    private IChatClient _chatClient;
    private readonly IContextWindowManager _ctx;
    private string _model;
    private int _maxOutputTokens;
    private string? _currentApiKey;
    private string? _currentEndpoint;
    private readonly Func<Func<Task<ChatResponse>>, CancellationToken, Task<ChatResponse>> _retry;

    private List<ChatMessage> _messages = [];
    private ChatOptions _chatOptions = new();
    private ChatResponse? _lastResponse;
    private ChatMessage? _lastStreamedMessage;
    private TokenUsage _lastTokenUsage;
    // Transient vision message: passed to the LLM for one call only, never stored in _messages.
    // If the call fails (e.g. 400 from LM Studio) it is discarded rather than corrupting history.
    private ChatMessage? _transientVisionMessage;

    public OpenAiProviderStrategy(
        IOpenAiProvider openAi,
        IContextWindowManager ctx,
        string model,
        Func<Func<Task<ChatResponse>>, CancellationToken, Task<ChatResponse>> retry,
        int maxOutputTokens = 8192,
        string? apiKeyOverride = null,
        string? endpointOverride = null)
    {
        _openAiRef       = openAi;
        _chatClient      = openAi.CreateChatClient(model, apiKeyOverride, endpointOverride);  // raw — no UseFunctionInvocation()
        _ctx             = ctx;
        _model           = model;
        _maxOutputTokens = maxOutputTokens;
        _currentApiKey   = apiKeyOverride;
        _currentEndpoint = endpointOverride;
        _retry           = retry;
    }

    public void Initialize(
        string systemPrompt,
        List<ConversationTurn> history,
        string userQuery,
        List<McpClientTool> mcpTools,
        IReadOnlyList<ContentPart>? attachments = null)
    {
        _messages = [new ChatMessage(ChatRole.System, systemPrompt)];
        foreach (var turn in history)
            _messages.Add(new ChatMessage(
                turn.Role == "assistant" ? ChatRole.Assistant : ChatRole.User,
                turn.Content));

        // Include image attachments via DataContent when the provider supports vision.
        if (attachments is { Count: > 0 })
        {
            var contentList = new List<AIContent>();
            foreach (var part in attachments)
            {
                switch (part)
                {
                    case ImageContentPart img when img.Data is not null:
                        contentList.Add(new DataContent($"data:{img.MediaType};base64,{img.Data}", img.MediaType));
                        break;
                    case ImageContentPart img when img.Url is not null:
                        contentList.Add(new DataContent(img.Url, img.MediaType));
                        break;
                    // DocumentContentPart: not universally supported in OpenAI-compat; omit silently.
                }
            }
            contentList.Add(new TextContent(userQuery));
            _messages.Add(new ChatMessage(ChatRole.User, contentList));
        }
        else
        {
            _messages.Add(new ChatMessage(ChatRole.User, userQuery));
        }

        _chatOptions = new ChatOptions { ModelId = _model, MaxOutputTokens = _maxOutputTokens, AllowMultipleToolCalls = true };
        if (mcpTools.Count > 0)
            _chatOptions.Tools = [.. mcpTools];
    }

    /// <inheritdoc/>
    public void AddExtraTools(IReadOnlyList<AIFunction> tools)
    {
        if (tools.Count == 0) return;
        _chatOptions.Tools ??= new List<AITool>();
        foreach (var tool in tools)
            _chatOptions.Tools.Add(tool);
    }

    public async Task<UnifiedLlmResponse> CallLlmAsync(CancellationToken ct)
    {
        var callMessages = BuildCallMessages();
        var response = await _retry(() => _chatClient.GetResponseAsync(callMessages, _chatOptions, ct), ct);
        _lastResponse = response;
        _lastTokenUsage = new TokenUsage(
            (int)(response.Usage?.InputTokenCount  ?? 0),
            (int)(response.Usage?.OutputTokenCount ?? 0));

        var text = response.Text ?? "";

        var functionCalls = response.Messages
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Select(fc => new UnifiedToolCall(
                fc.CallId,
                fc.Name,
                fc.Arguments is not null ? JsonSerializer.Serialize(fc.Arguments) : "{}"))
            .ToList();

        // Normalise to "max_tokens" so the ReAct loop can check a single string for both providers
        var stopReason = response.FinishReason == ChatFinishReason.Length ? "max_tokens"
                       : response.FinishReason?.Value;
        return new UnifiedLlmResponse(text, functionCalls, functionCalls.Count > 0, stopReason);
    }

    public async Task<string?> CallReplanAsync(CancellationToken ct)
    {
        var replanOptions = new ChatOptions { ModelId = _model, MaxOutputTokens = 2048 };
        var response = await _chatClient.GetResponseAsync(_messages, replanOptions, ct);
        var text = response.Text ?? "";
        return string.IsNullOrEmpty(text) ? null : text;
    }

    public async IAsyncEnumerable<UnifiedStreamDelta> StreamLlmAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // ME.AI 10.4.1 streaming updates do not expose Usage — reset to zero.
        // TODO: populate when ME.AI surfaces usage in GetStreamingResponseAsync.
        _lastTokenUsage = default;

        string accText = "";
        var functionCallsById = new Dictionary<string, FunctionCallContent>(StringComparer.Ordinal);
        ChatFinishReason? finishReason = null;

        var callMessages = BuildCallMessages();
        await foreach (var update in _chatClient.GetStreamingResponseAsync(callMessages, _chatOptions, ct))
        {
            if (update.FinishReason.HasValue)
                finishReason = update.FinishReason;

            var deltaText = update.Text;
            if (!string.IsNullOrEmpty(deltaText))
            {
                accText += deltaText;
                yield return new UnifiedStreamDelta(deltaText, false, null);
            }

            foreach (var content in update.Contents)
            {
                if (content is FunctionCallContent fcc && !string.IsNullOrEmpty(fcc.CallId))
                    functionCallsById[fcc.CallId] = fcc;  // overwrite — final chunk has complete args
            }
        }

        // Build assistant message for CommitAssistantResponse
        var msgContents = new List<AIContent>();
        if (!string.IsNullOrEmpty(accText))
            msgContents.Add(new TextContent(accText));
        var unified = new List<UnifiedToolCall>();
        foreach (var fcc in functionCallsById.Values)
        {
            msgContents.Add(fcc);
            unified.Add(new UnifiedToolCall(
                fcc.CallId,
                fcc.Name,
                fcc.Arguments is not null ? JsonSerializer.Serialize(fcc.Arguments) : "{}"));
        }
        _lastStreamedMessage = new ChatMessage(ChatRole.Assistant, msgContents);

        var stopReason = finishReason == ChatFinishReason.Length ? "max_tokens"
                       : unified.Count > 0 ? "tool_calls" : "stop";
        yield return new UnifiedStreamDelta(null, true,
            new UnifiedLlmResponse(accText, unified, unified.Count > 0, stopReason));
    }

    public void UpdateSystemPrompt(string systemPrompt)
    {
        if (_messages.Count > 0 && _messages[0].Role == ChatRole.System)
        {
            _messages[0] = new ChatMessage(ChatRole.System, systemPrompt);
            return;
        }

        _messages.Insert(0, new ChatMessage(ChatRole.System, systemPrompt));
    }

    /// <inheritdoc/>
    public TokenUsage LastTokenUsage => _lastTokenUsage;

    public void CommitAssistantResponse()
    {
        _transientVisionMessage = null; // consume after successful call; survives retries/fallbacks
        if (_lastStreamedMessage is not null)
        {
            _messages.Add(_lastStreamedMessage);
            _lastStreamedMessage = null;
            return;
        }
        if (_lastResponse?.Messages is null) return;
        foreach (var msg in _lastResponse.Messages)
            _messages.Add(msg);
    }

    public void AddToolResults(IReadOnlyList<UnifiedToolResult> results)
    {
        // OpenAI format requires one ChatMessage(ChatRole.Tool) per result,
        // each immediately following the assistant tool_calls message.
        // Tool result content is text-only (FunctionResultContent is a string).
        foreach (var result in results)
            _messages.Add(new ChatMessage(ChatRole.Tool, [new FunctionResultContent(result.ToolCallId, result.Output)]));

        // For vision-capable OpenAI-compat models: inject image content as a follow-up user message.
        // FunctionResultContent is text-only; this is the only way to pass image pixels
        // to a vision model after a tool call in the OpenAI-compat protocol.
        var allImages = results
            .Where(r => r.ContentParts is { Count: > 0 })
            .SelectMany(r => r.ContentParts!.OfType<ImageContentPart>())
            .ToList();
        if (allImages.Count == 0) return;

        var contentList = new List<AIContent>();
        foreach (var img in allImages)
        {
            if (img.Data is not null)
                contentList.Add(new DataContent($"data:{img.MediaType};base64,{img.Data}", img.MediaType));
            else if (img.Url is not null)
                contentList.Add(new DataContent(img.Url, img.MediaType));
        }
        if (contentList.Count == 0) return;
        contentList.Add(new TextContent("Here is the image from the tool result. Please analyze the visual content."));
        // Store as transient — injected for the next LLM call only, never persisted in _messages.
        // If the call fails the message is discarded, preventing 400 errors from cascading.
        _transientVisionMessage = new ChatMessage(ChatRole.User, contentList);
    }

    // Returns _messages with the transient vision message appended if one is pending.
    // The transient is NOT cleared here — it persists so streaming-to-buffered fallback both see it.
    // CommitAssistantResponse() clears it after a successful call is committed.
    private IList<ChatMessage> BuildCallMessages()
    {
        if (_transientVisionMessage is null) return _messages;
        var list = new List<ChatMessage>(_messages.Count + 1);
        list.AddRange(_messages);
        list.Add(_transientVisionMessage);
        return list;
    }

    /// <inheritdoc/>
    // Local endpoints (LM Studio, llama.cpp) cannot handle images in follow-up tool-result
    // messages. Vision summarization routes the image through a clean single-turn call instead.
    public bool UseVisionSummarization => !string.IsNullOrEmpty(_currentEndpoint);

    /// <inheritdoc/>
    // Uses raw HttpClient instead of ME.AI DataContent to avoid .NET Uri percent-encoding
    // of base64 chars (+→%2B, =→%3D, /→%2F). LM Studio rejects percent-encoded payloads.
    // Makes two focused passes (objects then text) and concatenates for maximum coverage.
    public async Task<string> SummarizeImageAsync(ImageContentPart img, CancellationToken ct)
    {
        if (img.Data is null && img.Url is null) return string.Empty;
        if (string.IsNullOrEmpty(_currentEndpoint)) return string.Empty;

        var imageUrl = img.Data is not null
            ? $"data:{img.MediaType};base64,{img.Data}"
            : img.Url!;

        var endpoint = _currentEndpoint.TrimEnd('/');
        if (!endpoint.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            endpoint += "/v1";

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        if (!string.IsNullOrEmpty(_currentApiKey))
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {_currentApiKey}");

        var pass1 = await VisionCallAsync(http, endpoint, imageUrl,
            "You are an inventory scanner. List EVERY physical object, product, item, and person visible in this image. " +
            "For each: name/type, brand (if readable), color, quantity, and position (e.g. top-left shelf, center foreground). " +
            "Include even partially visible items. Be exhaustive — missing an item is a critical error.",
            ct);

        var pass2 = await VisionCallAsync(http, endpoint, imageUrl,
            "Read and transcribe ALL text visible in this image. " +
            "Include product names, brand names, prices, barcodes, shelf labels, signs, stickers, handwriting. " +
            "Group by location (e.g. top shelf, front display). Transcribe exactly as written.",
            ct);

        var parts = new List<string>(2);
        if (!string.IsNullOrWhiteSpace(pass1)) parts.Add($"[Objects & Inventory]\n{pass1}");
        if (!string.IsNullOrWhiteSpace(pass2)) parts.Add($"[Text & Labels]\n{pass2}");
        return string.Join("\n\n", parts);
    }

    private async Task<string> VisionCallAsync(
        HttpClient http, string endpoint, string imageUrl, string prompt, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            model      = _model,
            max_tokens = 2048,
            messages   = new[]
            {
                new
                {
                    role    = "user",
                    content = new object[]
                    {
                        new { type = "image_url", image_url = new { url = imageUrl } },
                        new { type = "text", text = prompt }
                    }
                }
            }
        });

        var url  = $"{endpoint}/chat/completions";
        var resp = await http.PostAsync(url,
            new StringContent(body, Encoding.UTF8, "application/json"), ct);

        var rawBody = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(
                $"HTTP {(int)resp.StatusCode} from {url}: {rawBody}",
                null, resp.StatusCode);

        using var doc = JsonDocument.Parse(rawBody);
        return doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? string.Empty;
    }

    public void AddUserMessage(string text) =>
        _messages.Add(new ChatMessage(ChatRole.User, text));

    public void AddAssistantThenUser(string assistantText, string userText)
    {
        _messages.Add(new ChatMessage(ChatRole.Assistant, assistantText));
        _messages.Add(new ChatMessage(ChatRole.User, userText));
    }

    public void CompactHistory(string systemPrompt, ContextWindowOverrideOptions? agentOverride) =>
        _messages = _ctx.MaybeCompactChatMessages(_messages, agentOverride);

    public void PrepareNewWindow(string continuationContext, string systemPrompt, ContextWindowOverrideOptions? agentOverride)
    {
        _messages = _ctx.MaybeCompactChatMessages(_messages, agentOverride);
        _messages.Add(new ChatMessage(ChatRole.User, continuationContext));
    }

    // ── Per-iteration model switching ─────────────────────────────────────────

    /// <inheritdoc/>
    public void SetModel(string model, int? maxTokens = null, string? apiKeyOverride = null, string? endpointOverride = null)
    {
        _model = model;
        if (maxTokens.HasValue) _maxOutputTokens = maxTokens.Value;

        bool keyChanged      = apiKeyOverride   is not null && apiKeyOverride   != _currentApiKey;
        bool endpointChanged = endpointOverride is not null && endpointOverride != _currentEndpoint;

        if (keyChanged || endpointChanged)
        {
            _currentApiKey   = apiKeyOverride   ?? _currentApiKey;
            _currentEndpoint = endpointOverride ?? _currentEndpoint;
            _chatClient = _openAiRef.CreateChatClient(model, _currentApiKey, _currentEndpoint);
        }

        // Update chatOptions in place so tool definitions are preserved
        _chatOptions.ModelId         = model;
        _chatOptions.MaxOutputTokens = _maxOutputTokens;
    }

    /// <inheritdoc/>
    public List<UnifiedHistoryEntry> ExportHistory()
    {
        var result = new List<UnifiedHistoryEntry>();

        // Skip the leading system message (index 0)
        foreach (var msg in _messages.Skip(1))
        {
            if (msg.Role == ChatRole.System) continue;

            var parts = new List<UnifiedHistoryPart>();
            foreach (var content in msg.Contents)
            {
                switch (content)
                {
                    case TextContent tc when !string.IsNullOrEmpty(tc.Text):
                        parts.Add(new TextHistoryPart(tc.Text));
                        break;
                    case FunctionCallContent fc:
                        var argsJson = fc.Arguments is not null
                            ? JsonSerializer.Serialize(fc.Arguments)
                            : "{}";
                        parts.Add(new ToolCallHistoryPart(fc.CallId, fc.Name, argsJson));
                        break;
                    case FunctionResultContent fr:
                        parts.Add(new ToolResultHistoryPart(fr.CallId, fr.Result?.ToString() ?? "", false));
                        break;
                }
            }

            if (parts.Count > 0)
            {
                var role = msg.Role == ChatRole.Assistant ? "assistant" : "user";
                result.Add(new UnifiedHistoryEntry { Role = role, Parts = parts });
            }
        }

        return result;
    }

    /// <inheritdoc/>
    public void ImportHistory(List<UnifiedHistoryEntry> history, string systemPrompt, List<McpClientTool> tools)
    {
        _messages     = [new ChatMessage(ChatRole.System, systemPrompt)];
        _lastResponse = null;

        foreach (var entry in history)
        {
            var role     = entry.Role == "assistant" ? ChatRole.Assistant : ChatRole.User;
            var contents = new List<AIContent>();

            foreach (var part in entry.Parts)
            {
                switch (part)
                {
                    case TextHistoryPart tp:
                        contents.Add(new TextContent(tp.Text));
                        break;
                    case ToolCallHistoryPart tc:
                        var args = JsonSerializer.Deserialize<IDictionary<string, object?>>(tc.InputJson);
                        contents.Add(new FunctionCallContent(tc.Id, tc.Name, args));
                        break;
                    case ToolResultHistoryPart tr:
                        contents.Add(new FunctionResultContent(tr.ToolCallId, tr.Output));
                        break;
                }
            }

            if (contents.Count > 0)
                _messages.Add(new ChatMessage(role, contents));
        }

        // Re-register tool definitions
        _chatOptions = new ChatOptions { ModelId = _model, MaxOutputTokens = _maxOutputTokens, AllowMultipleToolCalls = true };
        if (tools.Count > 0)
            _chatOptions.Tools = [.. tools];
    }
}
