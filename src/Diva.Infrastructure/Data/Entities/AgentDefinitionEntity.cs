namespace Diva.Infrastructure.Data.Entities;

using System.Text.Json.Serialization;

public class AgentDefinitionEntity : ITenantEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public int TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string AgentType { get; set; } = string.Empty;
    public string? SystemPrompt { get; set; }
    public string? ModelId { get; set; }                   // null = use global default from LlmOptions
    /// <summary>ID of a named TenantLlmConfigEntity or GroupLlmConfigEntity. null = use platform→group→tenant hierarchy.</summary>
    public int? LlmConfigId { get; set; }
    public double Temperature { get; set; } = 0.7;
    public int MaxIterations { get; set; } = 10;
    public string? Capabilities { get; set; }              // JSON array of capability strings
    public string? ToolBindings { get; set; }              // JSON array of tool names
    public string? VerificationMode { get; set; }          // null = use global default; Off|ToolGrounded|LlmVerifier|Strict|Auto
    public string? ContextWindowJson { get; set; }         // null = use global defaults; JSON ContextWindowOverrideOptions e.g. {"MaxHistoryTurns":5}
    public string? OptimizationOverrideJson { get; set; } // null = use global defaults; JSON OptimizationOverrideOptions e.g. {"MergeMaxTokens":16384}
    public string? CustomVariablesJson { get; set; }       // null = no custom variables; JSON Dictionary<string,string> e.g. {"company_name":"Acme Corp"}
    public int? MaxContinuations { get; set; }              // null = use global AgentOptions.MaxContinuations
    public int? MaxToolResultChars { get; set; }            // null = use global AgentOptions.MaxToolResultChars
    public int? MaxOutputTokens { get; set; }               // null = use global AgentOptions.MaxOutputTokens
    /// <summary>Override Anthropic prompt-caching per agent. null = use global AgentOptions.EnableHistoryCaching (default true).</summary>
    public bool? EnableHistoryCaching { get; set; }         // null = use global AgentOptions.EnableHistoryCaching
    public string? PipelineStagesJson { get; set; }         // null = all stages enabled; JSON {"Decompose":true,"Verify":false,...}
    public string? ToolFilterJson { get; set; }             // null = all tools allowed; JSON {"mode":"allow","tools":["tool1"]}
    public string? StageInstructionsJson { get; set; }      // null = no per-stage instructions; JSON {"Decompose":"...","Integrate":"..."}
    public bool IsEnabled { get; set; } = true;
    public string Status { get; set; } = "Draft";          // "Draft" | "Published"
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? PublishedAt { get; set; }

    // ── Phase 15: Custom Agent Framework ──────────────────────────────────────

    /// <summary>Archetype ID (e.g. "rag", "code-analyst"). Null = "general".</summary>
    public string? ArchetypeId { get; set; }

    /// <summary>JSON dictionary of hook point → hook class name. Merged with archetype defaults at runtime.</summary>
    public string? HooksJson { get; set; }

    /// <summary>A2A endpoint URL for remote agents. When set, execution delegates via A2A client.</summary>
    [JsonPropertyName("a2aEndpoint")]
    public string? A2AEndpoint { get; set; }

    /// <summary>A2A auth scheme: Bearer | ApiKey. Used when calling remote agents.</summary>
    [JsonPropertyName("a2aAuthScheme")]
    public string? A2AAuthScheme { get; set; }

    /// <summary>Secret reference for A2A auth. Resolved from secure storage at runtime.</summary>
    [JsonPropertyName("a2aSecretRef")]
    public string? A2ASecretRef { get; set; }

    /// <summary>ID of the specific agent on the remote Diva instance to invoke via A2A. Appended as ?agentId= to /tasks/send.</summary>
    [JsonPropertyName("a2aRemoteAgentId")]
    public string? A2ARemoteAgentId { get; set; }

    /// <summary>Execution mode: Full (default), ChatOnly, ReadOnly, Supervised. Controls tool availability at runtime.</summary>
    public string ExecutionMode { get; set; } = "Full";

    /// <summary>
    /// JSON ModelSwitchingOptions for per-iteration model switching.
    /// null = no switching; use the agent's primary LlmConfig for all iterations.
    /// e.g. {"ToolIterationLlmConfigId":3,"FinalResponseLlmConfigId":1}
    /// </summary>
    public string? ModelSwitchingJson { get; set; }

    /// <summary>
    /// JSON array of agent IDs this agent can delegate to as peer tools.
    /// e.g. ["agent-1","agent-2"]. null = no delegation.
    /// </summary>
    public string? DelegateAgentIdsJson { get; set; }

    /// <summary>
    /// JSON string[] of shared <see cref="TenantMcpServerEntity.Name"/> values this agent references.
    /// e.g. ["weather","reservations"]. null = none. These are merged with the inline
    /// <see cref="ToolBindings"/> at runtime, with credentials selected per invoking API key.
    /// </summary>
    public string? McpServerRefsJson { get; set; }
}
