using System.Text.Json.Serialization;

namespace Diva.Rag.Abstractions;

/// <summary>
/// Per-agent knowledge retrieval profile. Stored as AgentDefinitionEntity.KnowledgeProfileJson.
/// </summary>
public sealed class AgentKnowledgeProfile
{
    [JsonPropertyName("autoInjectContext")]
    public bool AutoInjectContext { get; set; }

    [JsonPropertyName("autoQuery")]
    public bool AutoQuery { get; set; }

    [JsonPropertyName("includeAgentSources")]
    public bool IncludeAgentSources { get; set; } = true;

    [JsonPropertyName("includeGroupSources")]
    public bool IncludeGroupSources { get; set; } = true;

    [JsonPropertyName("includePlatformSources")]
    public bool IncludePlatformSources { get; set; } = true;

    [JsonPropertyName("domains")]
    public string[]? Domains { get; set; }

    [JsonPropertyName("products")]
    public string[]? Products { get; set; }

    [JsonPropertyName("modules")]
    public string[]? Modules { get; set; }

    [JsonPropertyName("contentTypes")]
    public string[]? ContentTypes { get; set; }

    [JsonPropertyName("securityLevels")]
    public string[]? SecurityLevels { get; set; }

    [JsonPropertyName("sourceTypes")]
    public string[]? SourceTypes { get; set; }

    [JsonPropertyName("maxResults")]
    public int MaxResults { get; set; } = 5;

    [JsonPropertyName("entityLinkHops")]
    public int EntityLinkHops { get; set; }

    // ── Agent Memory (Phase 26.2) ────────────────────────────────────────────

    [JsonPropertyName("enableMemory")]
    public bool EnableMemory { get; set; }

    [JsonPropertyName("memoryAutoRecall")]
    public bool MemoryAutoRecall { get; set; }

    [JsonPropertyName("memoryAutoRecallTypes")]
    public string[]? MemoryAutoRecallTypes { get; set; }

    [JsonPropertyName("memoryMaxRecallResults")]
    public int MemoryMaxRecallResults { get; set; } = 3;

    /// <summary>
    /// When true, saves an episodic checkpoint memory at the end of each successful execution.
    /// Combined with <see cref="MemoryAutoRecall"/>, this lets the agent resume from its last
    /// known state on the next invocation. Requires <see cref="EnableMemory"/> = true.
    /// </summary>
    [JsonPropertyName("saveTaskCheckpoint")]
    public bool SaveTaskCheckpoint { get; set; }
}
