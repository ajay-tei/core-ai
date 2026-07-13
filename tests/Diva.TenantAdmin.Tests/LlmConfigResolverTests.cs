using Diva.Core.Configuration;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Diva.Infrastructure.Groups;
using Diva.Infrastructure.LiteLLM;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Diva.TenantAdmin.Tests;

/// <summary>
/// Integration tests for LlmConfigResolver.
/// Verifies the 4-level resolution hierarchy: platform → group → tenant → per-agent model.
/// Uses real SQLite (in-memory) per ADR-010.
/// </summary>
public class LlmConfigResolverTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DivaDbContext _db;
    private readonly LlmConfigResolver _resolver;
    private readonly IMemoryCache _cache;
    private readonly GroupMembershipCache _membershipCache;

    private static readonly LlmOptions FallbackOptions = new()
    {
        DirectProvider = new DirectProviderOptions
        {
            Provider = "Anthropic",
            ApiKey = "fallback-key",
            Model = "claude-fallback",
        },
        AvailableModels = ["claude-fallback"],
    };

    public LlmConfigResolverTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var opts = new DbContextOptionsBuilder<DivaDbContext>()
            .UseSqlite(_connection)
            .Options;
        _db = new DivaDbContext(opts);
        _db.Database.EnsureCreated();

        _cache = new MemoryCache(new MemoryCacheOptions());
        var factory = new DirectDbFactory(opts);
        _membershipCache = new GroupMembershipCache(factory, _cache, NullLogger<GroupMembershipCache>.Instance);
        _resolver = new LlmConfigResolver(
            factory,
            _membershipCache,
            _cache,
            Options.Create(FallbackOptions),
            NullLogger<LlmConfigResolver>.Instance);
    }

    public void Dispose()
    {
        _db.Dispose();
        _cache.Dispose();
        _connection.Dispose();
    }

    // ── Platform (level 1) ────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_NoPlatformRow_FallsBackToOptions()
    {
        var result = await _resolver.ResolveAsync(0, null, null, CancellationToken.None);

        Assert.Equal("Anthropic", result.Provider);
        Assert.Equal("fallback-key", result.ApiKey);
        Assert.Equal("claude-fallback", result.Model);
    }

    [Fact]
    public async Task ResolveAsync_PlatformRow_UsesPlatformValues()
    {
        _db.PlatformLlmConfigs.Add(new PlatformLlmConfigEntity
        {
            Provider = "OpenAI",
            ApiKey = "platform-key",
            Model = "gpt-4o",
        });
        await _db.SaveChangesAsync();

        var result = await _resolver.ResolveAsync(0, null, null, CancellationToken.None);

        Assert.Equal("OpenAI", result.Provider);
        Assert.Equal("platform-key", result.ApiKey);
        Assert.Equal("gpt-4o", result.Model);
    }

    [Fact]
    public async Task ResolveAsync_MultiPlatformConfigs_UsesFirst()
    {
        // When multiple platform configs exist, resolver picks the one with the smallest Id
        _db.PlatformLlmConfigs.Add(new PlatformLlmConfigEntity { Name = "First", Provider = "Anthropic", ApiKey = "key-a", Model = "claude-base" });
        _db.PlatformLlmConfigs.Add(new PlatformLlmConfigEntity { Name = "Second", Provider = "OpenAI", ApiKey = "key-b", Model = "gpt-4o" });
        await _db.SaveChangesAsync();

        var result = await _resolver.ResolveAsync(0, null, null, CancellationToken.None);

        // "First" was inserted first → lower Id → used as default
        Assert.Equal("Anthropic", result.Provider);
        Assert.Equal("key-a", result.ApiKey);
        Assert.Equal("claude-base", result.Model);
    }

    // ── Per-agent model override (level 4) ────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_AgentModelId_OverridesModelOnly()
    {
        _db.PlatformLlmConfigs.Add(new PlatformLlmConfigEntity
        {
            Id = 1,
            Provider = "OpenAI",
            ApiKey = "plat-key",
            Model = "gpt-4o",
        });
        await _db.SaveChangesAsync();

        var result = await _resolver.ResolveAsync(0, null, "claude-opus-4-6", CancellationToken.None);

        // Provider and key come from platform; model overridden by per-agent value
        Assert.Equal("OpenAI", result.Provider);
        Assert.Equal("plat-key", result.ApiKey);
        Assert.Equal("claude-opus-4-6", result.Model);
    }

    // ── Caching ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_SecondCall_ReturnsCachedResult()
    {
        _db.PlatformLlmConfigs.Add(new PlatformLlmConfigEntity
        {
            Id = 1,
            Provider = "Anthropic",
            ApiKey = "plat-key",
            Model = "cached-model",
        });
        await _db.SaveChangesAsync();

        var first = await _resolver.ResolveAsync(0, null, null, CancellationToken.None);

        // Mutate DB directly — should not be seen on cache hit
        var config = await _db.PlatformLlmConfigs.FindAsync(1);
        config!.Model = "new-model";
        await _db.SaveChangesAsync();

        var second = await _resolver.ResolveAsync(0, null, null, CancellationToken.None);

        Assert.Equal("cached-model", first.Model);
        Assert.Equal("cached-model", second.Model);   // cache hit — still old value
    }

    [Fact]
    public void InvalidateForTenant_AllowsFreshResolution()
    {
        // Cache key format: llm_resolved_{tenantId}_{configId}_{modelId}
        // For (tenantId=3, configId=null, modelId=null) → "llm_resolved_3__"
        _cache.Set("llm_resolved_3__", new ResolvedLlmConfig("A", "k", "m", null, null, []));

        _resolver.InvalidateForTenant(3);

        Assert.False(_cache.TryGetValue("llm_resolved_3__", out _));
    }

    // ── Named config path (agentLlmConfigId) ──────────────────────────────────

    [Fact]
    public async Task ResolveAsync_NamedTenantConfig_OverlaysOnPlatformDefaults()
    {
        _db.PlatformLlmConfigs.Add(new PlatformLlmConfigEntity
        {
            Id = 1,
            Provider = "Anthropic",
            ApiKey = "plat-key",
            Model = "claude-base",
        });
        // Named tenant config with a different provider/model
        _db.TenantLlmConfigs.Add(new TenantLlmConfigEntity
        {
            Id = 99,
            TenantId = 10,
            Name = "OpenAI Production",
            Provider = "OpenAI",
            ApiKey = "openai-key",
            Model = "gpt-4o",
        });
        await _db.SaveChangesAsync();

        var result = await _resolver.ResolveAsync(10, 99, null, CancellationToken.None);

        Assert.Equal("OpenAI", result.Provider);
        Assert.Equal("openai-key", result.ApiKey);
        Assert.Equal("gpt-4o", result.Model);
    }

    [Fact]
    public async Task ResolveAsync_NamedGroupConfig_OverlaysOnPlatformDefaults()
    {
        _db.PlatformLlmConfigs.Add(new PlatformLlmConfigEntity
        {
            Id = 1,
            Provider = "Anthropic",
            ApiKey = "plat-key",
            Model = "claude-base",
        });
        // Group and member
        _db.TenantGroups.Add(new TenantGroupEntity { Id = 50, Name = "G", IsActive = true, CreatedAt = DateTime.UtcNow });
        _db.TenantGroupMembers.Add(new TenantGroupMemberEntity { GroupId = 50, TenantId = 11, JoinedAt = DateTime.UtcNow });
        // Named group config
        _db.GroupLlmConfigs.Add(new GroupLlmConfigEntity
        {
            Id = 55,
            GroupId = 50,
            Name = "Azure OpenAI",
            Provider = "AzureOpenAI",
            ApiKey = "azure-key",
            Model = "gpt-4o-azure",
        });
        await _db.SaveChangesAsync();

        var result = await _resolver.ResolveAsync(11, 55, null, CancellationToken.None);

        Assert.Equal("AzureOpenAI", result.Provider);
        Assert.Equal("azure-key", result.ApiKey);
        Assert.Equal("gpt-4o-azure", result.Model);
    }

    [Fact]
    public async Task ResolveAsync_NamedConfig_BypassesDefaultHierarchy()
    {
        _db.PlatformLlmConfigs.Add(new PlatformLlmConfigEntity
        {
            Id = 1,
            Provider = "Anthropic",
            ApiKey = "plat-key",
            Model = "claude-base",
        });
        // Named config for tenant 12
        _db.TenantLlmConfigs.Add(new TenantLlmConfigEntity
        {
            Id = 77,
            TenantId = 12,
            Name = "Special",
            Provider = "OpenAI",
            ApiKey = "special-key",
            Model = "gpt-3.5-turbo",
        });
        await _db.SaveChangesAsync();

        var result = await _resolver.ResolveAsync(12, 77, null, CancellationToken.None);

        Assert.Equal("OpenAI", result.Provider);
        Assert.Equal("gpt-3.5-turbo", result.Model);
    }

    [Fact]
    public async Task ResolveAsync_NamedConfigNotFound_FallsBackToPlatformDefaults()
    {
        _db.PlatformLlmConfigs.Add(new PlatformLlmConfigEntity
        {
            Id = 1,
            Provider = "Anthropic",
            ApiKey = "plat-key",
            Model = "claude-base",
        });
        await _db.SaveChangesAsync();

        // Config ID 9999 doesn't exist — should fall back to platform defaults without throwing
        var result = await _resolver.ResolveAsync(13, 9999, null, CancellationToken.None);

        Assert.Equal("Anthropic", result.Provider);
        Assert.Equal("claude-base", result.Model);
    }

    [Fact]
    public async Task ResolveAsync_NamedConfig_AgentModelIdStillApplied()
    {
        _db.PlatformLlmConfigs.Add(new PlatformLlmConfigEntity
        {
            Id = 1,
            Provider = "Anthropic",
            ApiKey = "plat-key",
            Model = "claude-base",
        });
        _db.TenantLlmConfigs.Add(new TenantLlmConfigEntity
        {
            Id = 88,
            TenantId = 14,
            Name = "Custom",
            Provider = "OpenAI",
            ApiKey = "openai-key",
            Model = "gpt-4o",
        });
        await _db.SaveChangesAsync();

        // agentModelId overrides even the named config's model
        var result = await _resolver.ResolveAsync(14, 88, "claude-opus-4-6", CancellationToken.None);

        Assert.Equal("OpenAI", result.Provider);   // from named config
        Assert.Equal("openai-key", result.ApiKey); // from named config
        Assert.Equal("claude-opus-4-6", result.Model);  // agentModelId wins
    }

    // ── Platform config reference chain ───────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_GroupRefToPlatform_UsesPlatformCredentials()
    {
        // Platform config with its own credentials
        var platform = new PlatformLlmConfigEntity
        {
            Name = "OpenAI Shared",
            Provider = "OpenAI",
            ApiKey = "plat-openai-key",
            Model = "gpt-4o",
        };
        _db.PlatformLlmConfigs.Add(platform);
        _db.TenantGroups.Add(new TenantGroupEntity { Id = 60, Name = "G", IsActive = true, CreatedAt = DateTime.UtcNow });
        _db.TenantGroupMembers.Add(new TenantGroupMemberEntity { GroupId = 60, TenantId = 15, JoinedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        // Group config references the platform config (no own credentials)
        _db.GroupLlmConfigs.Add(new GroupLlmConfigEntity
        {
            Id = 70,
            GroupId = 60,
            Name = "OpenAI via Platform",
            PlatformConfigRef = platform.Id,
        });
        await _db.SaveChangesAsync();

        var result = await _resolver.ResolveAsync(15, 70, null, CancellationToken.None);

        // Credentials must come from the referenced platform config
        Assert.Equal("OpenAI", result.Provider);
        Assert.Equal("plat-openai-key", result.ApiKey);
        Assert.Equal("gpt-4o", result.Model);
    }

    // ── Blank override must not clobber inherited values ───────────────────────

    [Fact]
    public async Task ResolveAsync_NamedConfigWithBlankApiKey_KeepsInheritedPlatformKey()
    {
        // Regression: a config row with an empty ApiKey must NOT overwrite the valid
        // inherited platform key (an empty x-api-key would be rejected with 401).
        _db.PlatformLlmConfigs.Add(new PlatformLlmConfigEntity
        {
            Id = 1,
            Provider = "Anthropic",
            ApiKey = "plat-key",
            Model = "claude-base",
        });
        _db.TenantGroups.Add(new TenantGroupEntity { Id = 80, Name = "G", IsActive = true, CreatedAt = DateTime.UtcNow });
        _db.TenantGroupMembers.Add(new TenantGroupMemberEntity { GroupId = 80, TenantId = 16, JoinedAt = DateTime.UtcNow });
        // Group config sets provider/model but leaves ApiKey blank (partially filled row)
        _db.GroupLlmConfigs.Add(new GroupLlmConfigEntity
        {
            Id = 81,
            GroupId = 80,
            Name = "Blank Key",
            Provider = "Anthropic",
            ApiKey = "",
            Model = "claude-sonnet-4-6",
        });
        await _db.SaveChangesAsync();

        var result = await _resolver.ResolveAsync(16, 81, null, CancellationToken.None);

        Assert.Equal("Anthropic", result.Provider);
        Assert.Equal("plat-key", result.ApiKey);          // inherited, not clobbered by blank
        Assert.Equal("claude-sonnet-4-6", result.Model);  // model override still applied
    }

    [Fact]
    public async Task ResolveAsync_NamedConfigWithBlankModel_KeepsInheritedPlatformModel()
    {
        // A blank Model must inherit the platform model rather than overwrite it with empty.
        _db.PlatformLlmConfigs.Add(new PlatformLlmConfigEntity
        {
            Id = 1,
            Provider = "Anthropic",
            ApiKey = "plat-key",
            Model = "claude-base",
        });
        _db.TenantLlmConfigs.Add(new TenantLlmConfigEntity
        {
            Id = 91,
            TenantId = 17,
            Name = "Blank Model",
            Provider = "Anthropic",
            ApiKey = "tenant-key",
            Model = "",
        });
        await _db.SaveChangesAsync();

        var result = await _resolver.ResolveAsync(17, 91, null, CancellationToken.None);

        Assert.Equal("tenant-key", result.ApiKey);   // config key applied
        Assert.Equal("claude-base", result.Model);   // blank model inherited from platform
    }
}
