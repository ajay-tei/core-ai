using Diva.Core.Configuration;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Diva.Infrastructure.LiteLLM;
using Diva.Infrastructure.Optimization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Diva.Agents.Tests.Optimization;

/// <summary>
/// Tests for TurnScoringService.
/// The LLM call paths cannot be unit-tested without a real API (no injectable abstraction).
/// Testable: EnablePerTurnScoring=false guard (no DB write, no exception), and score parsing.
/// </summary>
public class TurnScoringServiceTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<SessionTraceDbContext> _traceOpts;

    public TurnScoringServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _traceOpts = new DbContextOptionsBuilder<SessionTraceDbContext>()
            .UseSqlite(_connection)
            .Options;
        using var db = new SessionTraceDbContext(_traceOpts);
        db.Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private TurnScoringService BuildService(bool enableScoring)
    {
        var services = new ServiceCollection();
        services.AddDbContext<SessionTraceDbContext>(o => o.UseSqlite(_connection));

        var provider = services.BuildServiceProvider();

        var llmOpts = Options.Create(new LlmOptions
        {
            DirectProvider = new DirectProviderOptions
            {
                Provider = "OpenAI",
                ApiKey   = "test-key",
                Model    = "gpt-4o",
                Endpoint = "http://localhost:1/"   // invalid — ensures no real call
            }
        });
        var agentOpts = Options.Create(new AgentOptions
        {
            Optimization = new OptimizationOptions
            {
                EnablePerTurnScoring = enableScoring,
                ScorerMaxTokens      = 64
            }
        });

        return new TurnScoringService(
            llmOpts,
            agentOpts,
            provider.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<ILlmConfigResolver>(),
            NullLogger<TurnScoringService>.Instance);
    }

    [Fact]
    public async Task ScoreTurnAsync_WhenScoringDisabled_DoesNotWriteToDatabase()
    {
        // Seed a turn record
        await using (var db = new SessionTraceDbContext(_traceOpts))
        {
            db.TraceSessions.Add(new TraceSessionEntity
            {
                SessionId = "sess-1", TenantId = 1, AgentId = "a1", AgentName = "A", UserId = "u1"
            });
            db.TraceSessionTurns.Add(new TraceSessionTurnEntity
            {
                SessionId = "sess-1", TurnNumber = 1,
                UserMessage = "Hello", AssistantMessage = "Hi",
                AgentId = "a1", ModelId = "m1", Provider = "OpenAI"
            });
            await db.SaveChangesAsync();
        }

        var svc = BuildService(enableScoring: false);

        // Should return immediately without touching the DB
        await svc.ScoreTurnAsync("sess-1", 1, "a1", "Hello", "Hi", "", default);

        await using var verifyDb = new SessionTraceDbContext(_traceOpts);
        var turn = await verifyDb.TraceSessionTurns
            .FirstAsync(t => t.SessionId == "sess-1" && t.TurnNumber == 1);

        Assert.False(turn.ScoresAvailable);
        Assert.Null(turn.FaithfulnessScore);
        Assert.Null(turn.CompletenessScore);
    }

    [Fact]
    public async Task ScoreTurnAsync_WhenScoringDisabled_DoesNotThrow()
    {
        var svc = BuildService(enableScoring: false);

        // Must not throw under any circumstances (fire-and-forget contract)
        var ex = await Record.ExceptionAsync(
            () => svc.ScoreTurnAsync("no-such-session", 99, "agent", "msg", "resp", "", default));

        Assert.Null(ex);
    }

    [Fact]
    public async Task ScoreTurnAsync_WhenLlmEndpointUnreachable_CatchesExceptionAndDoesNotThrow()
    {
        // Even with scoring enabled and an unreachable endpoint,
        // the service must swallow the error (best-effort scoring).
        var svc = BuildService(enableScoring: true);

        var ex = await Record.ExceptionAsync(
            () => svc.ScoreTurnAsync("sess-x", 1, "agent", "query", "answer", "", default));

        Assert.Null(ex);
    }

    // ── Score parsing ──────────────────────────────────────────────────────────
    // A scorer response that cannot be read must leave the turn UNSCORED. Defaulting the
    // missing dimensions to 0 wrote a verdict indistinguishable from a genuine zero, and
    // claude-sonnet-5 hit it on a third of its turns — dragging the pilot KPIs down with
    // scores no one had actually given.

    [Fact]
    public void ParseScores_ReadsAWellFormedResponse()
    {
        var scores = BuildService(true).ParseScores(
            """{"faithfulness":0.9,"completeness":0.8,"tool_efficiency":1.0,"coherence":0.95}""");

        Assert.NotNull(scores);
        Assert.Equal(0.9f, scores!.Faithfulness, 3);
        Assert.Equal(0.8f, scores.Completeness, 3);
        Assert.Equal(1.0f, scores.ToolEfficiency, 3);
        Assert.Equal(0.95f, scores.Coherence, 3);
    }

    [Theory]
    // Nothing recognisable at all.
    [InlineData("""{"score":0.9}""")]
    // A partial payload: the absent dimensions used to be recorded as zero.
    [InlineData("""{"faithfulness":0.9,"completeness":0.8}""")]
    // Non-numeric values that carry no rating.
    [InlineData("""{"faithfulness":null,"completeness":null,"tool_efficiency":null,"coherence":null}""")]
    [InlineData("""{"faithfulness":"high","completeness":"high","tool_efficiency":"high","coherence":"high"}""")]
    [InlineData("not json at all")]
    public void ParseScores_ReturnsNull_WhenTheResponseCarriesNoUsableScores(string raw)
        => Assert.Null(BuildService(true).ParseScores(raw));

    [Fact]
    public void ParseScores_AcceptsQuotedNumbers()
    {
        var scores = BuildService(true).ParseScores(
            """{"faithfulness":"0.9","completeness":"0.8","tool_efficiency":"1.0","coherence":"0.7"}""");

        Assert.NotNull(scores);
        Assert.Equal(0.9f, scores!.Faithfulness, 3);
    }

    [Fact]
    public void ParseScores_AcceptsCamelCaseKeys()
    {
        var scores = BuildService(true).ParseScores(
            """{"faithfulness":0.9,"completeness":0.8,"toolEfficiency":0.6,"coherence":0.7}""");

        Assert.NotNull(scores);
        Assert.Equal(0.6f, scores!.ToolEfficiency, 3);
    }

    [Fact]
    public void ParseScores_UnwrapsAnEnvelope()
    {
        var scores = BuildService(true).ParseScores(
            """{"scores":{"faithfulness":0.9,"completeness":0.8,"tool_efficiency":1.0,"coherence":0.7}}""");

        Assert.NotNull(scores);
        Assert.Equal(0.9f, scores!.Faithfulness, 3);
    }

    [Fact]
    public void ParseScores_KeepsAGenuineZeroVerdict()
    {
        // A real all-zero rating is still recorded — only unreadable responses are discarded.
        var scores = BuildService(true).ParseScores(
            """{"faithfulness":0.0,"completeness":0.0,"tool_efficiency":0.0,"coherence":0.0}""");

        Assert.NotNull(scores);
        Assert.Equal(0f, scores!.Faithfulness, 3);
    }

    [Fact]
    public void ParseScores_ClampsOutOfRangeValues()
    {
        var scores = BuildService(true).ParseScores(
            """{"faithfulness":5,"completeness":-2,"tool_efficiency":0.5,"coherence":1}""");

        Assert.NotNull(scores);
        Assert.Equal(1f, scores!.Faithfulness, 3);
        Assert.Equal(0f, scores.Completeness, 3);
    }

    [Fact]
    public void ParseScores_ToleratesFencedJson()
    {
        var scores = BuildService(true).ParseScores(
            "```json\n{\"faithfulness\":0.9,\"completeness\":0.8,\"tool_efficiency\":1.0,\"coherence\":0.7}\n```");

        Assert.NotNull(scores);
        Assert.Equal(0.8f, scores!.Completeness, 3);
    }
}
