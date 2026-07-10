using System.Text.Json;
using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Diva.Infrastructure.LiteLLM;

/// <summary>
/// Resolves a tenant's shared <see cref="TenantMcpServerEntity"/> references into concrete
/// <see cref="McpToolBinding"/> instances, selecting each server's credential dynamically for the
/// invoking caller. Precedence: per-platform-API-key mapping → per-user-group credential →
/// server default → SSO passthrough (default credential null + PassSsoToken).
/// </summary>
public interface IMcpCredentialSelector
{
    /// <summary>
    /// Builds tool bindings for the given shared-server names. Unknown names are skipped (logged).
    /// The caller's <see cref="TenantContext"/> drives credential selection (platform API key,
    /// user-group membership, SSO identity).
    /// </summary>
    Task<List<McpToolBinding>> ResolveBindingsAsync(
        TenantContext tenant, IEnumerable<string> serverNames, CancellationToken ct);

    /// <summary>
    /// Returns the user groups the caller may pick from to drive shared-MCP credential selection
    /// for the given servers: groups the caller belongs to that also map a credential for at least
    /// one of the servers. When <paramref name="restrictToUserGroupIds"/> is non-empty (the agent's
    /// access group restricts by user group), the result is intersected with it. Ordered by group id.
    /// </summary>
    Task<List<CredentialGroupOption>> GetSelectableGroupsAsync(
        TenantContext tenant,
        IEnumerable<string> serverNames,
        IReadOnlyCollection<int>? restrictToUserGroupIds,
        CancellationToken ct);
}

/// <summary>A user group the caller can select to drive shared-MCP credential resolution.</summary>
public sealed record CredentialGroupOption(int Id, string Name);

