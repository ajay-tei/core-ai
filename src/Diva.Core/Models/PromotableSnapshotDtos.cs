namespace Diva.Core.Models;

/// <summary>
/// Portable MCP server snapshot — mirrors TenantMcpServerEntity's portable fields only.
/// ApiKeyCredentialMappingsJson and user-group credential mappings are excluded: they reference
/// tenant-specific PlatformApiKeyId/UserGroup rows that aren't portable across environments
/// (same rationale as AgentExportDefinition excluding LlmConfigId).
/// </summary>
public sealed record McpServerSnapshot
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Transport { get; init; } = "stdio";
    public string? Command { get; init; }
    public string? ArgsJson { get; init; }
    public string? EnvJson { get; init; }
    public string? Endpoint { get; init; }
    public bool PassSsoToken { get; init; }
    public bool PassTenantHeaders { get; init; }
    public string? DefaultCredentialRef { get; init; }
}

/// <summary>
/// Portable scheduled task snapshot — mirrors ScheduledTaskEntity's portable fields only.
/// AgentName carries the resolved target agent's display name (re-linked by name on materialize,
/// same pattern as AgentExportDefinition.DelegateAgentNames). RunAsUserId/Email/Label are excluded:
/// they reference a specific tenant's user identity and aren't portable across environments.
/// </summary>
public sealed record ScheduledTaskSnapshot
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string AgentName { get; init; } = string.Empty;
    public string ScheduleType { get; init; } = "once";
    public DateTime? ScheduledAtUtc { get; init; }
    public string? RunAtTime { get; init; }
    public int? DayOfWeek { get; init; }
    public string TimeZoneId { get; init; } = "UTC";
    public string PayloadType { get; init; } = "prompt";
    public string PromptText { get; init; } = string.Empty;
    public string? ParametersJson { get; init; }
    public bool IsEnabled { get; init; } = true;
    public string? NotifyEmails { get; init; }
    public string? NotifyOn { get; init; }
    public string? SuccessKeywords { get; init; }
}

/// <summary>
/// Portable agent group snapshot — mirrors AgentGroupEntity's portable fields only.
/// AgentNames carries resolved member-agent display names (re-linked by name on materialize).
/// AllowedUserIdsJson/UserGroupLinks are excluded: user identities and user groups are
/// tenant-specific, not portable across environments in a generically meaningful way.
/// </summary>
public sealed record AgentGroupSnapshot
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string? AllowedRolesJson { get; init; }
    public IReadOnlyList<string> AgentNames { get; init; } = [];
}
