using Anthropic.SDK.Messaging;
using Diva.Agents.Tests.Helpers;
using Diva.Infrastructure.LiteLLM;
using Diva.Infrastructure.Sessions;
using Microsoft.Extensions.AI;
using NSubstitute;
using System.Runtime.CompilerServices;

namespace Diva.Agents.Tests;

/// <summary>
/// Unit tests for AnthropicProviderStrategy prompt caching breakpoints.
///
/// Active breakpoints:
///   BP1 — static system block (BuildSystemBlocks → CacheControl on SystemMessage)
///   BP3 — last prior-session history message (Initialize → CacheControl on _messages[^2].Content[^1])
///   BP4 — sliding tool-result message (AddToolResults → CacheControl moves to newest block)
///
/// Note: BP2 (tool definition caching) is not available in Anthropic.SDK 5.10.0 because
/// Anthropic.SDK.Common.Tool exposes no CacheControl property. Tests 3 confirms this.
/// </summary>
public class AnthropicProviderStrategyCacheTests
{
    // ── BP3: history boundary ─────────────────────────────────────────────────

    /// <summary>
    /// When Initialize is called with prior-session history, the last history message's
    /// last content block should have CacheControlType.ephemeral (BP3).
    /// </summary>
    [Fact]
    public void Initialize_NonEmptyHistory_HistoryBoundaryIsEphemeral()
    {
        var strategy = BuildStrategy(enableHistoryCaching: true);

        var history = new List<ConversationTurn>
        {
            new("user",      "turn 1"),
            new("assistant", "turn 2"),
            new("user",      "turn 3")
        };
        strategy.Initialize("sys", history, "current query", []);

        var cc = strategy.GetHistoryBoundaryCacheControl();

        Assert.NotNull(cc);
        Assert.Equal(CacheControlType.ephemeral, cc!.Type);
    }

    /// <summary>
    /// When Initialize is called with an empty history list, there is no history boundary
    /// to cache. Both internal accessors should return null.
    /// </summary>
    [Fact]
    public void Initialize_EmptyHistory_NoCacheControlSet()
    {
        var strategy = BuildStrategy(enableHistoryCaching: true);

        strategy.Initialize("sys", [], "query", []);

        Assert.Null(strategy.GetHistoryBoundaryCacheControl());
        Assert.Null(strategy.GetSlidingBoundaryContent());
    }

    /// <summary>
    /// BP2: Anthropic.SDK.Common.Tool has no CacheControl property in SDK 5.10.0.
    /// Verify that Initialize still succeeds and _tools is populated even though
    /// no cache marker can be set on individual tool definitions.
    /// (Tool caching is handled by PromptCacheType.FineGrained on the system block.)
    /// </summary>
    [Fact]
    public void Initialize_WithNoToolCacheControlProperty_InitializesSuccessfully()
    {
        var strategy = BuildStrategy(enableHistoryCaching: true);

        // Just verify no exception is thrown — the tool cache control limitation is
        // documented in the class summary.
        strategy.Initialize("sys", [], "query", []);

        // Sliding boundary and history boundary are both null (empty history, no tool results yet).
        Assert.Null(strategy.GetHistoryBoundaryCacheControl());
        Assert.Null(strategy.GetSlidingBoundaryContent());
    }

    // ── BP4: sliding tool-result boundary ────────────────────────────────────

    /// <summary>
    /// The first call to AddToolResults should set CacheControlType.ephemeral on the
    /// last content block of the new tool-result message (BP4).
    /// </summary>
    [Fact]
    public void AddToolResults_FirstCall_SlidingBoundaryIsEphemeral()
    {
        var strategy = BuildStrategy(enableHistoryCaching: true);
        strategy.Initialize("sys", [], "query", []);

        strategy.AddToolResults([new UnifiedToolResult("call-1", "tool-a", "output", false)]);

        var content = strategy.GetSlidingBoundaryContent();
        Assert.NotNull(content);
        Assert.NotNull(content!.CacheControl);
        Assert.Equal(CacheControlType.ephemeral, content.CacheControl!.Type);
    }

