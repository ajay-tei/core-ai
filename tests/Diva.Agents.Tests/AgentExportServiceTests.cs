using Diva.Agents.Tests.Helpers;
using Diva.Core.Models;
using Diva.Infrastructure.AgentExport;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Diva.Agents.Tests;

/// <summary>
/// Unit tests for <see cref="AgentExportService"/>.
/// Uses a real in-memory SQLite database — no mocked DbContext.
/// </summary>
public class AgentExportServiceTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<DivaDbContext> _dbOptions;
    private readonly IDatabaseProviderFactory _factory;
    private readonly AgentExportService _svc;
    private readonly TenantContext _tenant = AgentTestFixtures.BasicTenant(1);

    public AgentExportServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<DivaDbContext>()
            .UseSqlite(_connection)
            .Options;

        using var seed = new DivaDbContext(_dbOptions, currentTenantId: 1);
        seed.Database.EnsureCreated();

        _factory = Substitute.For<IDatabaseProviderFactory>();
        _factory.CreateDbContext(Arg.Any<TenantContext?>())
            .Returns(_ => new DivaDbContext(_dbOptions, currentTenantId: 1));

        _svc = new AgentExportService(_factory, NullLogger<AgentExportService>.Instance);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<int> SeedEnvironmentAsync(string slug, int rank, bool isDefault = false)
    {
        using var db = new DivaDbContext(_dbOptions, currentTenantId: 1);
        var env = new TenantEnvironmentEntity
        {
            TenantId = 1,
            Slug = slug,
            DisplayName = slug,
            Rank = rank,
            IsDefault = isDefault,
        };
        db.TenantEnvironments.Add(env);
        await db.SaveChangesAsync();
        return env.Id;
    }

    private async Task<AgentDefinitionEntity> SeedAgentAsync(
        string id = "agent-1",
        string name = "my-agent",
        string? delegateIds = null,
        int? environmentId = null,
        string? customVariablesJson = null)
    {
        using var db = new DivaDbContext(_dbOptions, currentTenantId: 1);
        var agent = new AgentDefinitionEntity
        {
            Id = id,
            TenantId = 1,
            Name = name,
            DisplayName = "My Agent",
            Description = "Test",
            AgentType = "generic",
            SystemPrompt = "You are helpful.",
            Temperature = 0.7,
            MaxIterations = 10,
            DelegateAgentIdsJson = delegateIds,
            EnvironmentId = environmentId,
            CustomVariablesJson = customVariablesJson,
        };
        db.AgentDefinitions.Add(agent);
        await db.SaveChangesAsync();
        return agent;
    }

    private async Task SeedRuleAsync(string agentId, string ruleKey = "tone")
    {
        using var db = new DivaDbContext(_dbOptions, currentTenantId: 1);
        db.BusinessRules.Add(new TenantBusinessRuleEntity
        {
            Guid = Guid.NewGuid().ToString(),
            TenantId = 1,
            AgentId = agentId,
            AgentType = "generic",
            RuleCategory = "Behaviour",
            RuleKey = ruleKey,
            PromptInjection = "Be concise.",
            HookPoint = "OnInit",
            HookRuleType = "inject_prompt",
        });
        await db.SaveChangesAsync();
    }

    // ── Export tests ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ExportAsync_ReturnsAgentFieldsAndRules()
    {
        await SeedAgentAsync("agent-1");
        await SeedRuleAsync("agent-1", "tone");

        var bundle = await _svc.ExportAsync("agent-1", _tenant, CancellationToken.None);

        Assert.Equal("1.0", bundle.SchemaVersion);
        Assert.Equal("my-agent", bundle.Agent.Name);
        Assert.Equal("My Agent", bundle.Agent.DisplayName);
        Assert.Single(bundle.Rules);
        Assert.Equal("tone", bundle.Rules[0].RuleKey);
    }

    [Fact]
    public async Task ExportAsync_ResolvesDelegateAgentNames()
    {
        await SeedAgentAsync("agent-peer", "peer-agent");
        var ids = System.Text.Json.JsonSerializer.Serialize(new[] { "agent-peer" });
        await SeedAgentAsync("agent-1", delegateIds: ids);

        var bundle = await _svc.ExportAsync("agent-1", _tenant, CancellationToken.None);

        Assert.Contains("peer-agent", bundle.Agent.DelegateAgentNames);
    }

    [Fact]
    public async Task ExportAsync_ThrowsWhenAgentNotFound()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.ExportAsync("nonexistent", _tenant, CancellationToken.None));
    }

    // ── Import tests ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ImportAsync_CreatesNewAgentAndRules()
    {
        var bundle = new AgentExportBundle
        {
            SchemaVersion = "1.0",
            SourceTenantId = 99,
            Agent = new AgentExportDefinition
            {
                Name = "imported-agent",
                DisplayName = "Imported",
                AgentType = "generic",
                Temperature = 0.5,
                MaxIterations = 8,
                ExecutionMode = "Full",
            },
            Rules =
            [
                new AgentExportRule
                {
                    AgentType    = "generic",
                    RuleCategory = "Behaviour",
                    RuleKey      = "tone",
                    HookPoint    = "OnInit",
                    HookRuleType = "inject_prompt",
                },
            ],
        };

        var result = await _svc.ImportAsync(
            bundle, _tenant, new AgentImportOptions { ImportRules = true }, CancellationToken.None);

        Assert.Equal("imported-agent", result.AgentName);
        Assert.Equal(1, result.RulesImported);
        Assert.Empty(result.Warnings);

        using var db = new DivaDbContext(_dbOptions, currentTenantId: 1);
        var agent = await db.AgentDefinitions.FindAsync(result.AgentId);
        Assert.NotNull(agent);
        var rules = db.BusinessRules.Where(r => r.AgentId == result.AgentId).ToList();
        Assert.Single(rules);
    }

    [Fact]
    public async Task ImportAsync_OverwritesExistingAgent_WhenFlagSet()
    {
        await SeedAgentAsync("agent-1", "my-agent");
        await SeedRuleAsync("agent-1", "old-rule");

        var bundle = new AgentExportBundle
        {
            SchemaVersion = "1.0",
            SourceTenantId = 1,
            Agent = new AgentExportDefinition
            {
                Name = "my-agent",
                DisplayName = "Updated",
                AgentType = "generic",
                Temperature = 0.3,
                MaxIterations = 5,
                ExecutionMode = "Full",
            },
            Rules = [],
        };

        var result = await _svc.ImportAsync(
            bundle, _tenant,
            new AgentImportOptions { OverwriteExisting = true, ImportRules = false },
            CancellationToken.None);

        Assert.Equal("my-agent", result.AgentName);

        using var db = new DivaDbContext(_dbOptions, currentTenantId: 1);
        var agent = await db.AgentDefinitions.FindAsync(result.AgentId);
        Assert.NotNull(agent);
        Assert.Equal("Updated", agent.DisplayName);
        Assert.True(agent.Version > 1);
    }

    [Fact]
    public async Task ImportAsync_CreatesNewAgent_InheritsSourceCustomVariables()
    {
        var bundle = new AgentExportBundle
        {
            SchemaVersion = "1.0",
            SourceTenantId = 1,
            Agent = new AgentExportDefinition
            {
                Name = "imported-agent",
                AgentType = "generic",
                ExecutionMode = "Full",
                CustomVariablesJson = "{\"company_name\":\"Acme\"}",
            },
            Rules = [],
        };

        var result = await _svc.ImportAsync(bundle, _tenant, new AgentImportOptions(), CancellationToken.None);

        using var db = new DivaDbContext(_dbOptions, currentTenantId: 1);
        var agent = await db.AgentDefinitions.FindAsync(result.AgentId);
        Assert.Equal("{\"company_name\":\"Acme\"}", agent!.CustomVariablesJson);
    }

    [Fact]
    public async Task ImportAsync_OverwritesExistingAgent_PreservesExistingCustomVariables()
    {
        // Pins a real feature: custom variables are editable directly on a promoted agent (PUT
        // /api/agents/{id}/custom-variables) and must survive the next re-promotion/re-import from
        // source — unlike the rest of an agent's config, which is meant to stay pinned to whatever
        // was promoted. Only a brand-new row inherits the source's starting value (see the sibling
        // test above); an already-existing row keeps its own value untouched.
        await SeedAgentAsync("agent-1", "my-agent", customVariablesJson: "{\"region\":\"cot-play\"}");

        var bundle = new AgentExportBundle
        {
            SchemaVersion = "1.0",
            SourceTenantId = 1,
            Agent = new AgentExportDefinition
            {
                Name = "my-agent",
                AgentType = "generic",
                ExecutionMode = "Full",
                CustomVariablesJson = "{\"region\":\"dev\"}",
            },
            Rules = [],
        };

        var result = await _svc.ImportAsync(
            bundle, _tenant,
            new AgentImportOptions { OverwriteExisting = true, ImportRules = false },
            CancellationToken.None);

        using var db = new DivaDbContext(_dbOptions, currentTenantId: 1);
        var agent = await db.AgentDefinitions.FindAsync(result.AgentId);
        Assert.Equal("{\"region\":\"cot-play\"}", agent!.CustomVariablesJson); // kept, not overwritten by source's "dev"
    }

    [Fact]
    public async Task ImportAsync_WarnsMissingDelegateAgents()
    {
        var bundle = new AgentExportBundle
        {
            SchemaVersion = "1.0",
            SourceTenantId = 1,
            Agent = new AgentExportDefinition
            {
                Name = "agent-with-delegates",
                AgentType = "generic",
                ExecutionMode = "Full",
                DelegateAgentNames = ["ghost-agent"],
            },
            Rules = [],
        };

        var result = await _svc.ImportAsync(
            bundle, _tenant, new AgentImportOptions(), CancellationToken.None);

        Assert.Single(result.Warnings);
        Assert.Contains("ghost-agent", result.Warnings[0]);
    }

    [Fact]
    public async Task ImportAsync_ScopesDelegateResolution_ToTargetEnvironment_WhenSameNameExistsInMultipleEnvironments()
    {
        // Same delegate Name promoted to two environments — Dev (1) and COT Play (2) — as two
        // distinct physical rows, mirroring a real sub-agent promotion scenario.
        var devEnvId = await SeedEnvironmentAsync("dev", 0, isDefault: true);
        var cotPlayEnvId = await SeedEnvironmentAsync("cot-play", 1);
        await SeedAgentAsync("agent-dev-weather", "weather-agent", environmentId: devEnvId);
        await SeedAgentAsync("agent-cotplay-weather", "weather-agent", environmentId: cotPlayEnvId);

        var bundle = new AgentExportBundle
        {
            SchemaVersion = "1.0",
            SourceTenantId = 1,
            Agent = new AgentExportDefinition
            {
                Name = "analytics-cot",
                AgentType = "generic",
                ExecutionMode = "Full",
                DelegateAgentNames = ["weather-agent"],
            },
            Rules = [],
        };

        var result = await _svc.ImportAsync(
            bundle, _tenant,
            new AgentImportOptions { DelegateEnvironmentId = cotPlayEnvId },
            CancellationToken.None);

        Assert.Empty(result.Warnings);

        using var db = new DivaDbContext(_dbOptions, currentTenantId: 1);
        var agent = await db.AgentDefinitions.FindAsync(result.AgentId);
        Assert.NotNull(agent);
        var delegateIds = System.Text.Json.JsonSerializer.Deserialize<List<string>>(agent!.DelegateAgentIdsJson!);
        Assert.NotNull(delegateIds);
        Assert.Single(delegateIds!);
        Assert.Equal("agent-cotplay-weather", delegateIds![0]);
    }

    [Fact]
    public async Task ImportAsync_FallsBackToTenantWideDelegateResolution_WhenEnvironmentNotSpecified()
    {
        // Only one physical row for this Name — legacy tenant-wide matching (no environment
        // scoping requested) must still resolve it, since most tenants don't use environments.
        await SeedAgentAsync("agent-peer", "peer-agent", environmentId: null);

        var bundle = new AgentExportBundle
        {
            SchemaVersion = "1.0",
            SourceTenantId = 1,
            Agent = new AgentExportDefinition
            {
                Name = "agent-with-delegates",
                AgentType = "generic",
                ExecutionMode = "Full",
                DelegateAgentNames = ["peer-agent"],
            },
            Rules = [],
        };

        var result = await _svc.ImportAsync(
            bundle, _tenant, new AgentImportOptions(), CancellationToken.None);

        Assert.Empty(result.Warnings);

        using var db = new DivaDbContext(_dbOptions, currentTenantId: 1);
        var agent = await db.AgentDefinitions.FindAsync(result.AgentId);
        var delegateIds = System.Text.Json.JsonSerializer.Deserialize<List<string>>(agent!.DelegateAgentIdsJson!);
        Assert.Equal(["agent-peer"], delegateIds);
    }

    [Fact]
    public async Task ImportAsync_WithImportRulesFalse_SkipsRules()
    {
        var bundle = new AgentExportBundle
        {
            SchemaVersion = "1.0",
            SourceTenantId = 1,
            Agent = new AgentExportDefinition
            {
                Name = "no-rules-agent",
                AgentType = "generic",
                ExecutionMode = "Full",
            },
            Rules =
            [
                new AgentExportRule
                {
                    AgentType    = "generic",
                    RuleCategory = "Behaviour",
                    RuleKey      = "tone",
                    HookPoint    = "OnInit",
                    HookRuleType = "inject_prompt",
                },
            ],
        };

        var result = await _svc.ImportAsync(
            bundle, _tenant, new AgentImportOptions { ImportRules = false }, CancellationToken.None);

        Assert.Equal(0, result.RulesImported);

        using var db = new DivaDbContext(_dbOptions, currentTenantId: 1);
        var rules = db.BusinessRules.Where(r => r.AgentId == result.AgentId).ToList();
        Assert.Empty(rules);
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }
}
