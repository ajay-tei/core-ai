namespace Diva.Infrastructure.Data.Entities;

/// <summary>
/// Platform-wide named LLM configuration. Supports multiple named entries (e.g. "Anthropic Prod", "OpenAI Dev").
/// appsettings.json values are used only as seed on first startup if this table is empty.
/// </summary>
public class PlatformLlmConfigEntity
{
    public int Id { get; set; }
    /// <summary>Unique display name, e.g. "Anthropic Production".</summary>
    public string Name { get; set; } = "Default";
    public string Provider { get; set; } = "Anthropic";
    public string ApiKey { get; set; } = string.Empty;
    public string Model { get; set; } = "claude-sonnet-4-20250514";
    public string? Endpoint { get; set; }
    public string? DeploymentName { get; set; }
    public string? AvailableModelsJson { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Per-tenant LLM configuration — supports multiple named configs per tenant.
/// Name = null → the "default" (unnamed) config used by the platform→group→tenant hierarchy.
/// Name = "OpenAI Production" etc → named configs that individual agents can pin to via LlmConfigId.
/// Implements ITenantEntity so EF query filters apply.
/// </summary>
public class TenantLlmConfigEntity : ITenantEntity
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    /// <summary>null = default unnamed config; non-null = named config agents can pin to.</summary>
    public string? Name { get; set; }
    public string? Provider { get; set; }
    public string? ApiKey { get; set; }
    public string? Model { get; set; }
    public string? Endpoint { get; set; }
    public string? DeploymentName { get; set; }
    public string? AvailableModelsJson { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>FK to TenantEnvironmentEntity — which environment this named config's key applies
    /// to (Phase G). Null = untagged (resolves for any environment until tagged). Keys never travel
    /// with promotion — an agent promoted to a new environment must have its own tagged config.</summary>
    public int? EnvironmentId { get; set; }
}
