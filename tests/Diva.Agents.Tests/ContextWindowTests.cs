using Anthropic.SDK.Messaging;
using Diva.Agents.Tests.Helpers;
using Diva.Core.Configuration;
using Diva.Infrastructure.Context;
using Microsoft.Extensions.Options;
using Diva.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Diva.Infrastructure.Learning;
using Diva.Infrastructure.LiteLLM;
using Diva.Infrastructure.Sessions;
using Diva.Infrastructure.Verification;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Diva.Agents.Tests;

/// <summary>
/// Tests for ContextWindowManager (pure unit) and IContextWindowManager injection into
/// AnthropicAgentRunner (integration).
///
/// Pure unit tests construct ContextWindowManager directly — no runner, no DB, no real LLM.
/// Integration tests mock IContextWindowManager via NSubstitute and inject into the runner.
/// </summary>
public class ContextWindowTests : IAsyncDisposable
{
    // ── Shared SQLite in-memory DB for integration tests ─────────────────────

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<DivaDbContext> _dbOptions;

    public ContextWindowTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<DivaDbContext>()
            .UseSqlite(_connection)
            .Options;
        using var db = new DivaDbContext(_dbOptions);
        db.Database.EnsureCreated();
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    // ── Helper: build ContextWindowManager ───────────────────────────────────

    private static ContextWindowManager BuildManager(
        ContextWindowOptions? opts = null,
        IAnthropicProvider? anthropic = null,
        IOpenAiProvider? openAi = null)
    {
        var agentOpts = AgentTestFixtures.Opts(new AgentOptions
        {
            ContextWindow = opts ?? new ContextWindowOptions()
        });
        return new ContextWindowManager(
            agentOpts,
            AgentTestFixtures.AnthropicLlm(),
            anthropic ?? Substitute.For<IAnthropicProvider>(),
            openAi    ?? Substitute.For<IOpenAiProvider>(),
            NullLogger<ContextWindowManager>.Instance);
    }

    // ── EstimateTokens (static helper) ───────────────────────────────────────

    [Fact]
    public void EstimateTokens_EmptyString_ReturnsZero()
    {
        Assert.Equal(0, ContextWindowManager.EstimateTokens(""));
    }

    [Fact]
    public void EstimateTokens_FourChars_ReturnsOne()
    {
        Assert.Equal(1, ContextWindowManager.EstimateTokens("abcd"));
    }

    [Fact]
    public void EstimateTokens_TwelveChars_ReturnsThree()
    {
        Assert.Equal(3, ContextWindowManager.EstimateTokens("abcdefghijkl"));
    }

    // ── CompactHistoryAsync — rule-based path ─────────────────────────────────

    [Fact]
    public async Task CompactHistoryAsync_WithinLimit_ReturnsUnchangedAndNullSummary()
    {
        var mgr = BuildManager(new ContextWindowOptions { MaxHistoryTurns = 20 });
        var history = Enumerable.Range(1, 5)
            .Select(i => new ConversationTurn("user", $"query_{i}"))
            .ToList();

        var (turns, summary) = await mgr.CompactHistoryAsync(history);

        Assert.Equal(5, turns.Count);
        Assert.Null(summary);
    }

    [Fact]
    public async Task CompactHistoryAsync_ExceedsLimit_KeepsRecentTurnsVerbatim()
    {
        var mgr = BuildManager(new ContextWindowOptions { MaxHistoryTurns = 10 });
        var history = Enumerable.Range(1, 25)
            .Select(i => new ConversationTurn("user", $"query_{i}"))
            .ToList();

        var (turns, summary) = await mgr.CompactHistoryAsync(history);

        Assert.Equal(10, turns.Count);
        // Must be the tail (most recent 10)
        Assert.Equal("query_16", turns[0].Content);
        Assert.Equal("query_25", turns[^1].Content);
    }

