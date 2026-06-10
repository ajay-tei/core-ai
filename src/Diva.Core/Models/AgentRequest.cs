namespace Diva.Core.Models;

public sealed class AgentRequest
{
    /// <summary>The user's natural language query or task description.</summary>
    public string Query { get; init; } = string.Empty;

    /// <summary>Session ID for multi-turn conversation. Null = new session.</summary>
    public string? SessionId { get; init; }

    /// <summary>Runtime model override. Null = use agent definition's ModelId, or global default.</summary>
    public string? ModelId { get; init; }

    /// <summary>Preferred agent type. Null = supervisor auto-selects.</summary>
    public string? PreferredAgent { get; init; }

    /// <summary>Source that triggered this request.</summary>
    public string TriggerType { get; init; } = "api";   // "api" | "scheduled" | "event" | "webhook"

    /// <summary>Extra metadata passed through to agents (e.g., event payload).</summary>
    public Dictionary<string, object?> Metadata { get; init; } = [];

    /// <summary>Caller-provided instructions (from supervisor or API). Appended to the agent's system prompt at runtime.</summary>
    public string? Instructions { get; init; }

    /// <summary>Runtime LLM config override (test mode). Null = use agent definition's LlmConfigId.</summary>
    public int? LlmConfigId { get; init; }

    /// <summary>
    /// Session ID of the parent session that spawned this request.
    /// Set by DispatchStage when a supervisor delegates to a worker agent so the trace
    /// database can link worker sessions back to the originating supervisor session.
    /// </summary>
    public string? ParentSessionId { get; init; }

    /// <summary>
    /// Condensed summary of the supervisor's conversation history, injected by DispatchStage
    /// when delegating to a worker agent. Gives the worker the user's previously stated
    /// context (e.g. collected parameters, prior answers) without replaying the full transcript.
    /// Null when the request originates directly from the API (not a supervisor delegation).
    /// </summary>
    public string? ConversationContext { get; init; }

    /// <summary>
    /// When true, forces SSO token forwarding to all HTTP/SSE MCP bindings for this request,
    /// regardless of each binding's individual PassSsoToken setting.
    /// Used by the agent test chat so the caller's Bearer token reaches MCP tool servers
    /// even when bindings were not explicitly configured with PassSsoToken = true.
    /// No-op when TenantContext.AccessToken is null (dev mode / API key auth).
    /// </summary>
    public bool ForwardSsoToMcp { get; init; }

    /// <summary>
    /// Images or documents to pass inline to the LLM for this turn only.
    /// Not persisted in session history — attachments apply to the current turn only.
    /// </summary>
    public IReadOnlyList<ContentPart> Attachments { get; init; } = [];
}