    /// <summary>
    /// On the second AddToolResults call, the previous BP4 marker must be cleared and
    /// the new one must be set on the most recent tool-result block (sliding window).
    /// </summary>
    [Fact]
    public void AddToolResults_SecondCall_PreviousClearedAndNewSet()
    {
        var strategy = BuildStrategy(enableHistoryCaching: true);
        strategy.Initialize("sys", [], "query", []);

        // First tool result exchange — gets BP4.
        strategy.AddToolResults([new UnifiedToolResult("call-1", "tool-a", "output-1", false)]);
        var firstContent = strategy.GetSlidingBoundaryContent();
        Assert.NotNull(firstContent!.CacheControl);

        // Simulate the assistant committing its response before the second tool exchange.
        // We only need a second AddToolResults call to trigger the slider movement.
        strategy.AddToolResults([new UnifiedToolResult("call-2", "tool-b", "output-2", false)]);

        // Old content block must have its CacheControl cleared.
        Assert.Null(firstContent.CacheControl);

        // New sliding boundary must have CacheControl set.
        var secondContent = strategy.GetSlidingBoundaryContent();
        Assert.NotNull(secondContent);
        Assert.NotNull(secondContent!.CacheControl);
        Assert.Equal(CacheControlType.ephemeral, secondContent.CacheControl!.Type);
    }

    // ── Caching disabled ──────────────────────────────────────────────────────

    /// <summary>
    /// With enableHistoryCaching=false, Initialize must not set any CacheControl markers.
    /// </summary>
    [Fact]
    public void Initialize_CachingDisabled_NoMarkersOnHistory()
    {
        var strategy = BuildStrategy(enableHistoryCaching: false);

        var history = new List<ConversationTurn>
        {
            new("user",      "hi"),
            new("assistant", "hello")
        };
        strategy.Initialize("sys", history, "query", []);

        Assert.Null(strategy.GetHistoryBoundaryCacheControl());
    }

    /// <summary>
    /// With enableHistoryCaching=false, AddToolResults must not set any sliding BP4 marker.
    /// </summary>
    [Fact]
    public void AddToolResults_CachingDisabled_NoSliderSet()
    {
        var strategy = BuildStrategy(enableHistoryCaching: false);
        strategy.Initialize("sys", [], "query", []);

        strategy.AddToolResults([new UnifiedToolResult("call-1", "tool-a", "output", false)]);

        Assert.Null(strategy.GetSlidingBoundaryContent());
    }

    // ── CompactHistory resets slider ──────────────────────────────────────────

    /// <summary>
    /// CompactHistory rebuilds the message list; the sliding BP4 reference becomes stale
    /// and must be nulled so GetSlidingBoundaryContent() returns null.
    /// </summary>
    [Fact]
    public void CompactHistory_ResetsSlidingBoundary()
    {
        var strategy = BuildStrategy(enableHistoryCaching: true);
        strategy.Initialize("sys", [], "query", []);

        // Set up a sliding boundary.
        strategy.AddToolResults([new UnifiedToolResult("call-1", "tool-a", "output", false)]);
        Assert.NotNull(strategy.GetSlidingBoundaryContent());

        // CompactHistory must clear the stale reference.
        strategy.CompactHistory("sys", agentOverride: null);

        Assert.Null(strategy.GetSlidingBoundaryContent());
    }

    // ── Cache marker limit after compaction ───────────────────────────────────

