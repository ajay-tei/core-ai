using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Diva.Infrastructure.Data.Entities;
using ModelContextProtocol.Client;

namespace Diva.Infrastructure.LiteLLM;

/// <summary>
/// Singleton cache for MCP clients keyed by (agentId, MD5(toolBindings)).
/// Avoids spawning a new docker/stdio process (or HTTP connection) on every agent request.
/// TTL is 30 minutes; bindings changes invalidate the cache entry immediately.
/// </summary>
public sealed class McpClientCache : IAsyncDisposable
{
    private sealed record CachedEntry(
        Dictionary<string, McpClient> Clients,
        string BindingsHash,
        DateTime CreatedAt);

    private readonly ConcurrentDictionary<string, CachedEntry> _cache = new();
    private readonly TimeSpan _ttl = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Returns cached clients if the agent's bindings are unchanged and within TTL;
    /// otherwise disposes the stale entry and invokes <paramref name="connectFactory"/> to reconnect.
    /// An empty result is not cached when bindings are configured — lets the next request
    /// retry rather than inheriting a transient connection failure for the full TTL duration.
    /// Pass <paramref name="cacheKeySuffix"/> to create an isolated cache slot (e.g. ":sso" for
    /// SSO-forwarding connections so they don't collide with the non-forwarding entry).
    /// Pass <paramref name="effectiveBindingsJson"/> (inline + resolved shared-server bindings) and
    /// <paramref name="cacheKeyDiscriminator"/> (e.g. the invoking API key id) when an agent
    /// references shared MCP servers, so per-API-key credential variants don't collide or
    /// reuse a connection authenticated with another key's credential.
    /// </summary>
    public async Task<Dictionary<string, McpClient>> GetOrConnectAsync(
        AgentDefinitionEntity definition,
        Func<CancellationToken, Task<Dictionary<string, McpClient>>> connectFactory,
        CancellationToken ct,
        string? cacheKeySuffix = null,
        string? effectiveBindingsJson = null,
        string? cacheKeyDiscriminator = null)
    {
        var bindingsForHash = effectiveBindingsJson ?? definition.ToolBindings;
        var cacheKey = BuildCacheKey(definition.Id, cacheKeyDiscriminator, cacheKeySuffix);
        var hash = ComputeHash(bindingsForHash);

        if (_cache.TryGetValue(cacheKey, out var entry)
            && entry.BindingsHash == hash
            && DateTime.UtcNow - entry.CreatedAt < _ttl)
        {
            return entry.Clients;
        }

        return await ConnectAndCacheAsync(cacheKey, bindingsForHash, hash, connectFactory, ct);
    }

    /// <summary>
    /// Evicts the cached entry for an agent and forces a fresh reconnect.
    /// Called when a cached session turns out to be dead (e.g. "Session ID not found").
    /// </summary>
    public async Task<Dictionary<string, McpClient>> EvictAndReconnectAsync(
        AgentDefinitionEntity definition,
        Func<CancellationToken, Task<Dictionary<string, McpClient>>> connectFactory,
        CancellationToken ct,
        string? cacheKeySuffix = null,
        string? effectiveBindingsJson = null,
        string? cacheKeyDiscriminator = null)
    {
        var bindingsForHash = effectiveBindingsJson ?? definition.ToolBindings;
        var cacheKey = BuildCacheKey(definition.Id, cacheKeyDiscriminator, cacheKeySuffix);
        var hash = ComputeHash(bindingsForHash);
        if (_cache.TryRemove(cacheKey, out var dead))
            foreach (var c in dead.Clients.Values)
                try { await c.DisposeAsync(); } catch { /* ignore */ }
        return await ConnectAndCacheAsync(cacheKey, bindingsForHash, hash, connectFactory, ct);
    }

    private static string BuildCacheKey(string agentId, string? discriminator, string? suffix) =>
        string.IsNullOrEmpty(discriminator)
            ? agentId + suffix
            : $"{agentId}:{discriminator}{suffix}";

    private async Task<Dictionary<string, McpClient>> ConnectAndCacheAsync(
        string cacheKey,
        string? effectiveBindingsJson,
        string hash,
        Func<CancellationToken, Task<Dictionary<string, McpClient>>> connectFactory,
        CancellationToken ct)
    {
        // Stale or missing — evict and reconnect
        if (_cache.TryRemove(cacheKey, out var old))
        {
            foreach (var c in old.Clients.Values)
                try { await c.DisposeAsync(); } catch { /* ignore dispose errors on eviction */ }
        }

        var clients = await connectFactory(ct);

        // Only cache a non-empty result when bindings were configured.
        // An empty map with active bindings means all connections failed — don't
        // cache it so the next request retries rather than inheriting the failure.
        // An empty map for an agent with no bindings (null / "" / "[]") is intentional and safe to cache.
        bool hasBindings = !string.IsNullOrEmpty(effectiveBindingsJson)
                        && effectiveBindingsJson.Trim() != "[]";
        if (clients.Count > 0 || !hasBindings)
            _cache[cacheKey] = new CachedEntry(clients, hash, DateTime.UtcNow);

        return clients;
    }

    /// <summary>
    /// Evicts all cached entries for an agent (e.g. after admin edits bindings).
    /// Matches the base agent id plus any discriminator/suffix variants
    /// (per-API-key credential slots, ":sso" forwarding slot, etc.).
    /// No-op if the agent has no cached entry.
    /// </summary>
    public async Task EvictAsync(string agentId)
    {
        var keys = _cache.Keys
            .Where(k => k == agentId || k.StartsWith(agentId + ":", StringComparison.Ordinal))
            .ToList();
        foreach (var key in keys)
            if (_cache.TryRemove(key, out var entry))
                foreach (var c in entry.Clients.Values)
                    try { await c.DisposeAsync(); } catch { /* ignore */ }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _cache.Values)
            foreach (var c in entry.Clients.Values)
                try { await c.DisposeAsync(); } catch { /* ignore */ }
        _cache.Clear();
    }

    private static string ComputeHash(string? input)
    {
        if (string.IsNullOrEmpty(input)) return string.Empty;
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }
}
