using Diva.Core.Configuration;

namespace Diva.Infrastructure.Auth;

/// <summary>
/// Normalizes SSO-provided roles/groups into canonical Diva roles and resolves
/// access-group IDs, applying a safe default when the IdP supplies nothing.
///
/// Rules:
///  (a) Role normalization — when <paramref name="useRoleMappings"/> is on and the tenant
///      defines a <see cref="ClaimMappingsOptions.RoleMap"/>, every raw role/group value is
///      translated through the map (values not present pass through unchanged).
///  (c) Access-group resolution — raw role/group values are looked up in
///      <see cref="ClaimMappingsOptions.AccessGroupMap"/> and any matching access-group IDs are
///      merged into the user's explicit group_access list.
///  Default fallback — a user who ends up with no roles is granted only the default
///      <c>user</c> role, and (with no resolved access groups) can therefore invoke only
///      agents that are not restricted to any access group.
/// </summary>
public static class SsoClaimMapper
{
    public sealed record MappedAccess(string[] Roles, string[] GroupAccess);

    public static MappedAccess Map(
        string[]? rawRoles,
        string[]? rawGroups,
        string[]? rawGroupAccess,
        ClaimMappingsOptions? mappings,
        bool useRoleMappings)
    {
        var rolesIn = rawRoles ?? [];
        var groupsIn = rawGroups ?? [];

        // (a) Role normalization via RoleMap (only when the tenant opted in).
        IEnumerable<string> roles = rolesIn;
        if (useRoleMappings && mappings?.RoleMap is { Count: > 0 } rawRoleMap)
        {
            // Rebuild with OrdinalIgnoreCase — System.Text.Json deserialization loses the comparer.
            var roleMap = new Dictionary<string, string>(rawRoleMap, StringComparer.OrdinalIgnoreCase);
            roles = rolesIn.Concat(groupsIn)
                .Select(v => roleMap.TryGetValue(v, out var canonical) ? canonical : v);
        }

        var normalizedRoles = roles
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // (c) Access-group resolution from raw IdP role/group values.
        var groupAccess = new List<string>(rawGroupAccess ?? []);
        if (mappings?.AccessGroupMap is { Count: > 0 } rawAgMap)
        {
            var agMap = new Dictionary<string, string[]>(rawAgMap, StringComparer.OrdinalIgnoreCase);
            foreach (var value in rolesIn.Concat(groupsIn))
                if (agMap.TryGetValue(value, out var ids))
                    groupAccess.AddRange(ids);
        }

        var resolvedGroupAccess = groupAccess
            .Where(g => !string.IsNullOrWhiteSpace(g))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // Default fallback: no roles mapped → default "user" role only.
        if (normalizedRoles.Length == 0)
            normalizedRoles = ["user"];

        return new MappedAccess(normalizedRoles, resolvedGroupAccess);
    }
}
