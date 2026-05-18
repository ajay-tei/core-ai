using System.ComponentModel.DataAnnotations.Schema;

namespace Diva.Infrastructure.Data.Entities;

/// <summary>Root group entity — owned by master admin, groups multiple tenants together.</summary>
public class TenantGroupEntity
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Navigation
    public List<TenantGroupMemberEntity> Members { get; set; } = [];
    public List<GroupLlmConfigEntity> LlmConfigs { get; set; } = [];
}

/// <summary>Join table — which tenants belong to this group.</summary>
public class TenantGroupMemberEntity
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public int TenantId { get; set; }
    public DateTime JoinedAt { get; set; } = DateTime.UtcNow;

    public TenantGroupEntity Group { get; set; } = null!;
}

/// <summary>
/// Shared agent template — mirrors AgentDefinitionEntity fields but owned by a group.
/// NOT ITenantEntity: group resources are platform-level and not tenant-scoped.
/// Tenants in the group see these as read-only shared agents.
/// </summary>
public class GroupAgentTemplateEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public int GroupId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string AgentType { get; set; } = string.Empty;
    public string? SystemPrompt { get; set; }
    public string? ModelId { get; set; }
    public double Temperature { get; set; } = 0.7;
    public int MaxIterations { get; set; } = 10;
    public string? Capabilities { get; set; }
    public string? ToolBindings { get; set; }
    public string? VerificationMode { get; set; }
    public string? ContextWindowJson { get; set; }
    public string? CustomVariablesJson { get; set; }
    public int? MaxContinuations { get; set; }
    public int? MaxToolResultChars { get; set; }
    public int? MaxOutputTokens { get; set; }
    public bool? EnableHistoryCaching { get; set; }
    public string? PipelineStagesJson { get; set; }
    public string? ToolFilterJson { get; set; }
    public string? StageInstructionsJson { get; set; }
    /// <summary>ID of a GroupLlmConfigEntity or TenantLlmConfigEntity to use for this agent. null = use hierarchy default.</summary>
    public int? LlmConfigId { get; set; }
    // ── Phase-15 fields (mirrored from AgentDefinitionEntity) ─────────────────
    public string? ArchetypeId { get; set; }
    public string? HooksJson { get; set; }
    public string? A2AEndpoint { get; set; }
    public string? A2AAuthScheme { get; set; }
    public string? A2ASecretRef { get; set; }
    public string? A2ARemoteAgentId { get; set; }
    public string ExecutionMode { get; set; } = "Full";
    public string? ModelSwitchingJson { get; set; }
    // ──────────────────────────────────────────────────────────────────────────
    public bool IsEnabled { get; set; } = true;
    public string Status { get; set; } = "Published";
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public TenantGroupEntity Group { get; set; } = null!;
}

/// <summary>
/// Shared business rule — mirrors TenantBusinessRuleEntity but owned by a group.
/// Default Priority=50 so group rules apply before tenant rules (Priority=100).
/// </summary>
public class GroupBusinessRuleEntity
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public string AgentType { get; set; } = "*";
    public string RuleCategory { get; set; } = string.Empty;
    public string RuleKey { get; set; } = string.Empty;
    public string? RuleValueJson { get; set; }
    public string? PromptInjection { get; set; }
    public bool IsActive { get; set; } = true;
    public int Priority { get; set; } = 50;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    // ── Hook pipeline fields (mirror TenantBusinessRuleEntity) ────────────────
    public string HookPoint { get; set; } = "OnInit";
    public string HookRuleType { get; set; } = "inject_prompt";
    public string? Pattern { get; set; }
    public string? Replacement { get; set; }
    public string? ToolName { get; set; }
    public int OrderInPack { get; set; } = 0;
    public bool StopOnMatch { get; set; } = false;
    public int MaxEvaluationMs { get; set; } = 100;

    /// <summary>When true, this rule is offered as an opt-in template to member tenants rather than being auto-injected into all tenant prompts.</summary>
    public bool IsTemplate { get; set; } = false;

    public TenantGroupEntity Group { get; set; } = null!;
}

/// <summary>Shared prompt override — mirrors TenantPromptOverrideEntity but owned by a group.</summary>
public class GroupPromptOverrideEntity
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    public string AgentType { get; set; } = "*";
    public string Section { get; set; } = string.Empty;
    public string CustomText { get; set; } = string.Empty;
    public string MergeMode { get; set; } = "Append";
    public bool IsActive { get; set; } = true;
    /// <summary>When true, offered as opt-in template to member tenants rather than auto-injected.</summary>
    public bool IsTemplate { get; set; } = false;
    public int Version { get; set; } = 1;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public TenantGroupEntity Group { get; set; } = null!;
}

