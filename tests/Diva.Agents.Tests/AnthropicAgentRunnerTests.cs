using Anthropic.SDK.Messaging;
using Diva.Agents.Tests.Helpers;
using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Microsoft.AspNetCore.Http;
using Diva.Infrastructure.Data.Entities;
using Diva.Infrastructure.LiteLLM;
using Diva.Infrastructure.Learning;
using Diva.Infrastructure.Sessions;
using Diva.Infrastructure.Verification;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Diva.Agents.Tests;

/// <summary>
/// Unit tests for AnthropicAgentRunner — uses in-memory SQLite for session management
/// and mocked LLM providers so no real API keys are needed.
/// </summary>
public class AnthropicAgentRunnerTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DivaDbContext _db;
    private readonly DbContextOptions<DivaDbContext> _dbOptions;
    private readonly AnthropicAgentRunner _runner;
    private readonly IAnthropicProvider _anthropic;
    private readonly McpClientCache _cache;

    public AnthropicAgentRunnerTests()
    {
        // ── Real in-memory SQLite (per ADR-010: no mocked DivaDbContext) ─────
        // Keep the connection alive so multiple DbContext instances share the same DB.
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _dbOptions = new DbContextOptionsBuilder<DivaDbContext>()
            .UseSqlite(_connection)
            .Options;
        var dbOptions = _dbOptions;
        _db = new DivaDbContext(dbOptions);
        _db.Database.EnsureCreated();

        var factory = new TestDatabaseProviderFactory(dbOptions);
        var sessions = new AgentSessionService(factory, NullLogger<AgentSessionService>.Instance);

        // ── LLM mocks ─────────────────────────────────────────────────────────
        _anthropic = Substitute.For<IAnthropicProvider>();
        var openAi = Substitute.For<IOpenAiProvider>();

        // ── Verifier with Off mode — no LLM call during these tests ─────────
        var verifier = new ResponseVerifier(
            AgentTestFixtures.Opts(new VerificationOptions { Mode = "Off" }),
            AgentTestFixtures.AnthropicLlm(),
            Substitute.For<IAnthropicProvider>(),
            Substitute.For<IOpenAiProvider>(),
            NullLogger<ResponseVerifier>.Instance);

        var ruleLearner = Substitute.For<IRuleLearningService>();
        ruleLearner.ExtractRulesFromConversationAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);

        _cache = new McpClientCache();

        _runner = new AnthropicAgentRunner(
            AgentTestFixtures.AnthropicLlm(),
            AgentTestFixtures.Opts(new AgentOptions { EnableResponseStreaming = false }),
            AgentTestFixtures.Opts(new VerificationOptions { Mode = "Off" }),
            sessions,
            verifier,
            ruleLearner,
            _cache,
            _anthropic,
            openAi,
            ContextWindowTestHelpers.NoOpCtx(),
            Substitute.For<IHttpContextAccessor>(),
            new ToolExecutor(NullLogger<ToolExecutor>.Instance, Options.Create(new AgentOptions())),
            NullLogger<AnthropicAgentRunner>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        await _cache.DisposeAsync();
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // ── Tests ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_AnthropicPath_ReturnsSuccessWithContent()
    {
        _anthropic.GetClaudeMessageAsync(Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>())
            .Returns(MakeTextResponse("Hello from the test model!"));

        var result = await _runner.RunAsync(
            AgentTestFixtures.BasicAgent(),
            AgentTestFixtures.BasicRequest("hi"),
            AgentTestFixtures.BasicTenant(),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("Hello from the test model!", result.Content);
    }

    [Fact]
    public async Task RunAsync_CreatesNewSession_SessionIdPresentInResult()
    {
        _anthropic.GetClaudeMessageAsync(Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>())
            .Returns(MakeTextResponse("Hi!"));

        var result = await _runner.RunAsync(
            AgentTestFixtures.BasicAgent(),
            AgentTestFixtures.BasicRequest(),
            AgentTestFixtures.BasicTenant(),
            CancellationToken.None);

        Assert.NotEmpty(result.SessionId ?? "");
    }

    [Fact]
    public async Task RunAsync_VerificationOff_VerificationModeIsOff()
    {
        _anthropic.GetClaudeMessageAsync(Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>())
            .Returns(MakeTextResponse("Revenue was $24,500 last month."));

        var result = await _runner.RunAsync(
            AgentTestFixtures.BasicAgent(),
            AgentTestFixtures.BasicRequest("What was last month's revenue?"),
            AgentTestFixtures.BasicTenant(),
            CancellationToken.None);

        // Verification mode Off → result is skipped, no blocking
        Assert.True(result.Success);
        Assert.Equal("Off", result.Verification?.Mode ?? "Off");
        Assert.False(result.Verification?.WasBlocked ?? false);
    }

    [Fact]
    public async Task RunAsync_AnthropicCalled_ExactlyOnce_ForSimpleQuery()
    {
        _anthropic.GetClaudeMessageAsync(Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>())
            .Returns(MakeTextResponse("Hello!"));

        await _runner.RunAsync(
            AgentTestFixtures.BasicAgent(),
            AgentTestFixtures.BasicRequest("hi"),
            AgentTestFixtures.BasicTenant(),
            CancellationToken.None);

        await _anthropic.Received(1).GetClaudeMessageAsync(
            Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_OnInitHookMutatesPrompt_UpdatedPromptSentToProvider()
    {
        var hookPipeline = Substitute.For<IAgentHookPipeline>();
        hookPipeline.ResolveHooks(Arg.Any<Dictionary<string, string>>(), Arg.Any<string>(), Arg.Any<TenantContext>())
            .Returns((new List<IAgentLifecycleHook> { Substitute.For<IAgentLifecycleHook>() }, Substitute.For<IDisposable>()));
        hookPipeline.RunOnInitAsync(Arg.Any<List<IAgentLifecycleHook>>(), Arg.Any<AgentHookContext>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callInfo.Arg<AgentHookContext>().SystemPrompt += "\n\nAdded from init hook.";
                return Task.CompletedTask;
            });

        _anthropic.GetClaudeMessageAsync(Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>())
            .Returns(MakeTextResponse("Hello from the test model!"));

        var runner = CreateRunnerWith(hookPipeline: hookPipeline);
        var agent = AgentTestFixtures.BasicAgent();
        agent.HooksJson = "{\"OnInit\":\"TenantRulePackHook\"}";

        await runner.RunAsync(agent, AgentTestFixtures.BasicRequest("hi"), AgentTestFixtures.BasicTenant(), CancellationToken.None);

        await _anthropic.Received(1).GetClaudeMessageAsync(
            Arg.Is<MessageParameters>(p => p.System != null && p.System.Any(s => s.Text.Contains("Added from init hook."))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_OnBeforeIterationHookMutatesPrompt_UpdatedPromptSentToProvider()
    {
        var hookPipeline = Substitute.For<IAgentHookPipeline>();
        hookPipeline.ResolveHooks(Arg.Any<Dictionary<string, string>>(), Arg.Any<string>(), Arg.Any<TenantContext>())
            .Returns((new List<IAgentLifecycleHook> { Substitute.For<IAgentLifecycleHook>() }, Substitute.For<IDisposable>()));
        hookPipeline.RunOnBeforeIterationAsync(Arg.Any<List<IAgentLifecycleHook>>(), Arg.Any<AgentHookContext>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                callInfo.Arg<AgentHookContext>().SystemPrompt += "\n\nAdded from iteration hook.";
                return Task.CompletedTask;
            });

        _anthropic.GetClaudeMessageAsync(Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>())
            .Returns(MakeTextResponse("Hello from the test model!"));

        var runner = CreateRunnerWith(hookPipeline: hookPipeline);
        var agent = AgentTestFixtures.BasicAgent();
        agent.HooksJson = "{\"OnBeforeIteration\":\"TenantRulePackHook\"}";

        await runner.RunAsync(agent, AgentTestFixtures.BasicRequest("hi"), AgentTestFixtures.BasicTenant(), CancellationToken.None);

        await _anthropic.Received(1).GetClaudeMessageAsync(
            Arg.Is<MessageParameters>(p => p.System != null && p.System.Any(s => s.Text.Contains("Added from iteration hook."))),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunAsync_LlmFailure_InvokesOnErrorHook()
    {
        var hookPipeline = Substitute.For<IAgentHookPipeline>();
        hookPipeline.ResolveHooks(Arg.Any<Dictionary<string, string>>(), Arg.Any<string>(), Arg.Any<TenantContext>())
            .Returns((new List<IAgentLifecycleHook> { Substitute.For<IAgentLifecycleHook>() }, Substitute.For<IDisposable>()));
        hookPipeline.RunOnErrorAsync(Arg.Any<List<IAgentLifecycleHook>>(), Arg.Any<AgentHookContext>(), Arg.Any<string?>(), Arg.Any<Exception>(), Arg.Any<CancellationToken>())
            .Returns(ErrorRecoveryAction.Abort);

        _anthropic.GetClaudeMessageAsync(Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<MessageResponse>(new InvalidOperationException("llm failure")));

        var runner = CreateRunnerWith(hookPipeline: hookPipeline);
        var agent = AgentTestFixtures.BasicAgent();
        agent.HooksJson = "{\"OnError\":\"TenantRulePackHook\"}";

        var result = await runner.RunAsync(agent, AgentTestFixtures.BasicRequest("hi"), AgentTestFixtures.BasicTenant(), CancellationToken.None);

        Assert.False(result.Success);
        await hookPipeline.Received(1).RunOnErrorAsync(
            Arg.Any<List<IAgentLifecycleHook>>(),
            Arg.Any<AgentHookContext>(),
            Arg.Any<string?>(),
            Arg.Any<Exception>(),
            Arg.Any<CancellationToken>());
    }

    // ── ILlmConfigResolver integration ────────────────────────────────────────

    /// <summary>
    /// When the resolver returns a model that differs from the global LlmOptions default,
    /// the runner must use that resolved model in the MessageParameters sent to the provider.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithResolver_ResolvedModelUsedInMessageParameters()
    {
        var resolver = Substitute.For<ILlmConfigResolver>();
        resolver.ResolveAsync(Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedLlmConfig("Anthropic", "test-key", "claude-opus-4-6", null, null, []));

        _anthropic.GetClaudeMessageAsync(
                Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(MakeTextResponse("ok"));

        var runner = CreateRunnerWith(resolver);
        await runner.RunAsync(
            AgentTestFixtures.BasicAgent(),
            AgentTestFixtures.BasicRequest("hello"),
            AgentTestFixtures.BasicTenant(1),
            CancellationToken.None);

        // "claude-opus-4-6" differs from opts "claude-sonnet-4-20250514" → used in call
        await _anthropic.Received(1).GetClaudeMessageAsync(
            Arg.Is<MessageParameters>(p => p.Model == "claude-opus-4-6"),
            Arg.Any<CancellationToken>(),
            Arg.Any<string?>());
    }

    /// <summary>
    /// When the resolved API key differs from the global opts key, the runner must pass it
    /// as apiKeyOverride so the provider creates a scoped client with that key.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithResolver_DifferentApiKey_OverridePassedToProvider()
    {
        var resolver = Substitute.For<ILlmConfigResolver>();
        // "resolved-key" != opts "test-key" → override must be forwarded
        resolver.ResolveAsync(Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedLlmConfig("Anthropic", "resolved-key", "claude-sonnet-4-20250514", null, null, []));

        _anthropic.GetClaudeMessageAsync(
                Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(MakeTextResponse("ok"));

        var runner = CreateRunnerWith(resolver);
        await runner.RunAsync(
            AgentTestFixtures.BasicAgent(),
            AgentTestFixtures.BasicRequest("hello"),
            AgentTestFixtures.BasicTenant(1),
            CancellationToken.None);

        await _anthropic.Received(1).GetClaudeMessageAsync(
            Arg.Any<MessageParameters>(),
            Arg.Any<CancellationToken>(),
            Arg.Is<string?>(s => s == "resolved-key"));
    }

    /// <summary>
    /// When the resolved API key is identical to opts, no override is passed (null).
    /// This avoids the cost of creating a new AnthropicClient on every call.
    /// </summary>
    [Fact]
    public async Task RunAsync_WithResolver_SameApiKeyAsOpts_NoOverridePassed()
    {
        var resolver = Substitute.For<ILlmConfigResolver>();
        // Same key as opts ("test-key") → override should be null
        resolver.ResolveAsync(Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ResolvedLlmConfig("Anthropic", "test-key", "claude-sonnet-4-20250514", null, null, []));

        _anthropic.GetClaudeMessageAsync(Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>())
            .Returns(MakeTextResponse("ok"));

        var runner = CreateRunnerWith(resolver);
        await runner.RunAsync(
            AgentTestFixtures.BasicAgent(),
            AgentTestFixtures.BasicRequest("hello"),
            AgentTestFixtures.BasicTenant(1),
            CancellationToken.None);

        // null override = no new AnthropicClient
        await _anthropic.Received(1).GetClaudeMessageAsync(
            Arg.Any<MessageParameters>(),
            Arg.Any<CancellationToken>(),
            Arg.Is<string?>(s => s == null));
    }

    /// <summary>
    /// A resolver failure must not crash the run. The runner logs a warning and
    /// silently falls back to the global LlmOptions (appsettings) values.
    /// </summary>
    [Fact]
    public async Task RunAsync_ResolverThrows_FallsBackToOptsAndSucceeds()
    {
        var resolver = Substitute.For<ILlmConfigResolver>();
        resolver.ResolveAsync(Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ResolvedLlmConfig>(new InvalidOperationException("resolver failure")));

        _anthropic.GetClaudeMessageAsync(Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>())
            .Returns(MakeTextResponse("fallback response"));

        var runner = CreateRunnerWith(resolver);
        var result = await runner.RunAsync(
            AgentTestFixtures.BasicAgent(),
            AgentTestFixtures.BasicRequest("hello"),
            AgentTestFixtures.BasicTenant(1),
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("fallback response", result.Content);
        // No override — resolved was null due to exception, so opts key used as-is
        await _anthropic.Received(1).GetClaudeMessageAsync(
            Arg.Any<MessageParameters>(),
            Arg.Any<CancellationToken>(),
            Arg.Is<string?>(s => s == null));
    }

    // ── max_tokens handling ───────────────────────────────────────────────────

    /// <summary>
    /// When the LLM consistently returns max_tokens, the runner nudges once then
    /// accepts the partial response rather than looping forever.
    /// </summary>
    [Fact]
    public async Task RunAsync_MaxTokensStopReason_NudgesOnce_ThenAcceptsPartial()
    {
        _anthropic.GetClaudeMessageAsync(Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>())
            .Returns(MakeMaxTokensResponse("partial response text"));

        var result = await _runner.RunAsync(
            AgentTestFixtures.BasicAgent(),
            AgentTestFixtures.BasicRequest("summarise everything"),
            AgentTestFixtures.BasicTenant(),
            CancellationToken.None);

        // Should succeed with the partial content
        Assert.True(result.Success);
        Assert.Equal("partial response text", result.Content);

        // Exactly 2 calls: initial call + one nudge (maxTokensNudgeRetries = 1)
        await _anthropic.Received(2).GetClaudeMessageAsync(
            Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// When an OnError hook returns Abort on max_tokens, the partial response is
    /// accepted immediately without nudging (only 1 LLM call total).
    /// </summary>
    [Fact]
    public async Task RunAsync_MaxTokensWithAbortHook_AcceptsPartialImmediately()
    {
        var hookPipeline = Substitute.For<IAgentHookPipeline>();
        hookPipeline.ResolveHooks(Arg.Any<Dictionary<string, string>>(), Arg.Any<string>(), Arg.Any<TenantContext>())
            .Returns((new List<IAgentLifecycleHook> { Substitute.For<IAgentLifecycleHook>() }, Substitute.For<IDisposable>()));
        hookPipeline.RunOnErrorAsync(
                Arg.Any<List<IAgentLifecycleHook>>(), Arg.Any<AgentHookContext>(),
                Arg.Any<string?>(), Arg.Any<Exception>(), Arg.Any<CancellationToken>())
            .Returns(ErrorRecoveryAction.Abort);
        // Pass finalResponse through unchanged so it isn't lost in the BeforeResponse hook
        hookPipeline.RunOnBeforeResponseAsync(
                Arg.Any<List<IAgentLifecycleHook>>(), Arg.Any<AgentHookContext>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.ArgAt<string>(2));

        _anthropic.GetClaudeMessageAsync(Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>())
            .Returns(MakeMaxTokensResponse("truncated output"));

        var agent = AgentTestFixtures.BasicAgent();
        agent.HooksJson = "{\"OnError\":\"TenantRulePackHook\"}";

        var runner = CreateRunnerWith(hookPipeline: hookPipeline);
        var result = await runner.RunAsync(agent, AgentTestFixtures.BasicRequest("hi"), AgentTestFixtures.BasicTenant(), CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("truncated output", result.Content);

        // Abort → accept immediately, no nudge → only 1 LLM call
        await _anthropic.Received(1).GetClaudeMessageAsync(
            Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// WasTruncated must be true in the AgentHookContext when the OnError hook fires
    /// for a max_tokens stop reason.
    /// </summary>
    [Fact]
    public async Task RunAsync_MaxTokensStopReason_WasTruncatedSetInHookContext()
    {
        // Capture the flag value at call time, not the object reference (it's reset after Abort)
        bool capturedWasTruncated = false;

        var hookPipeline = Substitute.For<IAgentHookPipeline>();
        hookPipeline.ResolveHooks(Arg.Any<Dictionary<string, string>>(), Arg.Any<string>(), Arg.Any<TenantContext>())
            .Returns((new List<IAgentLifecycleHook> { Substitute.For<IAgentLifecycleHook>() }, Substitute.For<IDisposable>()));
        hookPipeline.RunOnErrorAsync(
                Arg.Any<List<IAgentLifecycleHook>>(), Arg.Any<AgentHookContext>(),
                Arg.Any<string?>(), Arg.Any<Exception>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                capturedWasTruncated = callInfo.Arg<AgentHookContext>().WasTruncated;
                return ErrorRecoveryAction.Abort;
            });
        hookPipeline.RunOnBeforeResponseAsync(
                Arg.Any<List<IAgentLifecycleHook>>(), Arg.Any<AgentHookContext>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => callInfo.ArgAt<string>(2));

        _anthropic.GetClaudeMessageAsync(Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>())
            .Returns(MakeMaxTokensResponse("cutoff"));

        var agent = AgentTestFixtures.BasicAgent();
        agent.HooksJson = "{\"OnError\":\"TenantRulePackHook\"}";

        var runner = CreateRunnerWith(hookPipeline: hookPipeline);
        await runner.RunAsync(agent, AgentTestFixtures.BasicRequest("hi"), AgentTestFixtures.BasicTenant(), CancellationToken.None);

        Assert.True(capturedWasTruncated);
    }

    // ── Per-agent MaxOutputTokens ──────────────────────────────────────────────

    /// <summary>
    /// When an agent sets MaxOutputTokens, that value overrides the global AgentOptions
    /// and is forwarded to the LLM provider strategy.
    /// </summary>
    [Fact]
    public async Task RunAsync_PerAgentMaxOutputTokens_PassedToProvider()
    {
        _anthropic.GetClaudeMessageAsync(Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>())
            .Returns(MakeTextResponse("ok"));

        var agent = AgentTestFixtures.BasicAgent();
        agent.MaxOutputTokens = 512;

        await _runner.RunAsync(agent, AgentTestFixtures.BasicRequest("hi"), AgentTestFixtures.BasicTenant(), CancellationToken.None);

        await _anthropic.Received(1).GetClaudeMessageAsync(
            Arg.Is<MessageParameters>(p => p.MaxTokens == 512),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// When an agent does not set MaxOutputTokens, the global AgentOptions value is used.
    /// </summary>
    [Fact]
    public async Task RunAsync_NoPerAgentMaxOutputTokens_UsesGlobalDefault()
    {
        _anthropic.GetClaudeMessageAsync(Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>())
            .Returns(MakeTextResponse("ok"));

        var agent = AgentTestFixtures.BasicAgent();
        // MaxOutputTokens is null — should fall back to AgentOptions.MaxOutputTokens (8192 default)

        await _runner.RunAsync(agent, AgentTestFixtures.BasicRequest("hi"), AgentTestFixtures.BasicTenant(), CancellationToken.None);

        await _anthropic.Received(1).GetClaudeMessageAsync(
            Arg.Is<MessageParameters>(p => p.MaxTokens == new AgentOptions().MaxOutputTokens),
            Arg.Any<CancellationToken>());
    }

    // ── Helper ───────────────────────────────────────────────────────────────

    /// <summary>Creates a runner with optional resolver and hook pipeline injected.</summary>
    private AnthropicAgentRunner CreateRunnerWith(
        ILlmConfigResolver? resolver = null,
        IAgentHookPipeline? hookPipeline = null)
    {
        var factory  = new TestDatabaseProviderFactory(_dbOptions);
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

        return new AnthropicAgentRunner(
            AgentTestFixtures.AnthropicLlm(),
            AgentTestFixtures.Opts(new AgentOptions { EnableResponseStreaming = false }),
            AgentTestFixtures.Opts(new VerificationOptions { Mode = "Off" }),
            sessions,
            verifier,
            ruleLearner,
            _cache,
            _anthropic,
            Substitute.For<IOpenAiProvider>(),
            ContextWindowTestHelpers.NoOpCtx(),
            Substitute.For<IHttpContextAccessor>(),
            new ToolExecutor(NullLogger<ToolExecutor>.Instance, Options.Create(new AgentOptions())),
            NullLogger<AnthropicAgentRunner>.Instance,
                resolver: resolver,
                hookPipeline: hookPipeline);
    }

    private static MessageResponse MakeTextResponse(string text) => new()
    {
        Content    = [new Anthropic.SDK.Messaging.TextContent { Text = text }],
        StopReason = "end_turn",
        Model      = "claude-sonnet-4-20250514"
    };

    private static MessageResponse MakeMaxTokensResponse(string text) => new()
    {
        Content    = [new Anthropic.SDK.Messaging.TextContent { Text = text }],
        StopReason = "max_tokens",
        Model      = "claude-sonnet-4-20250514"
    };
}

/// <summary>
/// In-memory SQLite factory for tests — per ADR-010 (real SQLite, no mocked DbContext).
/// Each CreateDbContext() call returns a NEW instance sharing the same underlying connection
/// so that services using `using var db = factory.CreateDbContext()` don't dispose the shared DB.
/// </summary>
internal sealed class TestDatabaseProviderFactory : IDatabaseProviderFactory
{
    private readonly DbContextOptions<DivaDbContext> _options;
    public TestDatabaseProviderFactory(DbContextOptions<DivaDbContext> options) => _options = options;

    public DivaDbContext CreateDbContext(TenantContext? tenant = null)
        => new(_options, tenant?.TenantId ?? 0);

    public Task ApplyMigrationsAsync() => Task.CompletedTask;
}
