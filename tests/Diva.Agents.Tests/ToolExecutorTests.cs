using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Infrastructure.LiteLLM;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using NSubstitute;

namespace Diva.Agents.Tests;

/// <summary>
/// Unit tests for <see cref="ToolExecutor"/>.
/// Uses empty/null client maps to exercise the error/exception paths without a real MCP server.
/// </summary>
public class ToolExecutorTests
{
    private readonly ToolExecutor _sut = new(
        NullLogger<ToolExecutor>.Instance,
        Options.Create(new AgentOptions()));

    // ── No client available ───────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NoClientInMap_NoFallbackClients_ReturnsFailed()
    {
        var result = await _sut.ExecuteAsync(
            toolName:          "my_tool",
            inputJson:         "{}",
            toolClientMap:     [],
            mcpClients:        [],
            maxToolResultChars: 4000,
            ct:                CancellationToken.None);

        Assert.True(result.Failed);
        Assert.NotNull(result.Error);
        Assert.Contains("Error:", result.Output);
    }

    [Fact]
    public async Task ExecuteAsync_ToolNotInMap_FallsBackToFirstClient_ReturnsErrorWhenNoClients()
    {
        // toolClientMap has no entry for "unknown_tool" and mcpClients is also empty
        var result = await _sut.ExecuteAsync(
            toolName:          "unknown_tool",
            inputJson:         "{}",
            toolClientMap:     new Dictionary<string, McpClient>(),
            mcpClients:        new Dictionary<string, McpClient>(),
            maxToolResultChars: 4000,
            ct:                CancellationToken.None);

        Assert.True(result.Failed);
        Assert.StartsWith("Error:", result.Output);
    }

    // ── Cancellation / timeout ────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_PreCancelledToken_ReturnsTimeoutMessage()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var result = await _sut.ExecuteAsync(
            toolName:          "slow_tool",
            inputJson:         "{}",
            toolClientMap:     [],
            mcpClients:        [],
            maxToolResultChars: 4000,
            ct:                cts.Token);

        // Pre-cancelled CT may either hit the timeout path or the exception path,
        // but either way the call must complete and Failed must be true.
        Assert.True(result.Failed);
        Assert.NotEmpty(result.Output);
    }

    // ── Output truncation ─────────────────────────────────────────────────────

    [Fact]
    public void TruncateResult_Applied_WhenOutputExceedsMax()
    {
        // The internal truncation is exercised via ReActToolHelper — verify the helper
        // returns a string that fits within the requested limit.
        var longText = new string('A', 10_000);
        var truncated = ReActToolHelper.TruncateResult(longText, 500);

        // Must fit within the limit (allowing for the truncation suffix)
        Assert.True(truncated.Length <= 600, $"Truncated length was {truncated.Length}");
        Assert.Contains("[truncated", truncated);
    }

    // ── Error classification ──────────────────────────────────────────────────

    [Theory]
    [InlineData("Error: connection refused", true)]
    [InlineData("Error: tool not found", true)]
    [InlineData("{\"status\":\"error\",\"msg\":\"foo\"}", true)]
    [InlineData("42 results found", false)]
    [InlineData("Success", false)]
    public void IsToolOutputError_CorrectlyClassifies(string output, bool expectedFailed)
    {
        var isError = ReActToolHelper.IsToolOutputError(output);
        Assert.Equal(expectedFailed, isError);
    }

    // ── MayAutoRetry ──────────────────────────────────────────────────────────

    private static IReadOnlyList<(string ToolName, string InputJson, bool Failed)> Breakdown(
        params (string Name, bool Failed)[] calls) =>
        calls.Select(c => (c.Name, "{}", c.Failed)).ToList();

    [Fact]
    public void MayAutoRetry_FailedReadOnlyTool_IsAllowed()
    {
        var readOnly = new Dictionary<string, bool> { ["search_tee_times"] = true };

        Assert.True(ReActToolHelper.MayAutoRetry(Breakdown(("search_tee_times", true)), readOnly));
    }

    [Fact]
    public void MayAutoRetry_FailedWriteTool_IsRefused()
    {
        var readOnly = new Dictionary<string, bool> { ["book_tee_time"] = false };

        Assert.False(ReActToolHelper.MayAutoRetry(Breakdown(("book_tee_time", true)), readOnly));
    }

    [Fact]
    public void MayAutoRetry_UnknownTool_IsRefused()
    {
        Assert.False(ReActToolHelper.MayAutoRetry(
            Breakdown(("unlisted_tool", true)), new Dictionary<string, bool>()));
    }

    [Fact]
    public void MayAutoRetry_WriteToolThatSucceeded_DoesNotBlockReadOnlyRetry()
    {
        var readOnly = new Dictionary<string, bool>
        {
            ["book_tee_time"] = false,
            ["search_tee_times"] = true,
        };

        Assert.True(ReActToolHelper.MayAutoRetry(
            Breakdown(("book_tee_time", false), ("search_tee_times", true)), readOnly));
    }

    [Fact]
    public void MayAutoRetry_MixedFailures_IsRefused()
    {
        var readOnly = new Dictionary<string, bool>
        {
            ["book_tee_time"] = false,
            ["search_tee_times"] = true,
        };

        Assert.False(ReActToolHelper.MayAutoRetry(
            Breakdown(("search_tee_times", true), ("book_tee_time", true)), readOnly));
    }

    [Fact]
    public void MayAutoRetry_NoBreakdown_IsRefused()
    {
        Assert.False(ReActToolHelper.MayAutoRetry(null, new Dictionary<string, bool>()));
        Assert.False(ReActToolHelper.MayAutoRetry(Breakdown(), new Dictionary<string, bool>()));
    }
}
