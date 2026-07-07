using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Diva.Infrastructure.LiteLLM;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Diva.Agents.Tests;

/// <summary>
/// Verifies dynamic credential selection for shared MCP servers:
/// the credential is chosen from the invoking platform API key, falling back to a
/// per-server default, and finally to SSO passthrough (empty CredentialRef).
/// Uses real in-memory SQLite per ADR-010.
/// </summary>
public class McpCredentialSelectorTests : IDisposable
{
    private const int TenantId = 7;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<DivaDbContext> _dbOptions;
    private readonly McpCredentialSelector _selector;

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
        _selector = new McpCredentialSelector(factory, NullLogger<McpCredentialSelector>.Instance);
    }

    private void Seed(TenantMcpServerEntity server)
    {
        using var db = new DivaDbContext(_dbOptions, currentTenantId: TenantId);
        db.TenantMcpServers.Add(server);
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

        var bindings = await _selector.ResolveBindingsAsync(TenantId, platformApiKeyId: 12, ["weather"], default);

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
        var bindings = await _selector.ResolveBindingsAsync(TenantId, platformApiKeyId: 99, ["weather"], default);

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

        var bindings = await _selector.ResolveBindingsAsync(TenantId, platformApiKeyId: null, ["weather"], default);

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

        var bindings = await _selector.ResolveBindingsAsync(TenantId, platformApiKeyId: null, ["weather"], default);

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

        var bindings = await _selector.ResolveBindingsAsync(TenantId, platformApiKeyId: null, ["fs"], default);

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
            TenantId, platformApiKeyId: null, ["weather", "does-not-exist"], default);

        var b = Assert.Single(bindings);
        Assert.Equal("weather", b.Name);
    }

    [Fact]
    public async Task OtherTenantServer_IsNotReturned()
    {
        // Seeded under TenantId; query a different tenant.
        Seed(new TenantMcpServerEntity { TenantId = TenantId, Name = "weather", DefaultCredentialRef = "k" });

        var bindings = await _selector.ResolveBindingsAsync(
            tenantId: 999, platformApiKeyId: null, ["weather"], default);

        Assert.Empty(bindings);
    }

    [Fact]
    public async Task EmptyServerNames_ReturnsEmpty()
    {
        var bindings = await _selector.ResolveBindingsAsync(TenantId, null, [], default);
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

        var bindings = await _selector.ResolveBindingsAsync(TenantId, platformApiKeyId: 12, ["weather"], default);

        var b = Assert.Single(bindings);
        Assert.Equal("default-key", b.CredentialRef);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