    [Fact]
    public async Task CompactHistoryAsync_ExceedsLimit_SummaryContainsUserQuerySnippets()
    {
        var mgr = BuildManager(new ContextWindowOptions { MaxHistoryTurns = 10 });
        var history = Enumerable.Range(1, 25)
            .Select(i => new ConversationTurn("user", $"user_query_{i}"))
            .ToList();

        var (_, summary) = await mgr.CompactHistoryAsync(history);

        Assert.NotNull(summary);
        Assert.Contains("user_query_1", summary);
    }

    // ── CompactHistoryAsync — LLM path ────────────────────────────────────────

    [Fact]
    public async Task CompactHistoryAsync_WithSummarizerModel_CallsAnthropicProvider()
    {
        var anthropic = Substitute.For<IAnthropicProvider>();
        anthropic.GetClaudeMessageAsync(Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(MakeAnthropicResponse("• Key fact 1\n• Key fact 2")));

        var mgr = BuildManager(
            new ContextWindowOptions
            {
                MaxHistoryTurns  = 10,
                SummarizerModel  = "claude-haiku-4-5-20251001"
            },
            anthropic: anthropic);

        var history = Enumerable.Range(1, 25)
            .Select(i => new ConversationTurn(i % 2 == 0 ? "assistant" : "user", $"content_{i}"))
            .ToList();

        var (turns, summary) = await mgr.CompactHistoryAsync(history);

        Assert.Equal(10, turns.Count);
        Assert.NotNull(summary);
        Assert.StartsWith("Earlier session context (LLM summary):", summary);
        await anthropic.Received(1).GetClaudeMessageAsync(
            Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>());
    }

    // ── CompactHistoryAsync — per-agent override ──────────────────────────────

    [Fact]
    public async Task CompactHistoryAsync_WithAgentOverride_MaxHistoryTurnsOverrideApplied()
    {
        // Global = 20 turns (no compaction for 10 items), override = 5 (triggers compaction)
        var mgr = BuildManager(new ContextWindowOptions { MaxHistoryTurns = 20 });
        var history = Enumerable.Range(1, 10)
            .Select(i => new ConversationTurn("user", $"query_{i}"))
            .ToList();

        var (turns, summary) = await mgr.CompactHistoryAsync(
            history,
            agentOverride: new ContextWindowOverrideOptions { MaxHistoryTurns = 5 });

        Assert.Equal(5, turns.Count);
        Assert.NotNull(summary);
        // Tail of 5 should be queries 6..10
        Assert.Equal("query_6", turns[0].Content);
    }

    // ── ComputeCompactionPlan (generic core) ──────────────────────────────────

    [Fact]
    public void ComputeCompactionPlan_UnderThreshold_ReturnsShouldCompactFalse()
    {
        var mgr = BuildManager(new ContextWindowOptions
        {
            BudgetTokens        = 120_000,
            CompactionThreshold = 0.65,
            KeepLastRawMessages = 6
        });

        var texts = new List<string> { "short message", "another short one", "third" };
        var (should, _, _) = mgr.ComputeCompactionPlan(texts, "short system");

        Assert.False(should);
    }

    [Fact]
    public void ComputeCompactionPlan_OverThreshold_ReturnsShouldCompactTrueAndSummaryText()
    {
        // Force threshold = 1 token, 8 messages with enough text to exceed
        var mgr = BuildManager(new ContextWindowOptions
        {
            BudgetTokens        = 4,   // threshold = 4 * 0.65 ≈ 2 tokens
            CompactionThreshold = 0.65,
            KeepLastRawMessages = 2
        });

        // 8 messages × 40 chars each ≈ 80 tokens >> 2 threshold
        var texts = Enumerable.Range(1, 8)
            .Select(i => new string('x', 40))
            .ToList();

        var (should, keepLast, summaryText) =
            mgr.ComputeCompactionPlan(texts, "system");

        Assert.True(should);
        Assert.Equal(2, keepLast);
        Assert.NotEmpty(summaryText);
    }

    // ── MaybeCompactAnthropicMessages (Anthropic adapter) ────────────────────

