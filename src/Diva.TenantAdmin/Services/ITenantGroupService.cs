using Diva.Infrastructure.Data.Entities;

namespace Diva.TenantAdmin.Services;

public interface ITenantGroupService
{
    // ── Groups ────────────────────────────────────────────────────────────────
    Task<List<TenantGroupEntity>> GetAllGroupsAsync(CancellationToken ct);
    Task<TenantGroupEntity?> GetGroupByIdAsync(int groupId, CancellationToken ct);
    Task<TenantGroupEntity> CreateGroupAsync(CreateGroupDto dto, CancellationToken ct);
    Task<TenantGroupEntity> UpdateGroupAsync(int groupId, UpdateGroupDto dto, CancellationToken ct);
    Task DeleteGroupAsync(int groupId, CancellationToken ct);

    // ── Members ───────────────────────────────────────────────────────────────
    Task<List<TenantGroupMemberEntity>> GetMembersAsync(int groupId, CancellationToken ct);
    Task<TenantGroupMemberEntity> AddMemberAsync(int groupId, int tenantId, CancellationToken ct);
    Task RemoveMemberAsync(int groupId, int tenantId, CancellationToken ct);

    // ── Agent templates ───────────────────────────────────────────────────────
    Task<List<GroupAgentTemplateEntity>> GetAgentTemplatesAsync(int groupId, CancellationToken ct);
    Task<GroupAgentTemplateEntity?> GetAgentTemplateAsync(int groupId, string templateId, CancellationToken ct);
    Task<GroupAgentTemplateEntity> CreateAgentTemplateAsync(int groupId, CreateGroupAgentDto dto, CancellationToken ct);
    Task<GroupAgentTemplateEntity> UpdateAgentTemplateAsync(int groupId, string templateId, UpdateGroupAgentDto dto, CancellationToken ct);
    Task DeleteAgentTemplateAsync(int groupId, string templateId, CancellationToken ct);

    // ── Business rules ────────────────────────────────────────────────────────
    Task<List<GroupBusinessRuleEntity>> GetBusinessRulesAsync(int groupId, CancellationToken ct);
    Task<GroupBusinessRuleEntity> CreateBusinessRuleAsync(int groupId, CreateGroupRuleDto dto, CancellationToken ct);
    Task<GroupBusinessRuleEntity> UpdateBusinessRuleAsync(int groupId, int ruleId, UpdateGroupRuleDto dto, CancellationToken ct);
    Task DeleteBusinessRuleAsync(int groupId, int ruleId, CancellationToken ct);

    // ── Prompt overrides ──────────────────────────────────────────────────────
    Task<List<GroupPromptOverrideEntity>> GetPromptOverridesAsync(int groupId, CancellationToken ct);
    Task<GroupPromptOverrideEntity> CreatePromptOverrideAsync(int groupId, CreateGroupPromptOverrideDto dto, CancellationToken ct);
    Task<GroupPromptOverrideEntity> UpdatePromptOverrideAsync(int groupId, int overrideId, UpdateGroupPromptOverrideDto dto, CancellationToken ct);
    Task DeletePromptOverrideAsync(int groupId, int overrideId, CancellationToken ct);

    // ── Scheduled tasks ───────────────────────────────────────────────────────
    Task<List<GroupScheduledTaskEntity>> GetScheduledTasksAsync(int groupId, CancellationToken ct);
    Task<GroupScheduledTaskEntity?> GetScheduledTaskAsync(int groupId, string taskId, CancellationToken ct);
    Task<GroupScheduledTaskEntity> CreateScheduledTaskAsync(int groupId, CreateGroupTaskDto dto, CancellationToken ct);
    Task<GroupScheduledTaskEntity> UpdateScheduledTaskAsync(int groupId, string taskId, UpdateGroupTaskDto dto, CancellationToken ct);
    Task DeleteScheduledTaskAsync(int groupId, string taskId, CancellationToken ct);
    Task SetScheduledTaskEnabledAsync(int groupId, string taskId, bool isEnabled, CancellationToken ct);

    // ── Group LLM config (default unnamed + named list) ───────────────────────
    /// <summary>Returns the unnamed default group config (Name=null). Backward-compat.</summary>
    Task<GroupLlmConfigEntity?> GetGroupLlmConfigAsync(int groupId, CancellationToken ct);
    /// <summary>Upserts the unnamed default group config (Name=null). Backward-compat.</summary>
    Task<GroupLlmConfigEntity> UpsertGroupLlmConfigAsync(int groupId, UpsertLlmConfigDto dto, CancellationToken ct);
    Task<List<GroupLlmConfigEntity>> ListGroupLlmConfigsAsync(int groupId, CancellationToken ct);
    Task<GroupLlmConfigEntity?> GetGroupLlmConfigByIdAsync(int configId, int groupId, CancellationToken ct);
    Task<GroupLlmConfigEntity> CreateGroupLlmConfigAsync(int groupId, CreateNamedLlmConfigDto dto, CancellationToken ct);
    Task<GroupLlmConfigEntity> UpdateGroupLlmConfigByIdAsync(int configId, int groupId, UpsertLlmConfigDto dto, CancellationToken ct);
    Task DeleteGroupLlmConfigByIdAsync(int configId, int groupId, CancellationToken ct);

