using System.Text.Json;
using Diva.Core.Configuration;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Diva.Infrastructure.LiteLLM;

/// <summary>
/// Resolves a tenant's shared <see cref="TenantMcpServerEntity"/> references into concrete
/// <see cref="McpToolBinding"/> instances, selecting each server's credential dynamically based
/// on the platform API key used to invoke the agent. JWT/SSO callers (no API key) fall back to the
/// server's default credential, or to SSO passthrough when no credential applies.
/// </summary>
public interface IMcpCredentialSelector
{
    /// <summary>
    /// Builds tool bindings for the given shared-server names. Unknown names are skipped (logged).
    /// </summary>
    /// <param name="tenantId">Owning tenant.</param>
    /// <param name="platformApiKeyId">DB id of the invoking platform API key, or null for JWT/SSO callers.</param>
    /// <param name="serverNames">Shared server names referenced by the agent.</param>
    Task<List<McpToolBinding>> ResolveBindingsAsync(
        int tenantId, int? platformApiKeyId, IEnumerable<string> serverNames, CancellationToken ct);
}

/// <inheritdoc />
public sealed class McpCredentialSelector : IMcpCredentialSelector
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly IDatabaseProviderFactory _db;
    private readonly ILogger<McpCredentialSelector> _logger;

    public McpCredentialSelector(IDatabaseProviderFactory db, ILogger<McpCredentialSelector> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<List<McpToolBinding>> ResolveBindingsAsync(
        int tenantId, int? platformApiKeyId, IEnumerable<string> serverNames, CancellationToken ct)
    {
        var names = serverNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<McpToolBinding>();
        if (names.Count == 0) return result;

        using var db = _db.CreateDbContext(Core.Models.TenantContext.System(tenantId));
        var servers = await db.TenantMcpServers
            .Where(s => s.TenantId == tenantId && names.Contains(s.Name))
            .AsNoTracking()
            .ToListAsync(ct);

        var found = servers.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var missing in names.Where(n => !found.Contains(n)))
            _logger.LogWarning("Shared MCP server '{Name}' referenced by an agent does not exist for tenant {TenantId}", missing, tenantId);

        foreach (var server in servers)
        {
            var credentialRef = SelectCredentialRef(server, platformApiKeyId);
            result.Add(new McpToolBinding
            {
                Name = server.Name,
                Transport = string.IsNullOrWhiteSpace(server.Transport) ? "stdio" : server.Transport,
                Command = server.Command ?? string.Empty,
                Args = ParseStringList(server.ArgsJson),
                Env = ParseStringDict(server.EnvJson),
                Endpoint = server.Endpoint ?? string.Empty,
                PassSsoToken = server.PassSsoToken,
                PassTenantHeaders = server.PassTenantHeaders,
                CredentialRef = string.IsNullOrWhiteSpace(credentialRef) ? null : credentialRef,
            });

            _logger.LogDebug(
                "Shared MCP server '{Name}' resolved for tenant {TenantId} (apiKeyId={ApiKeyId}, credentialRef={Ref})",
                server.Name, tenantId, platformApiKeyId, credentialRef ?? "<none/SSO>");
        }

        return result;
    }

    private string? SelectCredentialRef(TenantMcpServerEntity server, int? platformApiKeyId)
    {
        if (platformApiKeyId is int keyId && !string.IsNullOrWhiteSpace(server.ApiKeyCredentialMappingsJson))
        {
            try
            {
                var mappings = JsonSerializer.Deserialize<List<ApiKeyCredentialMapping>>(
                    server.ApiKeyCredentialMappingsJson!, JsonOpts);
                var match = mappings?.FirstOrDefault(m => m.ApiKeyId == keyId);
                if (match is not null && !string.IsNullOrWhiteSpace(match.CredentialRef))
                    return match.CredentialRef;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Shared MCP server '{Name}': ApiKeyCredentialMappingsJson is malformed — falling back to default credential",
                    server.Name);
            }
        }

        // No per-key match → default credential (may be null → SSO passthrough / no credential).
        return server.DefaultCredentialRef;
    }

    private static List<string> ParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json, JsonOpts) ?? []; }
        catch { return []; }
    }

    private static Dictionary<string, string> ParseStringDict(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<Dictionary<string, string>>(json, JsonOpts) ?? []; }
        catch { return []; }
    }
}