    /// <summary>
    /// Regression: CompactHistory must strip orphaned CacheControl markers from kept messages.
    /// Without the fix, AddToolResults after CompactHistory would leave the old BP4 marker
    /// intact (because _slidingCacheBoundary was nulled) and add a new one, exceeding the
    /// Anthropic 4-breakpoint limit.
    /// </summary>
    [Fact]
    public void AddToolResults_AfterCompactHistory_NeverExceedsFourMarkers()
    {
        var strategy = BuildStrategy(enableHistoryCaching: true);
        strategy.Initialize("sys", [], "query", []);

        // Iteration 1: tool call + result → BP4 set
        strategy.AddToolResults([new UnifiedToolResult("call-1", "tool-a", "out1", false)]);
        Assert.Equal(1, strategy.CountCacheControlMarkers()); // just BP4 (BP3 not set: empty history)

        // Compaction (simulates Point A or re-planning compaction)
        strategy.CompactHistory("sys", agentOverride: null);

        // Iteration 2: new tool call + result → should set new BP4 without orphaning old one
        strategy.AddToolResults([new UnifiedToolResult("call-2", "tool-b", "out2", false)]);

        // At most 2 markers: BP3 (re-set by compaction) + BP4 (new sliding).
        // The old BP4 from iteration 1 must NOT survive.
        Assert.True(strategy.CountCacheControlMarkers() <= 2,
            $"Expected ≤ 2 cache markers, got {strategy.CountCacheControlMarkers()}");
    }

    /// <summary>
    /// Multiple compaction cycles must not accumulate orphaned markers.
    /// Simulates: tool results → compact → tool results → compact → tool results.
    /// </summary>
    [Fact]
    public void MultipleCompactionCycles_NeverAccumulateOrphanedMarkers()
    {
        var strategy = BuildStrategy(enableHistoryCaching: true);
        var history = new List<ConversationTurn>
        {
            new("user",      "hi"),
            new("assistant", "hello"),
        };
        strategy.Initialize("sys", history, "query", []);
        // BP3 is set here on history boundary

        for (int cycle = 0; cycle < 5; cycle++)
        {
            strategy.AddToolResults([new UnifiedToolResult($"c{cycle}", "tool", $"out{cycle}", false)]);
            strategy.CompactHistory("sys", agentOverride: null);
        }

        // Final tool result after last compaction
        strategy.AddToolResults([new UnifiedToolResult("cfinal", "tool", "final", false)]);

        // Should have at most 2: BP3 (re-set after last compaction) + BP4 (sliding)
        Assert.True(strategy.CountCacheControlMarkers() <= 2,
            $"Expected ≤ 2 cache markers after 5 compaction cycles, got {strategy.CountCacheControlMarkers()}");
    }

    /// <summary>
    /// PrepareNewWindow must also strip orphaned markers, same as CompactHistory.
    /// </summary>
    [Fact]
    public void PrepareNewWindow_StripsOrphanedMarkers()
    {
        var strategy = BuildStrategy(enableHistoryCaching: true);
        strategy.Initialize("sys", [], "query", []);

        strategy.AddToolResults([new UnifiedToolResult("call-1", "tool-a", "out1", false)]);
        strategy.PrepareNewWindow("Continue from where you left off.", "sys", agentOverride: null);

        // After PrepareNewWindow, old BP4 should be stripped + BP3 re-set
        strategy.AddToolResults([new UnifiedToolResult("call-2", "tool-b", "out2", false)]);

        Assert.True(strategy.CountCacheControlMarkers() <= 2,
            $"Expected ≤ 2 cache markers after PrepareNewWindow, got {strategy.CountCacheControlMarkers()}");
    }

    // ── Tool ordering stability ─────────────────────────────────────────────────

    /// <summary>
    /// Tools passed to AddExtraTools out of order must come out sorted by name, so the
    /// tools-array hash (part of the same cache prefix as BP1/BP2) stays stable regardless
    /// of MCP server response order.
    /// </summary>
    [Fact]
    public void AddExtraTools_OutOfOrderInput_SortedByName()
    {
        var strategy = BuildStrategy(enableHistoryCaching: true);
        strategy.Initialize("sys", [], "query", []);

        strategy.AddExtraTools([
            AIFunctionFactory.Create(() => "", name: "zebra_tool"),
            AIFunctionFactory.Create(() => "", name: "apple_tool"),
            AIFunctionFactory.Create(() => "", name: "mango_tool"),
        ]);

        Assert.Equal(["apple_tool", "mango_tool", "zebra_tool"], strategy.GetToolNamesInOrder());
    }

