using Diva.Infrastructure.Data.Entities;
using Diva.Infrastructure.LiteLLM;
using ModelContextProtocol.Client;

namespace Diva.Agents.Tests;

public class McpClientCacheTests : IAsyncDisposable
{
    private readonly McpClientCache _cache = new();

    public async ValueTask DisposeAsync() => await _cache.DisposeAsync();

    // ── helpers ──────────────────────────────────────────────────────────────

    private static AgentDefinitionEntity Agent(string id = "a1", string bindings = "[]") =>
        new() { Id = id, Name = "Test", DisplayName = "Test", ToolBindings = bindings };

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetOrConnect_FirstCall_InvokesFactory()
    {
        int calls = 0;
        var agent = Agent();

        await _cache.GetOrConnectAsync(agent, _ => { calls++; return Task.FromResult(new Dictionary<string, McpClient>()); }, default);

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task GetOrConnect_SameBindings_ReturnsCachedAndSkipsFactory()
    {
        int calls = 0;
        var agent = Agent();
        Func<CancellationToken, Task<Dictionary<string, McpClient>>> factory =
            _ => { calls++; return Task.FromResult(new Dictionary<string, McpClient>()); };

        var first = await _cache.GetOrConnectAsync(agent, factory, default);
        var second = await _cache.GetOrConnectAsync(agent, factory, default);

        Assert.Equal(1, calls);           // factory called only once
        Assert.Same(first, second);       // same dictionary instance returned
    }

    [Fact]
    public async Task GetOrConnect_BindingsChanged_ReconnectsWithNewFactory()
    {
        int calls = 0;
        var agentV1 = Agent("a1", "[]");
        var agentV2 = Agent("a1", "[{\"name\":\"new-server\"}]");
        Func<CancellationToken, Task<Dictionary<string, McpClient>>> factory =
            _ => { calls++; return Task.FromResult(new Dictionary<string, McpClient>()); };

        await _cache.GetOrConnectAsync(agentV1, factory, default);  // cold
        await _cache.GetOrConnectAsync(agentV2, factory, default);  // bindings changed → reconnect

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task GetOrConnect_TtlExpired_Reconnects()
    {
        int calls = 0;
        var agent = Agent();
        Func<CancellationToken, Task<Dictionary<string, McpClient>>> factory =
            _ => { calls++; return Task.FromResult(new Dictionary<string, McpClient>()); };

        // Warm the cache
        await _cache.GetOrConnectAsync(agent, factory, default);
        Assert.Equal(1, calls);

        // Manually backdating the cached entry's CreatedAt via reflection to simulate TTL expiry.
        // Use IDictionary (non-generic) to avoid the invariant generic type cast.
        var cacheField = typeof(McpClientCache)
            .GetField("_cache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var dict = (System.Collections.IDictionary)cacheField.GetValue(_cache)!;

        // Replace the entry with a stale one (CreatedAt 31 minutes ago)
        var existingEntry = dict[agent.Id];
        if (existingEntry is not null)
        {
            var entryType = existingEntry.GetType();
            var staleEntry = entryType.GetConstructors()[0].Invoke([
                entryType.GetProperty("Clients")!.GetValue(existingEntry),
                entryType.GetProperty("BindingsHash")!.GetValue(existingEntry),
                DateTime.UtcNow.AddMinutes(-31)
            ]);
            dict[agent.Id] = staleEntry;
        }

        // Next call should bypass cache and reconnect
        await _cache.GetOrConnectAsync(agent, factory, default);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task EvictAsync_RemovesEntry_NextCallReconnects()
    {
        int calls = 0;
        var agent = Agent();
        Func<CancellationToken, Task<Dictionary<string, McpClient>>> factory =
            _ => { calls++; return Task.FromResult(new Dictionary<string, McpClient>()); };

        await _cache.GetOrConnectAsync(agent, factory, default);   // warm
        await _cache.EvictAsync(agent.Id);                          // evict
        await _cache.GetOrConnectAsync(agent, factory, default);   // should reconnect

        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task EvictAsync_NonExistentAgent_IsNoOp()
    {
        // Should not throw
        await _cache.EvictAsync("does-not-exist");
    }

    [Fact]
    public async Task EvictAllAsync_RemovesEveryAgent_NextCallsAllReconnect()
    {
        int calls1 = 0, calls2 = 0;
        var agent1 = Agent("a1");
        var agent2 = Agent("a2");
        Func<CancellationToken, Task<Dictionary<string, McpClient>>> factory1 =
            _ => { calls1++; return Task.FromResult(new Dictionary<string, McpClient>()); };
        Func<CancellationToken, Task<Dictionary<string, McpClient>>> factory2 =
            _ => { calls2++; return Task.FromResult(new Dictionary<string, McpClient>()); };

        await _cache.GetOrConnectAsync(agent1, factory1, default);  // warm agent1
        await _cache.GetOrConnectAsync(agent2, factory2, default);  // warm agent2

        await _cache.EvictAllAsync();                                // evict every agent

        await _cache.GetOrConnectAsync(agent1, factory1, default);  // should reconnect
        await _cache.GetOrConnectAsync(agent2, factory2, default);  // should reconnect

        Assert.Equal(2, calls1);
        Assert.Equal(2, calls2);
    }

    [Fact]
    public async Task EvictAllAsync_EmptyCache_IsNoOp()
    {
        // Should not throw
        await _cache.EvictAllAsync();
    }
}