    [Fact]
    public void MaybeCompactAnthropicMessages_OverThreshold_FirstAndLastMessagesPreserved()
    {
        var mgr = BuildManager(new ContextWindowOptions
        {
            BudgetTokens        = 4,
            CompactionThreshold = 0.65,
            KeepLastRawMessages = 2
        });

        // 8 padded messages → compaction triggers
        var messages = Enumerable.Range(1, 8)
            .Select(i => new Message
            {
                Role    = i % 2 == 0 ? RoleType.Assistant : RoleType.User,
                Content = [new Anthropic.SDK.Messaging.TextContent { Text = new string('x', 40) }]
            })
            .ToList();

        var result = mgr.MaybeCompactAnthropicMessages(messages, "system", null);

        // First message preserved, last 2 preserved, a summary in between
        Assert.Equal(messages[0], result[0]);
        Assert.Equal(messages[^1], result[^1]);
        Assert.Equal(messages[^2], result[^2]);
        // Summary message inserted at index 1
        Assert.Contains("[Prior context in this run", result[1].Content
            .OfType<Anthropic.SDK.Messaging.TextContent>().First().Text);
    }

    // ── Integration tests — mock IContextWindowManager ────────────────────────

    private AnthropicAgentRunner BuildRunnerWithCtx(
        IAnthropicProvider anthropic,
        IContextWindowManager ctx,
        AgentOptions? agentOpts = null)
    {
        var factory  = new CwTestDbFactory(_dbOptions);
        var sessions = new AgentSessionService(factory, NullLogger<AgentSessionService>.Instance);
        var verifier = new ResponseVerifier(
            AgentTestFixtures.Opts(new VerificationOptions { Mode = "Off" }),
            AgentTestFixtures.AnthropicLlm(),
            Substitute.For<IAnthropicProvider>(),
            Substitute.For<IOpenAiProvider>(),
            NullLogger<ResponseVerifier>.Instance);
        var ruleLearner = Substitute.For<IRuleLearningService>();
        ruleLearner.ExtractRulesFromConversationAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);
        var cache = new McpClientCache();