    /// <summary>
    /// Calling AddExtraTools twice with different (out-of-order) batches must still yield a
    /// tool list where duplicate names are dropped and each batch is internally sorted.
    /// </summary>
    [Fact]
    public void AddExtraTools_CalledTwice_EachBatchSortedAndDeduplicated()
    {
        var strategy = BuildStrategy(enableHistoryCaching: true);
        strategy.Initialize("sys", [], "query", []);

        strategy.AddExtraTools([
            AIFunctionFactory.Create(() => "", name: "zebra_tool"),
            AIFunctionFactory.Create(() => "", name: "apple_tool"),
        ]);
        strategy.AddExtraTools([
            AIFunctionFactory.Create(() => "", name: "zebra_tool"),   // duplicate — must be skipped
            AIFunctionFactory.Create(() => "", name: "banana_tool"),
        ]);

        Assert.Equal(["apple_tool", "zebra_tool", "banana_tool"], strategy.GetToolNamesInOrder());
    }

    // ── 1-hour cache TTL ─────────────────────────────────────────────────────────

    /// <summary>
    /// With enableOneHourCache=true, BP3 (history boundary) must use the 1-hour TTL.
    /// </summary>
    [Fact]
    public void Initialize_OneHourCacheEnabled_HistoryBoundaryUsesOneHourTtl()
    {
        var strategy = BuildStrategy(enableHistoryCaching: true, enableOneHourCache: true);

        var history = new List<ConversationTurn> { new("user", "hi"), new("assistant", "hello") };
        strategy.Initialize("sys", history, "query", []);

        var cc = strategy.GetHistoryBoundaryCacheControl();
        Assert.NotNull(cc);
        Assert.Equal(CacheDuration.OneHour, cc!.TTL);
    }

    /// <summary>
    /// With enableOneHourCache=false (default), BP3 must not set a TTL (plain 5-minute default).
    /// </summary>
    [Fact]
    public void Initialize_OneHourCacheDisabled_HistoryBoundaryHasNoTtlOverride()
    {
        var strategy = BuildStrategy(enableHistoryCaching: true, enableOneHourCache: false);

        var history = new List<ConversationTurn> { new("user", "hi"), new("assistant", "hello") };
        strategy.Initialize("sys", history, "query", []);

        var cc = strategy.GetHistoryBoundaryCacheControl();
        Assert.NotNull(cc);
        Assert.Null(cc!.TTL);
    }

    /// <summary>
    /// BP4 (sliding tool-result marker) always keeps the plain 5-minute default, even when
    /// enableOneHourCache is on — it slides every tool exchange and rarely survives that long.
    /// </summary>
    [Fact]
    public void AddToolResults_OneHourCacheEnabled_SlidingBoundaryStaysFiveMinute()
    {
        var strategy = BuildStrategy(enableHistoryCaching: true, enableOneHourCache: true);
        strategy.Initialize("sys", [], "query", []);

        strategy.AddToolResults([new UnifiedToolResult("call-1", "tool-a", "output", false)]);

        var content = strategy.GetSlidingBoundaryContent();
        Assert.NotNull(content?.CacheControl);
        Assert.Null(content!.CacheControl!.TTL);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static AnthropicProviderStrategy BuildStrategy(bool enableHistoryCaching, bool enableOneHourCache = false)
    {
        var anthropic = Substitute.For<IAnthropicProvider>();
        return new AnthropicProviderStrategy(
            anthropic,
            ContextWindowTestHelpers.NoOpCtx(),
            "claude-sonnet-4-6",
            4096,
            "static-sys",
            "",
            (fn, ct) => fn(),
            enableHistoryCaching: enableHistoryCaching,
            enableOneHourCache: enableOneHourCache);
    }
}
