namespace Diva.Core.Models;

/// <summary>
/// Portable, tenant-neutral agent export bundle.
/// Contains everything needed to recreate an agent (definition + linked rules)
/// on any Diva tenant. All identity fields (Id, TenantId, CreatedAt) are stripped.
/// </summary>
public sealed record AgentExportBundle
{
    /// <summary>Schema version for forward-compatibility checks on import.</summary>
    public string SchemaVersion { get; init; } = "1.0";

    public DateTime ExportedAt { get; init; } = DateTime.UtcNow;

    /// <summary>Informational only — not used during import.</summary>
    public int SourceTenantId { get; init; }

    public AgentExportDefinition Agent { get; init; } = null!;

    public IReadOnlyList<AgentExportRule> Rules { get; init; } = [];
}

/// <summary>
/// Portable agent definition — mirrors AgentDefinitionEntity without identity/audit fields.
/// DelegateAgentNames carries resolved peer-agent names so they can be re-linked by name on import.
/// </summary>
public sealed record AgentExportDefinition
{
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string AgentType { get; init; } = string.Empty;
    public string? SystemPrompt { get; init; }
    public string? ModelId { get; init; }
    public double Temperature { get; init; } = 0.7;
    public int MaxIterations { get; init; } = 10;
    public string? Capabilities { get; init; }
    public string? ToolBindings { get; init; }
    public string? VerificationMode { get; init; }
    public string? ContextWindowJson { get; init; }
    public string? OptimizationOverrideJson { get; init; }
    public string? CustomVariablesJson { get; init; }
    public int? MaxContinuations { get; init; }
    public int? MaxToolResultChars { get; init; }
    public int? MaxOutputTokens { get; init; }
    public bool? EnableHistoryCaching { get; init; }
    public string? PipelineStagesJson { get; init; }
    public string? ToolFilterJson { get; init; }
    public string? StageInstructionsJson { get; init; }
    public string? ArchetypeId { get; init; }
    public string? HooksJson { get; init; }
    public string? A2AEndpoint { get; init; }
    public string? A2AAuthScheme { get; init; }
    public string? A2ASecretRef { get; init; }
    public string? A2ARemoteAgentId { get; init; }
    public string ExecutionMode { get; init; } = "Full";
    public string? ModelSwitchingJson { get; init; }
    public bool IsEnabled { get; init; } = true;
    public string Status { get; init; } = "Draft";

    /// <summary>
    /// Resolved display names of peer delegation agents (from DelegateAgentIdsJson).
    /// Used to re-link agents by name on import — IDs are not portable across tenants.
    /// </summary>
    public IReadOnlyList<string> DelegateAgentNames { get; init; } = [];
}

/// <summary>
/// Portable business rule — mirrors TenantBusinessRuleEntity without identity/FK fields.
/// RulePackId is stripped because pack IDs are not portable; imported rules become standalone.
/// </summary>
public sealed record AgentExportRule
{
    public string AgentType { get; init; } = "*";
    public string RuleCategory { get; init; } = string.Empty;
    public string RuleKey { get; init; } = string.Empty;
    public string? RuleValueJson { get; init; }
    public string? PromptInjection { get; init; }
    public bool IsActive { get; init; } = true;
    public int Priority { get; init; } = 100;
    public string HookPoint { get; init; } = "OnInit";
    public string HookRuleType { get; init; } = "inject_prompt";
    public string? Pattern { get; init; }
    public string? Replacement { get; init; }
    public string? ToolName { get; init; }
    public int OrderInPack { get; init; }
    public bool StopOnMatch { get; init; }
    public int MaxEvaluationMs { get; init; } = 100;
}

/// <summary>Controls behaviour during import.</summary>
public sealed record AgentImportOptions
{
    /// <summary>When true and an agent with the same Name already exists, overwrite it (bumps Version).</summary>
    public bool OverwriteExisting { get; init; } = false;

    /// <summary>When true (default), linked business rules are also imported.</summary>
    public bool ImportRules { get; init; } = true;

    /// <summary>Override the imported agent name. Null = use the name from the bundle.</summary>
    public string? NewAgentName { get; init; }
}

/// <summary>Result returned after a successful import.</summary>
public sealed record AgentImportResult
{
    public string AgentId { get; init; } = string.Empty;
    public string AgentName { get; init; } = string.Empty;
    public int RulesImported { get; init; }

    /// <summary>Non-fatal warnings, e.g. delegate agent names that could not be resolved.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