        return new AnthropicAgentRunner(
            AgentTestFixtures.AnthropicLlm(),
            AgentTestFixtures.Opts(agentOpts ?? new AgentOptions
            {
                // The mock only stubs the buffered GetClaudeMessageAsync, not the streaming
                // method — leaving streaming on makes NSubstitute auto-return an empty stream,
                // which the runner treats as an empty end_turn response and retries once.
                EnableResponseStreaming = false,
                Retry = new LlmRetryOptions { MaxRetries = 3, BaseDelayMs = 1 }
            }),
            AgentTestFixtures.Opts(new VerificationOptions { Mode = "Off" }),
            sessions,
            verifier,
            ruleLearner,
            cache,
            anthropic,
            Substitute.For<IOpenAiProvider>(),
            ctx,
            Substitute.For<IHttpContextAccessor>(),
            new ToolExecutor(NullLogger<ToolExecutor>.Instance, Options.Create(new AgentOptions())),
            NullLogger<AnthropicAgentRunner>.Instance);
    }

    [Fact]
    public async Task RunAsync_CallsCompactHistoryOnce()
    {
        var anthropic = Substitute.For<IAnthropicProvider>();
        anthropic.GetClaudeMessageAsync(Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(MakeTextResponse("Hello!")));

        var ctx = ContextWindowTestHelpers.NoOpCtx();
        var runner = BuildRunnerWithCtx(anthropic, ctx);

        await runner.RunAsync(
            AgentTestFixtures.BasicAgent(),
            AgentTestFixtures.BasicRequest("hi"),
            AgentTestFixtures.BasicTenant(),
            CancellationToken.None);

        await ctx.Received(1).CompactHistoryAsync(
            Arg.Any<List<ConversationTurn>>(),
            Arg.Any<string?>(),
            Arg.Any<ContextWindowOverrideOptions?>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_CallsMaybeCompactAnthropicBeforeLlmCall()
    {
        var anthropic = Substitute.For<IAnthropicProvider>();
        anthropic.GetClaudeMessageAsync(Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(MakeTextResponse("Hello!")));

        var ctx = ContextWindowTestHelpers.NoOpCtx();
        var runner = BuildRunnerWithCtx(anthropic, ctx);

        await runner.RunAsync(
            AgentTestFixtures.BasicAgent(),
            AgentTestFixtures.BasicRequest("hi"),
            AgentTestFixtures.BasicTenant(),
            CancellationToken.None);

        // One LLM call → one compaction check
        ctx.Received(1).MaybeCompactAnthropicMessages(
            Arg.Any<List<Message>>(),
            Arg.Any<string>(),
            Arg.Any<ContextWindowOverrideOptions?>());
    }

    // ── AnthropicMessageText — tool content estimation ────────────────────────

    [Fact]
    public void AnthropicMessageText_ToolResultContent_CountsAsNonZero()
    {
        var msg = new Message
        {
            Role    = RoleType.User,
            Content =
            [
                new Anthropic.SDK.Messaging.ToolResultContent
                {
                    ToolUseId = "tc-1",
                    Content   = [new Anthropic.SDK.Messaging.TextContent { Text = new string('x', 400) }]
                }
            ]
        };

        var text = ContextWindowManager.AnthropicMessageText(msg);
        Assert.Equal(400, text.Length); // was 0 before fix
    }

    [Fact]
    public void AnthropicMessageText_ToolUseContent_CountsInputJson()
    {
        var msg = new Message
        {
            Role    = RoleType.Assistant,
            Content =
            [
                new ToolUseContent
                {
                    Id    = "tc-1",
                    Name  = "send_email",
                    Input = System.Text.Json.Nodes.JsonNode.Parse("""{"body":"large email content here"}""")
                }
            ]
        };

        var text = ContextWindowManager.AnthropicMessageText(msg);
        Assert.True(text.Length > 0);
    }

    [Fact]
    public void AnthropicMessageText_MixedContent_SumsAllParts()
    {
        var msg = new Message
        {
            Role    = RoleType.User,
            Content =
            [
                new Anthropic.SDK.Messaging.TextContent { Text = "aaa" },
                new Anthropic.SDK.Messaging.ToolResultContent
                {
                    ToolUseId = "tc-1",
                    Content   = [new Anthropic.SDK.Messaging.TextContent { Text = "bbbbb" }]
                }
            ]
        };

        var text = ContextWindowManager.AnthropicMessageText(msg);
        Assert.Equal(9, text.Length); // "aaa bbbbb" — separator adds 1
    }

    // ── ChatMessageText — OpenAI tool content estimation ─────────────────────

    [Fact]
    public void ChatMessageText_FunctionResultContent_CountsAsNonZero()
    {
        var msg = new ChatMessage(ChatRole.Tool,
            [new FunctionResultContent("tc-1", new string('x', 400))]);

        var text = ContextWindowManager.ChatMessageText(msg);
        Assert.Equal(400, text.Length); // was 0 before fix (m.Text returned null)
    }

    [Fact]
    public void ChatMessageText_FunctionCallContent_CountsArgs()
    {
        var args = new Dictionary<string, object?> { ["city"] = "London" };
        var msg = new ChatMessage(ChatRole.Assistant,
            [new FunctionCallContent("tc-1", "get_weather", args)]);

        var text = ContextWindowManager.ChatMessageText(msg);
        Assert.True(text.Length > 0);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static MessageResponse MakeAnthropicResponse(string text) => new()
    {
        Content    = [new Anthropic.SDK.Messaging.TextContent { Text = text }],
        StopReason = "end_turn",
        Model      = "claude-sonnet-4-20250514"
    };

    private static MessageResponse MakeTextResponse(string text) => new()
    {
        Content    = [new Anthropic.SDK.Messaging.TextContent { Text = text }],
        StopReason = "end_turn",
        Model      = "claude-sonnet-4-20250514"
    };
}

internal sealed class CwTestDbFactory : IDatabaseProviderFactory
{
    private readonly DbContextOptions<DivaDbContext> _options;
    public CwTestDbFactory(DbContextOptions<DivaDbContext> options) => _options = options;
    public DivaDbContext CreateDbContext(Diva.Core.Models.TenantContext? tenant = null)
        => new(_options, tenant?.TenantId ?? 0);
    public Task ApplyMigrationsAsync() => Task.CompletedTask;
}
