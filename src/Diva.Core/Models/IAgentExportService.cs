namespace Diva.Core.Models;

/// <summary>
/// Exports and imports full agent configuration bundles (agent definition + linked business rules).
/// </summary>
public interface IAgentExportService
{
    /// <summary>
    /// Exports the agent with the given <paramref name="agentId"/> and all business rules
    /// linked to it into a portable <see cref="AgentExportBundle"/>.
    /// </summary>
    Task<AgentExportBundle> ExportAsync(
        string agentId,
        TenantContext tenant,
        CancellationToken ct);

    /// <summary>
    /// Imports an <see cref="AgentExportBundle"/> into the given tenant.
    /// Returns a result containing the new (or updated) agent ID, name, rule count, and any warnings.
    /// </summary>
    Task<AgentImportResult> ImportAsync(
        AgentExportBundle bundle,
        TenantContext tenant,
        AgentImportOptions options,
        CancellationToken ct);
}