/// <inheritdoc />
public sealed class McpCredentialSelector : IMcpCredentialSelector
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly IDatabaseProviderFactory _db;
    private readonly IUserGroupResolver _userGroups;
    private readonly ICredentialEncryptor? _encryptor;
    private readonly ILogger<McpCredentialSelector> _logger;

    public McpCredentialSelector(
        IDatabaseProviderFactory db,
        IUserGroupResolver userGroups,
        ILogger<McpCredentialSelector> logger,
        ICredentialEncryptor? encryptor = null)
    {
        _db = db;
        _userGroups = userGroups;
        _encryptor = encryptor;
        _logger = logger;
    }

    public async Task<List<McpToolBinding>> ResolveBindingsAsync(
        TenantContext tenant, IEnumerable<string> serverNames, CancellationToken ct)
    {
        var tenantId = tenant.TenantId;
        var platformApiKeyId = tenant.PlatformApiKeyId;
        var preferredGroupId = tenant.PreferredUserGroupId;

        var names = serverNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<McpToolBinding>();
        if (names.Count == 0) return result;

        using var db = _db.CreateDbContext(TenantContext.System(tenantId));
        var servers = await db.TenantMcpServers
            .Where(s => s.TenantId == tenantId && names.Contains(s.Name))
            .AsNoTracking()
            .ToListAsync(ct);

        var found = servers.Select(s => s.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var missing in names.Where(n => !found.Contains(n)))
            _logger.LogWarning("Shared MCP server '{Name}' referenced by an agent does not exist for tenant {TenantId}", missing, tenantId);

        // Load per-user-group credential rows for these servers in one round-trip.
        var serverIds = servers.Select(s => s.Id).ToList();
        var groupCredentials = await db.McpServerUserGroupCredentials
            .Where(c => c.TenantId == tenantId && serverIds.Contains(c.McpServerId))
            .Select(c => new GroupCred(c.McpServerId, c.UserGroupId, c.CredentialRef))
            .AsNoTracking()
            .ToListAsync(ct);

        // Only resolve the caller's group ids if there is at least one group credential to match.
        HashSet<int> userGroupIds = new();
        if (groupCredentials.Count > 0)
            userGroupIds = (await _userGroups.GetGroupIdsForUserAsync(tenant, ct)).ToHashSet();

        // serverId → ascending-by-user-group-id credential rows restricted to the caller's groups.
        var groupCredByServer = groupCredentials
            .Where(c => userGroupIds.Contains(c.UserGroupId) && !string.IsNullOrWhiteSpace(c.CredentialRef))
            .GroupBy(c => c.McpServerId)
            .ToDictionary(
                g => g.Key,
                g => g.OrderBy(c => c.UserGroupId).ToList());

        // Compute the credential decision (ref + source) for each server first, so we can batch-load
        // the encrypted keys needed to log a masked tail for verification.
        var decisions = new Dictionary<int, CredDecision>();
        foreach (var server in servers)
        {
            var decision = SelectCredential(server, platformApiKeyId, preferredGroupId, groupCredByServer, tenantId);
            decisions[server.Id] = decision;
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
                CredentialRef = string.IsNullOrWhiteSpace(decision.CredentialRef) ? null : decision.CredentialRef,
            });
        }

        // Batch-load encrypted keys for the chosen credential names to derive a masked tail.
        var chosenRefs = decisions.Values
            .Select(d => d.CredentialRef)
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        Dictionary<string, string> encByName = new(StringComparer.OrdinalIgnoreCase);
        if (chosenRefs.Count > 0 && _encryptor is not null)
        {
            encByName = await db.McpCredentials
                .Where(c => c.TenantId == tenantId && chosenRefs.Contains(c.Name))
                .Select(c => new { c.Name, c.EncryptedApiKey })
                .AsNoTracking()
                .ToDictionaryAsync(c => c.Name, c => c.EncryptedApiKey, StringComparer.OrdinalIgnoreCase, ct);
        }

        foreach (var server in servers)
        {
            var decision = decisions[server.Id];
            var source = decision.Source switch
            {
                CredSource.ApiKey => $"platform API key {decision.ApiKeyId}",
                CredSource.UserGroup => $"user-group {decision.UserGroupId}",
                CredSource.ServerDefault => "server default",
                _ => "SSO passthrough / tenant headers",
            };

            if (decision.Source == CredSource.SsoPassthrough)
            {
                _logger.LogInformation(
                    "Shared MCP server '{Name}' (tenant {TenantId}): no stored credential — {Source}",
                    server.Name, tenantId, source);
            }
            else
            {
                _logger.LogInformation(
                    "Shared MCP server '{Name}' (tenant {TenantId}): credential '{Ref}' selected via {Source}{Tail}",
                    server.Name, tenantId, decision.CredentialRef, source,
                    MaskTail(decision.CredentialRef, encByName));
            }
        }

        return result;
    }

    public async Task<List<CredentialGroupOption>> GetSelectableGroupsAsync(
        TenantContext tenant,
        IEnumerable<string> serverNames,
        IReadOnlyCollection<int>? restrictToUserGroupIds,
        CancellationToken ct)
    {
        var tenantId = tenant.TenantId;
        var names = serverNames
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (names.Count == 0) return [];

        using var db = _db.CreateDbContext(TenantContext.System(tenantId));

        var serverIds = await db.TenantMcpServers
            .Where(s => s.TenantId == tenantId && names.Contains(s.Name))
            .Select(s => s.Id)
            .ToListAsync(ct);
        if (serverIds.Count == 0) return [];

        // User-group ids that map a credential for at least one of these servers.
        var mappedGroupIds = await db.McpServerUserGroupCredentials
            .Where(c => c.TenantId == tenantId
                && serverIds.Contains(c.McpServerId)
                && c.CredentialRef != null && c.CredentialRef != "")
            .Select(c => c.UserGroupId)
            .Distinct()
            .ToListAsync(ct);
        if (mappedGroupIds.Count == 0) return [];

        // Restrict to the caller's own groups.
        var userGroupIds = (await _userGroups.GetGroupIdsForUserAsync(tenant, ct)).ToHashSet();
        var eligible = mappedGroupIds.Where(userGroupIds.Contains);

        // Optionally restrict to the agent access group's allowed user groups.
        if (restrictToUserGroupIds is { Count: > 0 })
            eligible = eligible.Where(restrictToUserGroupIds.Contains);

        var eligibleSet = eligible.ToHashSet();
        if (eligibleSet.Count == 0) return [];

        return await db.UserGroups
            .Where(g => g.TenantId == tenantId && eligibleSet.Contains(g.Id))
            .OrderBy(g => g.Id)
            .Select(g => new CredentialGroupOption(g.Id, g.Name))
            .AsNoTracking()
            .ToListAsync(ct);
    }

    private CredDecision SelectCredential(
        TenantMcpServerEntity server,
        int? platformApiKeyId,
        int? preferredUserGroupId,
        IReadOnlyDictionary<int, List<GroupCred>> groupCredByServer,
        int tenantId)
    {
        // 1. Per-platform-API-key mapping wins.
        if (platformApiKeyId is int keyId && !string.IsNullOrWhiteSpace(server.ApiKeyCredentialMappingsJson))
        {
            try
            {
                var mappings = JsonSerializer.Deserialize<List<ApiKeyCredentialMapping>>(
                    server.ApiKeyCredentialMappingsJson!, JsonOpts);
                var match = mappings?.FirstOrDefault(m => m.ApiKeyId == keyId);
                if (match is not null && !string.IsNullOrWhiteSpace(match.CredentialRef))
                    return new CredDecision(match.CredentialRef, CredSource.ApiKey, null, keyId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Shared MCP server '{Name}': ApiKeyCredentialMappingsJson is malformed — falling back to lower-precedence credential",
                    server.Name);
            }
        }

        // 2. Per-user-group credential. When the caller explicitly picked a group (and it maps a
        //    credential for this server), that choice wins. Otherwise, if the caller belongs to
        //    multiple credential-mapped groups, the lowest UserGroupId (oldest group) wins.
        if (groupCredByServer.TryGetValue(server.Id, out var groupCreds) && groupCreds.Count > 0)
        {
            if (preferredUserGroupId is int pref &&
                groupCreds.FirstOrDefault(c => c.UserGroupId == pref) is { } picked)
            {
                _logger.LogInformation(
                    "Shared MCP server '{Name}' (tenant {TenantId}): using caller-selected user-group {GroupId} credential ('{Ref}').",
                    server.Name, tenantId, picked.UserGroupId, picked.CredentialRef);
                return new CredDecision(picked.CredentialRef, CredSource.UserGroup, picked.UserGroupId, null);
            }

            var chosen = groupCreds[0];   // already ordered ascending by UserGroupId
            if (groupCreds.Count > 1)
            {
                _logger.LogWarning(
                    "Shared MCP server '{Name}' (tenant {TenantId}): caller matches {Count} user groups with credential mappings ({Groups}). "
                    + "Using credential from lowest user-group id {GroupId} ('{Ref}').",
                    server.Name, tenantId, groupCreds.Count,
                    string.Join(", ", groupCreds.Select(c => c.UserGroupId)),
                    chosen.UserGroupId, chosen.CredentialRef);
            }
            return new CredDecision(chosen.CredentialRef, CredSource.UserGroup, chosen.UserGroupId, null);
        }

        // 3. Default credential (may be null → SSO passthrough / no credential).
        return string.IsNullOrWhiteSpace(server.DefaultCredentialRef)
            ? new CredDecision(null, CredSource.SsoPassthrough, null, null)
            : new CredDecision(server.DefaultCredentialRef, CredSource.ServerDefault, null, null);
    }

    /// <summary>
    /// Decrypts the chosen credential and returns a masked tail (last 4 chars) prefixed with " key ****",
    /// e.g. " key ****cd12". Returns empty string when no encryptor is available or decryption fails.
    /// Never logs the full key.
    /// </summary>
    private string MaskTail(string? credentialRef, IReadOnlyDictionary<string, string> encByName)
    {
        if (string.IsNullOrWhiteSpace(credentialRef) || _encryptor is null) return string.Empty;
        if (!encByName.TryGetValue(credentialRef, out var enc) || string.IsNullOrEmpty(enc)) return string.Empty;
        try
        {
            var plain = _encryptor.Decrypt(enc);
            if (string.IsNullOrEmpty(plain)) return string.Empty;
            var tail = plain.Length <= 4 ? plain : plain[^4..];
            return " key ****" + tail;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>Which precedence tier supplied the credential for a shared server.</summary>
    private enum CredSource { ApiKey, UserGroup, ServerDefault, SsoPassthrough }

    /// <summary>The resolved credential choice for a shared server and where it came from.</summary>
    private sealed record CredDecision(string? CredentialRef, CredSource Source, int? UserGroupId, int? ApiKeyId);

    /// <summary>Projected per-user-group credential row for a shared server.</summary>
    private sealed record GroupCred(int McpServerId, int UserGroupId, string CredentialRef);

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
