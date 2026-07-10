using Diva.Core.Models;
using Diva.Infrastructure.Auth;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Diva.Infrastructure.LiteLLM;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diva.Agents.Tests;

/// <summary>
/// Verifies dynamic credential selection for shared MCP servers:
/// the credential is chosen from the invoking platform API key, then per-user-group
/// membership, then a per-server default, and finally SSO passthrough (empty CredentialRef).
/// Uses real in-memory SQLite per ADR-010.
/// </summary>
public class McpCredentialSelectorTests : IDisposable
{
    private const int TenantId = 7;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<DivaDbContext> _dbOptions;
    private readonly McpCredentialSelector _selector;
    private readonly UserGroupMembershipCache _resolver;

    public McpCredentialSelectorTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<DivaDbContext>()
            .UseSqlite(_connection)
            .Options;

        using (var db = new DivaDbContext(_dbOptions))
            db.Database.EnsureCreated();

        var factory = new TestDatabaseProviderFactory(_dbOptions);
        _resolver = new UserGroupMembershipCache(factory, new MemoryCache(new MemoryCacheOptions()), NullLogger<UserGroupMembershipCache>.Instance);
        _selector = new McpCredentialSelector(factory, _resolver, NullLogger<McpCredentialSelector>.Instance);
    }

    private static TenantContext Ctx(int? platformApiKeyId = null, string userId = "", string email = "")
        => new() { TenantId = TenantId, PlatformApiKeyId = platformApiKeyId, UserId = userId, UserEmail = email };

    private void Seed(TenantMcpServerEntity server)
    {
        using var db = new DivaDbContext(_dbOptions, currentTenantId: TenantId);
        db.TenantMcpServers.Add(server);
        db.SaveChanges();
    }

    /// <summary>Creates a user group with one explicit member and returns its id.</summary>
    private int SeedUserGroup(string name, string userId, string? email = null)
    {
        using var db = new DivaDbContext(_dbOptions, currentTenantId: TenantId);
        var group = new UserGroupEntity
        {
            TenantId = TenantId,
            Name = name,
            Members = { new UserGroupMemberEntity { TenantId = TenantId, UserId = userId, Email = email } },
        };
        db.UserGroups.Add(group);
        db.SaveChanges();
        return group.Id;
    }

    private void SeedGroupCredential(int serverId, int userGroupId, string credentialRef)
    {
        using var db = new DivaDbContext(_dbOptions, currentTenantId: TenantId);
        db.McpServerUserGroupCredentials.Add(new McpServerUserGroupCredentialEntity
        {
            TenantId = TenantId,
            McpServerId = serverId,
            UserGroupId = userGroupId,
            CredentialRef = credentialRef,
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task ApiKeyMapping_Wins_OverDefault()
    {
        Seed(new TenantMcpServerEntity
        {
            TenantId = TenantId,
            Name = "weather",
            Transport = "http",
            Endpoint = "https://mcp.example.com/sse",
            DefaultCredentialRef = "default-key",
            ApiKeyCredentialMappingsJson = """[{"apiKeyId":12,"credentialRef":"acme-key"}]""",
        });

        var bindings = await _selector.ResolveBindingsAsync(Ctx(platformApiKeyId: 12), ["weather"], default);

        var b = Assert.Single(bindings);
        Assert.Equal("weather", b.Name);
        Assert.Equal("acme-key", b.CredentialRef);
        Assert.Equal("https://mcp.example.com/sse", b.Endpoint);
    }

    [Fact]
    public async Task NoMatchingApiKey_FallsBackToDefault()
    {
        Seed(new TenantMcpServerEntity
        {
            TenantId = TenantId,
            Name = "weather",
            DefaultCredentialRef = "default-key",
            ApiKeyCredentialMappingsJson = """[{"apiKeyId":12,"credentialRef":"acme-key"}]""",
        });

        // Invoking key 99 has no mapping → default credential.
        var bindings = await _selector.ResolveBindingsAsync(Ctx(platformApiKeyId: 99), ["weather"], default);

        var b = Assert.Single(bindings);
        Assert.Equal("default-key", b.CredentialRef);
    }

    [Fact]
    public async Task JwtCaller_NoApiKey_UsesDefault()
    {
        Seed(new TenantMcpServerEntity
        {
            TenantId = TenantId,
            Name = "weather",
            DefaultCredentialRef = "default-key",
            ApiKeyCredentialMappingsJson = """[{"apiKeyId":12,"credentialRef":"acme-key"}]""",
        });

        var bindings = await _selector.ResolveBindingsAsync(Ctx(), ["weather"], default);

        var b = Assert.Single(bindings);
        Assert.Equal("default-key", b.CredentialRef);
    }

    [Fact]
    public async Task NoDefault_NoMapping_LeavesCredentialEmpty_ForSsoPassthrough()
    {
        Seed(new TenantMcpServerEntity
        {
            TenantId = TenantId,
            Name = "weather",
            Endpoint = "https://mcp.example.com/sse",
            PassSsoToken = true,
            DefaultCredentialRef = null,
            ApiKeyCredentialMappingsJson = null,
        });

        var bindings = await _selector.ResolveBindingsAsync(Ctx(), ["weather"], default);

        var b = Assert.Single(bindings);
        Assert.Null(b.CredentialRef);
        Assert.True(b.PassSsoToken);
    }

    [Fact]
    public async Task StdioServer_ParsesArgsAndEnv()
    {
        Seed(new TenantMcpServerEntity
        {
            TenantId = TenantId,
            Name = "fs",
            Transport = "stdio",
            Command = "npx",
            ArgsJson = """["-y","@modelcontextprotocol/server-filesystem"]""",
            EnvJson = """{"ROOT":"/data"}""",
        });

        var bindings = await _selector.ResolveBindingsAsync(Ctx(), ["fs"], default);

        var b = Assert.Single(bindings);
        Assert.Equal("npx", b.Command);
        Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem"], b.Args);
        Assert.Equal("/data", b.Env["ROOT"]);
    }

    [Fact]
    public async Task UnknownServerName_IsSkipped()
    {
        Seed(new TenantMcpServerEntity { TenantId = TenantId, Name = "weather", DefaultCredentialRef = "k" });

        var bindings = await _selector.ResolveBindingsAsync(
            Ctx(), ["weather", "does-not-exist"], default);

        var b = Assert.Single(bindings);
        Assert.Equal("weather", b.Name);
    }

    [Fact]
    public async Task OtherTenantServer_IsNotReturned()
    {
        // Seeded under TenantId; query a different tenant.
        Seed(new TenantMcpServerEntity { TenantId = TenantId, Name = "weather", DefaultCredentialRef = "k" });

        var bindings = await _selector.ResolveBindingsAsync(
            new TenantContext { TenantId = 999 }, ["weather"], default);

        Assert.Empty(bindings);
    }

    [Fact]
    public async Task EmptyServerNames_ReturnsEmpty()
    {
        var bindings = await _selector.ResolveBindingsAsync(Ctx(), [], default);
        Assert.Empty(bindings);
    }

    [Fact]
    public async Task MalformedMappingsJson_FallsBackToDefault()
    {
        Seed(new TenantMcpServerEntity
        {
            TenantId = TenantId,
            Name = "weather",
            DefaultCredentialRef = "default-key",
            ApiKeyCredentialMappingsJson = "{ not valid json ]",
        });

        var bindings = await _selector.ResolveBindingsAsync(Ctx(platformApiKeyId: 12), ["weather"], default);

        var b = Assert.Single(bindings);
        Assert.Equal("default-key", b.CredentialRef);
    }

    // ── User-group credential precedence ────────────────────────────────────────

    [Fact]
    public async Task UserGroupCredential_Wins_OverDefault_ForGroupMember()
    {
        Seed(new TenantMcpServerEntity { TenantId = TenantId, Name = "weather", DefaultCredentialRef = "default-key" });
        var serverId = ServerId("weather");
        var gid = SeedUserGroup("finance", "alice");
        SeedGroupCredential(serverId, gid, "finance-key");

        var bindings = await _selector.ResolveBindingsAsync(Ctx(userId: "alice"), ["weather"], default);

        Assert.Equal("finance-key", Assert.Single(bindings).CredentialRef);
    }

    [Fact]
    public async Task NonMember_DoesNotGetGroupCredential()
    {
        Seed(new TenantMcpServerEntity { TenantId = TenantId, Name = "weather", DefaultCredentialRef = "default-key" });
        var serverId = ServerId("weather");
        var gid = SeedUserGroup("finance", "alice");
        SeedGroupCredential(serverId, gid, "finance-key");

        var bindings = await _selector.ResolveBindingsAsync(Ctx(userId: "bob"), ["weather"], default);

        Assert.Equal("default-key", Assert.Single(bindings).CredentialRef);
    }

    [Fact]
    public async Task ApiKeyMapping_Wins_OverUserGroupCredential()
    {
        Seed(new TenantMcpServerEntity
        {
            TenantId = TenantId,
            Name = "weather",
            DefaultCredentialRef = "default-key",
            ApiKeyCredentialMappingsJson = """[{"apiKeyId":12,"credentialRef":"acme-key"}]""",
        });
        var serverId = ServerId("weather");
        var gid = SeedUserGroup("finance", "alice");
        SeedGroupCredential(serverId, gid, "finance-key");

        // Caller is both an API-key caller AND a group member → API key wins.
        var bindings = await _selector.ResolveBindingsAsync(Ctx(platformApiKeyId: 12, userId: "alice"), ["weather"], default);

        Assert.Equal("acme-key", Assert.Single(bindings).CredentialRef);
    }

    [Fact]
    public async Task MultipleGroups_LowestGroupIdWins()
    {
        Seed(new TenantMcpServerEntity { TenantId = TenantId, Name = "weather", DefaultCredentialRef = "default-key" });
        var serverId = ServerId("weather");
        var g1 = SeedUserGroup("group-one", "alice");
        var g2 = SeedUserGroup("group-two", "alice");
        Assert.True(g1 < g2);
        SeedGroupCredential(serverId, g2, "cred-two");
        SeedGroupCredential(serverId, g1, "cred-one");

        var bindings = await _selector.ResolveBindingsAsync(Ctx(userId: "alice"), ["weather"], default);

        // Oldest group (lowest id) deterministically wins.
        Assert.Equal("cred-one", Assert.Single(bindings).CredentialRef);
    }

    [Fact]
    public async Task PreferredUserGroup_OverridesLowestIdTieBreak()
    {
        Seed(new TenantMcpServerEntity { TenantId = TenantId, Name = "weather", DefaultCredentialRef = "default-key" });
        var serverId = ServerId("weather");
        var g1 = SeedUserGroup("group-one", "alice");
        var g2 = SeedUserGroup("group-two", "alice");
        SeedGroupCredential(serverId, g1, "cred-one");
        SeedGroupCredential(serverId, g2, "cred-two");

        var ctx = Ctx(userId: "alice").WithPreferredUserGroup(g2);
        var bindings = await _selector.ResolveBindingsAsync(ctx, ["weather"], default);

        // The caller's explicit choice (g2) wins over the lowest-id default (g1).
        Assert.Equal("cred-two", Assert.Single(bindings).CredentialRef);
    }

    [Fact]
    public async Task PreferredUserGroup_IgnoredWhenNotAMemberOrNoMapping()
    {
        Seed(new TenantMcpServerEntity { TenantId = TenantId, Name = "weather", DefaultCredentialRef = "default-key" });
        var serverId = ServerId("weather");
        var g1 = SeedUserGroup("group-one", "alice");
        SeedGroupCredential(serverId, g1, "cred-one");

        // Prefer a group id the caller does not belong to → falls back to the mapped group.
        var ctx = Ctx(userId: "alice").WithPreferredUserGroup(9999);
        var bindings = await _selector.ResolveBindingsAsync(ctx, ["weather"], default);

        Assert.Equal("cred-one", Assert.Single(bindings).CredentialRef);
    }

    [Fact]
    public async Task GetSelectableGroups_ReturnsCallersCredentialMappedGroups()
    {
        Seed(new TenantMcpServerEntity { TenantId = TenantId, Name = "weather", DefaultCredentialRef = "default-key" });
        var serverId = ServerId("weather");
        var g1 = SeedUserGroup("group-one", "alice");
        var g2 = SeedUserGroup("group-two", "alice");
        var g3 = SeedUserGroup("group-three", "bob");   // caller is not a member
        SeedGroupCredential(serverId, g1, "cred-one");
        SeedGroupCredential(serverId, g2, "cred-two");
        SeedGroupCredential(serverId, g3, "cred-three");

        var groups = await _selector.GetSelectableGroupsAsync(
            Ctx(userId: "alice"), ["weather"], restrictToUserGroupIds: null, default);

        Assert.Equal([g1, g2], groups.Select(g => g.Id).ToArray());
    }

    [Fact]
    public async Task GetSelectableGroups_IntersectsWithAgentAllowedGroups()
    {
        Seed(new TenantMcpServerEntity { TenantId = TenantId, Name = "weather", DefaultCredentialRef = "default-key" });
        var serverId = ServerId("weather");
        var g1 = SeedUserGroup("group-one", "alice");
        var g2 = SeedUserGroup("group-two", "alice");
        SeedGroupCredential(serverId, g1, "cred-one");
        SeedGroupCredential(serverId, g2, "cred-two");

        // Agent access group only allows g2.
        var groups = await _selector.GetSelectableGroupsAsync(
            Ctx(userId: "alice"), ["weather"], restrictToUserGroupIds: new[] { g2 }, default);

        Assert.Equal([g2], groups.Select(g => g.Id).ToArray());
    }

    private int ServerId(string name)
    {
        using var db = new DivaDbContext(_dbOptions, currentTenantId: TenantId);
        return db.TenantMcpServers.Single(s => s.Name == name).Id;
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