    // ── Platform LLM config ───────────────────────────────────────────────────
    /// <summary>Returns the first platform config (backward-compat singleton accessor).</summary>
    Task<PlatformLlmConfigEntity?> GetPlatformLlmConfigAsync(CancellationToken ct);
    /// <summary>Upserts the first platform config (backward-compat singleton upsert).</summary>
    Task<PlatformLlmConfigEntity> UpsertPlatformLlmConfigAsync(UpsertLlmConfigDto dto, CancellationToken ct);
    Task<List<PlatformLlmConfigEntity>> ListPlatformLlmConfigsAsync(CancellationToken ct);
    Task<PlatformLlmConfigEntity> CreatePlatformLlmConfigAsync(CreatePlatformLlmConfigDto dto, CancellationToken ct);
    Task<PlatformLlmConfigEntity> UpdatePlatformLlmConfigAsync(int id, UpsertLlmConfigDto dto, CancellationToken ct);
    Task DeletePlatformLlmConfigAsync(int id, CancellationToken ct);
    /// <summary>Creates a group LLM config that references a platform config (no credential duplication).</summary>
    Task<GroupLlmConfigEntity> AddGroupPlatformRefAsync(int groupId, AddGroupPlatformRefDto dto, CancellationToken ct);

    // ── Tenant LLM config (default unnamed + named list) ─────────────────────
    /// <summary>Returns the unnamed default tenant config (Name=null). Backward-compat.</summary>
    Task<TenantLlmConfigEntity?> GetTenantLlmConfigAsync(int tenantId, CancellationToken ct);
    /// <summary>Upserts the unnamed default tenant config (Name=null). Backward-compat.</summary>
    Task<TenantLlmConfigEntity> UpsertTenantLlmConfigAsync(int tenantId, UpsertLlmConfigDto dto, CancellationToken ct);
    /// <summary>Deletes the unnamed default tenant config (Name=null). Backward-compat.</summary>
    Task DeleteTenantLlmConfigAsync(int tenantId, CancellationToken ct);
    Task<List<TenantLlmConfigEntity>> ListTenantLlmConfigsAsync(int tenantId, CancellationToken ct);
    Task<TenantLlmConfigEntity?> GetTenantLlmConfigByIdAsync(int configId, int tenantId, CancellationToken ct);
    Task<TenantLlmConfigEntity> CreateTenantLlmConfigAsync(int tenantId, CreateNamedLlmConfigDto dto, CancellationToken ct);
    Task<TenantLlmConfigEntity> UpdateTenantLlmConfigByIdAsync(int configId, int tenantId, UpsertLlmConfigDto dto, CancellationToken ct);
    Task DeleteTenantLlmConfigByIdAsync(int configId, int tenantId, CancellationToken ct);

    // ── Available configs for agent picker ────────────────────────────────────
    /// <summary>Returns all named LLM configs accessible to a tenant (own + group-level) for the agent builder picker.</summary>
    Task<List<AvailableLlmConfigDto>> ListAvailableLlmConfigsForTenantAsync(int tenantId, CancellationToken ct);

    // ── Runtime helpers ───────────────────────────────────────────────────────
    Task<List<GroupBusinessRuleEntity>> GetActiveRulesForTenantAsync(int tenantId, string agentType, CancellationToken ct);
    Task<List<GroupPromptOverrideEntity>> GetActiveOverridesForTenantAsync(int tenantId, string agentType, CancellationToken ct);
    Task<List<GroupAgentTemplateEntity>> GetAgentTemplatesForTenantAsync(int tenantId, CancellationToken ct);
    Task<List<GroupScheduledTaskEntity>> GetDueGroupTasksAsync(DateTime utcNow, CancellationToken ct);
    /// <summary>Returns all TenantIds that are members of the specified group. Used for cache propagation.</summary>
    Task<List<int>> GetMemberTenantIdsAsync(int groupId, CancellationToken ct);
    void InvalidateGroupCache(int groupId, string? agentType = null);

    // ── Group prompt templates (opt-in, tenant-activate) ───────────────────────────
    Task<List<GroupPromptTemplateDto>> GetAvailableGroupPromptTemplatesAsync(int tenantId, CancellationToken ct);
    Task<TenantPromptOverrideEntity> ActivateGroupPromptTemplateAsync(int tenantId, int groupOverrideId, CancellationToken ct);
    Task DeactivateGroupPromptTemplateAsync(int tenantId, int groupOverrideId, CancellationToken ct);
}

// ── DTOs ──────────────────────────────────────────────────────────────────────

