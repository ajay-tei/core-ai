using Diva.Rag.Abstractions;

namespace Diva.Rag.Retrieval;

/// <summary>
/// Builds Qdrant metadata filters from a <see cref="KnowledgeFilter"/>.
/// 4-scope filter hierarchy: platform → group → tenant → agent.
/// </summary>
public static class MetadataFilterBuilder
{
    /// <summary>
    /// Convert a <see cref="KnowledgeFilter"/> into <see cref="VectorSearchOptions"/> filter dictionaries.
    /// Uses _should (OR) for scope filters and _must (AND) for taxonomy + stale filters.
    /// </summary>
    public static VectorSearchOptions Build(KnowledgeFilter filter)
    {
        var options = new VectorSearchOptions
        {
            TopK = filter.TopK,
            ScoreThreshold = filter.MinScore,
            Filter = new Dictionary<string, object>(),
        };

        var mustClauses = new List<Dictionary<string, object>>();

        // Scope filter — OR across visible scopes
        var scopeClauses = new List<Dictionary<string, object>>();

        // Always include platform scope
        scopeClauses.Add(new Dictionary<string, object> { ["scope_type"] = "platform" });

        // Tenant scope
        if (filter.TenantId > 0)
        {
            scopeClauses.Add(new Dictionary<string, object>
            {
                ["scope_type"] = "tenant",
                ["scope_id"] = filter.TenantId,
            });
        }

        // Group scope
        if (filter.GroupIds is { Count: > 0 })
        {
            foreach (var gid in filter.GroupIds)
            {
                scopeClauses.Add(new Dictionary<string, object>
                {
                    ["scope_type"] = "group",
                    ["scope_id"] = gid,
                });
            }
        }

        // Agent scope
        if (!string.IsNullOrEmpty(filter.AgentId))
        {
            scopeClauses.Add(new Dictionary<string, object>
            {
                ["scope_type"] = "agent",
                ["agent_id"] = filter.AgentId,
            });
        }

        if (scopeClauses.Count > 0)
        {
            mustClauses.Add(new Dictionary<string, object>
            {
                ["_should"] = scopeClauses,
            });
        }

        // Exclude stale chunks
        mustClauses.Add(new Dictionary<string, object> { ["is_stale"] = false });

        // Taxonomy filters
        if (!string.IsNullOrEmpty(filter.Domain))
            mustClauses.Add(new Dictionary<string, object> { ["domain"] = filter.Domain });
        if (!string.IsNullOrEmpty(filter.Product))
            mustClauses.Add(new Dictionary<string, object> { ["product"] = filter.Product });
        if (!string.IsNullOrEmpty(filter.ContentType))
            mustClauses.Add(new Dictionary<string, object> { ["content_type"] = filter.ContentType });
        if (!string.IsNullOrEmpty(filter.SecurityLevel))
            mustClauses.Add(new Dictionary<string, object> { ["security_level"] = filter.SecurityLevel });
        if (!string.IsNullOrEmpty(filter.SourceId))
            mustClauses.Add(new Dictionary<string, object> { ["source_id"] = filter.SourceId });

        if (mustClauses.Count > 0)
            options.Filter!["_must"] = mustClauses;

        return options;
    }
}
