using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Optimization;
using Diva.Infrastructure.Sessions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Diva.Agents.Tests;

/// <summary>
/// Regression tests for SessionTraceWriter's tool_call/tool_result pairing.
/// Covers the bug where two calls to the same tool name within one iteration
/// (e.g. parallel calls with different arguments) could have their inputs and
/// outputs cross-assigned when results arrived out of announcement order.
/// </summary>
public class SessionTraceWriterTests : IAsyncDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<SessionTraceDbContext> _opts;
    private readonly SessionTraceWriter _writer;

    public SessionTraceWriterTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _opts = new DbContextOptionsBuilder<SessionTraceDbContext>()
            .UseSqlite(_connection)
            .Options;
        using (var db = new SessionTraceDbContext(_opts))
            db.Database.EnsureCreated();

        var lifetime = Substitute.For<IHostApplicationLifetime>();
        lifetime.ApplicationStopping.Returns(CancellationToken.None);

        _writer = new SessionTraceWriter(
            new SessionTraceDbContext(_opts),
            Substitute.For<ITurnScoringService>(),
            Options.Create(new AgentOptions { Optimization = new OptimizationOptions { EnablePerTurnScoring = false } }),
            lifetime,
            NullLogger<SessionTraceWriter>.Instance);
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task CaptureChunk_SameToolNameTwiceOutOfOrder_MatchesByToolCallId()
    {
        const string sessionId = "sess-1";
        await _writer.EnsureSessionAsync(sessionId, null,
            new TenantContext { TenantId = 1, UserId = "user-1" },
            "agent-1", "Test Agent", isSupervisor: false, CancellationToken.None);

        _writer.CaptureChunk(new AgentStreamChunk { Type = "iteration_start", Iteration = 1 });
        _writer.CaptureChunk(new AgentStreamChunk { Type = "tool_call", ToolName = "search", ToolCallId = "call_A", ToolInput = "{\"q\":\"A\"}" });
        _writer.CaptureChunk(new AgentStreamChunk { Type = "tool_call", ToolName = "search", ToolCallId = "call_B", ToolInput = "{\"q\":\"B\"}" });

        // Results arrive in REVERSE order of the calls — this is what previously
        // triggered cross-assignment when matching relied on LastOrDefault(name).
        _writer.CaptureChunk(new AgentStreamChunk { Type = "tool_result", ToolName = "search", ToolCallId = "call_B", ToolOutput = "Result for B" });
        _writer.CaptureChunk(new AgentStreamChunk { Type = "tool_result", ToolName = "search", ToolCallId = "call_A", ToolOutput = "Result for A" });

        await _writer.FlushTurnAsync(sessionId, turnNumber: 1, "hi", "done", 100, "agent-1", "model-1", "Anthropic", CancellationToken.None);

        await using var db = new SessionTraceDbContext(_opts);
        var toolCalls = await db.TraceToolCalls.OrderBy(tc => tc.Sequence).ToListAsync();

        Assert.Equal(2, toolCalls.Count);
        Assert.Equal("{\"q\":\"A\"}", toolCalls[0].ToolInput);
        Assert.Equal("Result for A", toolCalls[0].ToolOutput);
        Assert.Equal("{\"q\":\"B\"}", toolCalls[1].ToolInput);
        Assert.Equal("Result for B", toolCalls[1].ToolOutput);
    }

    [Fact]
    public async Task CaptureChunk_NoToolCallId_FallsBackToFifoOrder()
    {
        const string sessionId = "sess-2";
        await _writer.EnsureSessionAsync(sessionId, null,
            new TenantContext { TenantId = 1, UserId = "user-1" },
            "agent-1", "Test Agent", isSupervisor: false, CancellationToken.None);

        _writer.CaptureChunk(new AgentStreamChunk { Type = "iteration_start", Iteration = 1 });
        _writer.CaptureChunk(new AgentStreamChunk { Type = "tool_call", ToolName = "search", ToolInput = "{\"q\":\"A\"}" });
        _writer.CaptureChunk(new AgentStreamChunk { Type = "tool_call", ToolName = "search", ToolInput = "{\"q\":\"B\"}" });

        // No ToolCallId on either side (e.g. older/other caller) — results still
        // arrive in the same order the calls were announced (FIFO), which the
        // fallback matching must honor.
        _writer.CaptureChunk(new AgentStreamChunk { Type = "tool_result", ToolName = "search", ToolOutput = "Result for A" });
        _writer.CaptureChunk(new AgentStreamChunk { Type = "tool_result", ToolName = "search", ToolOutput = "Result for B" });

        await _writer.FlushTurnAsync(sessionId, turnNumber: 1, "hi", "done", 100, "agent-1", "model-1", "Anthropic", CancellationToken.None);

        await using var db = new SessionTraceDbContext(_opts);
        var toolCalls = await db.TraceToolCalls.OrderBy(tc => tc.Sequence).ToListAsync();

        Assert.Equal(2, toolCalls.Count);
        Assert.Equal("{\"q\":\"A\"}", toolCalls[0].ToolInput);
        Assert.Equal("Result for A", toolCalls[0].ToolOutput);
        Assert.Equal("{\"q\":\"B\"}", toolCalls[1].ToolInput);
        Assert.Equal("Result for B", toolCalls[1].ToolOutput);
    }

    [Fact]
    public async Task CaptureChunk_ToolResultCarriesDurationMs_UsesRunnerMeasuredValueNotCaptureGap()
    {
        // tool_call and tool_result are announced/resolved as an already-completed batch
        // (parallel tool execution), so the wall-clock gap between capturing them here is
        // near-zero regardless of how long the tool actually took. The runner's own
        // authoritative measurement (chunk.ToolDurationMs) must win over that gap.
        const string sessionId = "sess-3";
        await _writer.EnsureSessionAsync(sessionId, null,
            new TenantContext { TenantId = 1, UserId = "user-1" },
            "agent-1", "Test Agent", isSupervisor: false, CancellationToken.None);

        _writer.CaptureChunk(new AgentStreamChunk { Type = "iteration_start", Iteration = 1 });
        _writer.CaptureChunk(new AgentStreamChunk { Type = "tool_call", ToolName = "search", ToolCallId = "call_A", ToolInput = "{\"q\":\"A\"}" });
        _writer.CaptureChunk(new AgentStreamChunk { Type = "tool_result", ToolName = "search", ToolCallId = "call_A", ToolOutput = "Result for A", ToolDurationMs = 4200 });

        await _writer.FlushTurnAsync(sessionId, turnNumber: 1, "hi", "done", 100, "agent-1", "model-1", "Anthropic", CancellationToken.None);

        await using var db = new SessionTraceDbContext(_opts);
        var toolCall = await db.TraceToolCalls.SingleAsync();
        Assert.Equal(4200, toolCall.DurationMs);
    }
}