/// <summary>
/// Shared scheduled task — mirrors ScheduledTaskEntity but owned by a group.
/// Uses AgentType (not AgentId) — resolved to the first matching enabled agent per tenant at runtime.
/// Fires once per member tenant per schedule tick.
/// </summary>
public class GroupScheduledTaskEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public int GroupId { get; set; }

    /// <summary>Agent type to invoke. Resolved to first enabled agent of this type per tenant at runtime.</summary>
    public string AgentType { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string ScheduleType { get; set; } = "once";
    public DateTime? ScheduledAtUtc { get; set; }
    public string? RunAtTime { get; set; }
    public int? DayOfWeek { get; set; }
    public string TimeZoneId { get; set; } = "UTC";
    public string PayloadType { get; set; } = "prompt";
    public string PromptText { get; set; } = string.Empty;
    public string? ParametersJson { get; set; }
    public bool IsEnabled { get; set; } = true;
    public DateTime? LastRunAtUtc { get; set; }
    public DateTime? NextRunUtc { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    // ── Notification config ───────────────────────────────────────────────────
    public string? NotifyEmails { get; set; }
    public string? NotifyOn { get; set; }
    [Column("FailureKeywords")]
    public string? SuccessKeywords { get; set; }

    public TenantGroupEntity Group { get; set; } = null!;
}

/// <summary>Tracks a single execution of a GroupScheduledTaskEntity for one member tenant.</summary>
public class GroupScheduledTaskRunEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string GroupTaskId { get; set; } = string.Empty;
    public int TenantId { get; set; }
    public int GroupId { get; set; }
    public string Status { get; set; } = "pending";   // "pending"|"running"|"success"|"failed"
    public DateTime ScheduledForUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public long? DurationMs { get; set; }
    public string? ResponseText { get; set; }
    public string? ErrorMessage { get; set; }
    public string? SessionId { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int? IterationCount { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public GroupScheduledTaskEntity GroupTask { get; set; } = null!;
}

/// <summary>
/// Tenant-specific activation and customisation of a <see cref="GroupAgentTemplateEntity"/>.
/// Stores only the fields the tenant wishes to override — null means "use the template value".
/// Implements ITenantEntity so EF global query filters apply automatically.
/// One row per (TenantId, GroupTemplateId) — enforced by unique index.
/// </summary>
public class TenantGroupAgentOverlayEntity : ITenantEntity
{
    public int Id { get; set; }
    /// <summary>Stable external identifier returned in API responses. Never changes after creation.</summary>
    public string Guid { get; set; } = System.Guid.NewGuid().ToString();
    public int TenantId { get; set; }
    /// <summary>FK to GroupAgentTemplateEntity.Id (string PK).</summary>
    public string GroupTemplateId { get; set; } = string.Empty;
    /// <summary>Denormalized GroupId — avoids join on hot path in registry lookup.</summary>
    public int GroupId { get; set; }
    public bool IsEnabled { get; set; } = true;

    // ── Nullable overrides (null = use template value) ────────────────────────
    /// <summary>Appended to the template system prompt with a "## Tenant Addendum" header.</summary>
    public string? SystemPromptAddendum { get; set; }
    public string? ModelId { get; set; }
    public double? Temperature { get; set; }
    /// <summary>JSON string[] — merged with template tool bindings (union, no duplicates).</summary>
    public string? ExtraToolBindingsJson { get; set; }
    /// <summary>JSON Dict&lt;string,string&gt; — merged with template custom variables; overlay values win.</summary>
    public string? CustomVariablesJson { get; set; }
    public int? LlmConfigId { get; set; }
    public int? MaxOutputTokens { get; set; }

    public DateTime ActivatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? UpdatedAt { get; set; }

    public TenantGroupEntity Group { get; set; } = null!;
    public GroupAgentTemplateEntity Template { get; set; } = null!;
}

/// <summary>
/// Per-group LLM configuration — supports multiple named configs per group.
/// Either an own config (own credentials) or a reference to a platform config (PlatformConfigRef set).
/// </summary>
public class GroupLlmConfigEntity
{
    public int Id { get; set; }
    public int GroupId { get; set; }
    /// <summary>Required display name for this config entry, e.g. "OpenAI Production".</summary>
    public string? Name { get; set; }
    /// <summary>FK to PlatformLlmConfigs. When set, this entry is a reference alias — own credential fields are ignored and the platform config's credentials are used at runtime.</summary>
    public int? PlatformConfigRef { get; set; }
    public string? Provider { get; set; }
    public string? ApiKey { get; set; }
    public string? Model { get; set; }
    public string? Endpoint { get; set; }
    public string? DeploymentName { get; set; }
    public string? AvailableModelsJson { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public TenantGroupEntity Group { get; set; } = null!;
    public PlatformLlmConfigEntity? PlatformConfig { get; set; }
}