public record CreateGroupDto(string Name, string? Description);
public record UpdateGroupDto(string Name, string? Description, bool IsActive);

public record GroupPromptTemplateDto(
    int Id, int GroupId, string GroupName, string AgentType,
    string Section, string CustomText, string MergeMode,
    bool IsActivated, int? ActivatedOverrideId);

public record CreateGroupAgentDto(
    string Name, string DisplayName, string Description, string AgentType,
    string? SystemPrompt, string? ModelId, double Temperature, int MaxIterations,
    string? Capabilities, string? ToolBindings, string? VerificationMode,
    string? ContextWindowJson, string? CustomVariablesJson,
    int? MaxContinuations, int? MaxToolResultChars, int? MaxOutputTokens,
    string? PipelineStagesJson, string? ToolFilterJson, string? StageInstructionsJson,
    bool IsEnabled, string Status = "Published",
    // Phase-15 fields
    string? ArchetypeId = null, string? HooksJson = null,
    string? A2AEndpoint = null, string? A2AAuthScheme = null, string? A2ASecretRef = null,
    string ExecutionMode = "Full", string? ModelSwitchingJson = null,
    int? LlmConfigId = null, bool? EnableHistoryCaching = null);

public record UpdateGroupAgentDto(
    string Name, string DisplayName, string Description,
    string? SystemPrompt, string? ModelId, double Temperature, int MaxIterations,
    string? Capabilities, string? ToolBindings, string? VerificationMode,
    string? ContextWindowJson, string? CustomVariablesJson,
    int? MaxContinuations, int? MaxToolResultChars, int? MaxOutputTokens,
    string? PipelineStagesJson, string? ToolFilterJson, string? StageInstructionsJson,
    bool IsEnabled, string Status = "Published",
    // Phase-15 fields
    string? ArchetypeId = null, string? HooksJson = null,
    string? A2AEndpoint = null, string? A2AAuthScheme = null, string? A2ASecretRef = null,
    string ExecutionMode = "Full", string? ModelSwitchingJson = null,
    int? LlmConfigId = null, bool? EnableHistoryCaching = null);

public record CreateGroupRuleDto(
    string AgentType, string RuleCategory, string RuleKey,
    string? PromptInjection, string? RuleValueJson = null, int Priority = 50,
    bool IsTemplate = false,
    string HookPoint = "OnInit", string HookRuleType = "inject_prompt",
    string? Pattern = null, string? Replacement = null, string? ToolName = null,
    int OrderInPack = 0, bool StopOnMatch = false, int MaxEvaluationMs = 100);

public record UpdateGroupRuleDto(
    string RuleCategory, string RuleKey, string? PromptInjection,
    string? RuleValueJson, bool IsActive, int Priority, bool IsTemplate = false,
    string HookPoint = "OnInit", string HookRuleType = "inject_prompt",
    string? Pattern = null, string? Replacement = null, string? ToolName = null,
    int OrderInPack = 0, bool StopOnMatch = false, int MaxEvaluationMs = 100);

public record CreateGroupPromptOverrideDto(
    string AgentType, string Section, string CustomText, string MergeMode = "Append", bool IsTemplate = false);

public record UpdateGroupPromptOverrideDto(string CustomText, string MergeMode, bool? IsActive = null, bool IsTemplate = false);

public record CreateGroupTaskDto(
    string AgentType, string Name, string? Description,
    string ScheduleType, DateTime? ScheduledAtUtc, string? RunAtTime, int? DayOfWeek,
    string TimeZoneId, string PayloadType, string PromptText, string? ParametersJson,
    bool IsEnabled);

public record UpdateGroupTaskDto(
    string? AgentType, string? Name, string? Description,
    string? ScheduleType, DateTime? ScheduledAtUtc, string? RunAtTime, int? DayOfWeek,
    string? TimeZoneId, string? PayloadType, string? PromptText, string? ParametersJson,
    bool? IsEnabled);

public record UpsertLlmConfigDto(
    string? Provider, string? ApiKey, string? Model,
    string? Endpoint, string? DeploymentName, string? AvailableModelsJson,
    int? EnvironmentId = null);

public record CreateNamedLlmConfigDto(
    string? Name, string? Provider, string? ApiKey, string? Model,
    string? Endpoint, string? DeploymentName, string? AvailableModelsJson,
    int? EnvironmentId = null);

public record CreatePlatformLlmConfigDto(
    string Name, string? Provider, string? ApiKey, string? Model,
    string? Endpoint, string? DeploymentName, string? AvailableModelsJson);

public record AddGroupPlatformRefDto(int PlatformConfigId, string? NameOverride = null);

/// <summary>Lightweight summary for the agent-builder LLM config picker.</summary>
public record AvailableLlmConfigDto(
    int Id, string Source, string? Name, string DisplayName,
    string? Provider, string? Model, IReadOnlyList<string> AvailableModels,
    bool IsRef = false, string? RefSource = null);
