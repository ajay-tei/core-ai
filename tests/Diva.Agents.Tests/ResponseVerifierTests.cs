using Anthropic.SDK.Messaging;
using Diva.Agents.Tests.Helpers;
using Diva.Core.Configuration;
using Diva.Infrastructure.LiteLLM;
using Diva.Infrastructure.Verification;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Diva.Agents.Tests;

public class ResponseVerifierTests
{
    // ── helpers ──────────────────────────────────────────────────────────────

    private static ResponseVerifier BuildVerifier(
        string mode = "Off",
        IAnthropicProvider? anthropic = null,
        IOpenAiProvider? openAi = null)
    {
        return new ResponseVerifier(
            AgentTestFixtures.Opts(new VerificationOptions { Mode = mode }),
            AgentTestFixtures.AnthropicLlm(),
            anthropic ?? Substitute.For<IAnthropicProvider>(),
            openAi ?? Substitute.For<IOpenAiProvider>(),
            NullLogger<ResponseVerifier>.Instance);
    }

    private static ResponseVerifier BuildVerifierWithOptions(
        VerificationOptions options,
        IAnthropicProvider? anthropic = null,
        IOpenAiProvider? openAi = null)
    {
        return new ResponseVerifier(
            AgentTestFixtures.Opts(options),
            AgentTestFixtures.AnthropicLlm(),
            anthropic ?? Substitute.For<IAnthropicProvider>(),
            openAi ?? Substitute.For<IOpenAiProvider>(),
            NullLogger<ResponseVerifier>.Instance);
    }

    private static ResponseVerifier BuildVerifierWithOverride(
        string globalMode,
        IAnthropicProvider? anthropic = null)
    {
        return new ResponseVerifier(
            AgentTestFixtures.Opts(new VerificationOptions { Mode = globalMode }),
            AgentTestFixtures.AnthropicLlm(),
            anthropic ?? Substitute.For<IAnthropicProvider>(),
            Substitute.For<IOpenAiProvider>(),
            NullLogger<ResponseVerifier>.Instance);
    }

    // ── Off mode ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Off_AlwaysSkips_ReturnsVerifiedTrue()
    {
        var verifier = BuildVerifier("Off");

        var result = await verifier.VerifyAsync(
            "The Sensex closed at 62,150 today.",
            ["GetMarketData"],
            "evidence here",
            CancellationToken.None);

        Assert.True(result.IsVerified);
        Assert.Equal("Off", result.Mode);
        Assert.Equal(1f, result.Confidence);
    }

    // ── ToolGrounded mode ────────────────────────────────────────────────────

    [Fact]
    public async Task ToolGrounded_NoToolsCalled_WithFactualNumbers_ReturnsFlagged()
    {
        var verifier = BuildVerifier("ToolGrounded");

        var result = await verifier.VerifyAsync(
            "Revenue was $24,500 last month with 1,250 transactions.",
            toolsUsed: [],
            toolEvidence: "",
            CancellationToken.None);

        Assert.False(result.IsVerified);
        Assert.Equal("ToolGrounded", result.Mode);
        Assert.True(result.Confidence < 0.5f);
        Assert.NotEmpty(result.UngroundedClaims);
    }

    [Fact]
    public async Task ToolGrounded_NoToolsCalled_PurelyConversational_Passes()
    {
        var verifier = BuildVerifier("ToolGrounded");

        var result = await verifier.VerifyAsync(
            "Sure, I can help you with that! Let me know what you need.",
            toolsUsed: [],
            toolEvidence: "",
            CancellationToken.None);

        Assert.True(result.IsVerified);
        Assert.Equal("ToolGrounded", result.Mode);
    }

    [Fact]
    public async Task ToolGrounded_ToolsWereCalled_AlwaysPasses()
    {
        var verifier = BuildVerifier("ToolGrounded");

        var result = await verifier.VerifyAsync(
            "Revenue was $24,500 last month.",
            toolsUsed: ["GetMetrics"],
            toolEvidence: "[Tool: GetMetrics]\n{\"total\": 24500}",
            CancellationToken.None);

        Assert.True(result.IsVerified);
        Assert.Equal("ToolGrounded", result.Mode);
        Assert.True(result.Confidence >= 0.8f);
    }

    // ── Auto mode ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Auto_ShortResponse_Skips()
    {
        var verifier = BuildVerifier("Auto");

        var result = await verifier.VerifyAsync(
            "Got it!",   // < 80 chars
            toolsUsed: [],
            toolEvidence: "",
            CancellationToken.None);

        Assert.True(result.IsVerified);
        Assert.Equal("Off", result.Mode);
    }

    [Fact]
    public async Task Auto_NoToolsNoFacts_Skips()
    {
        var verifier = BuildVerifier("Auto");

        var result = await verifier.VerifyAsync(
            "Sure, I can help you with that! Please describe what you'd like to do and I'll guide you through it step by step.",
            toolsUsed: [],
            toolEvidence: "",
            CancellationToken.None);

        Assert.True(result.IsVerified);
        Assert.Equal("Off", result.Mode);
    }

    [Fact]
    public async Task Auto_NoToolsWithFacts_RunsToolGrounded()
    {
        var verifier = BuildVerifier("Auto");

        var result = await verifier.VerifyAsync(
            "The stock is currently priced at $142.75, up 3.2% from yesterday's close of $138.30.",
            toolsUsed: [],
            toolEvidence: "",
            CancellationToken.None);

        Assert.Equal("ToolGrounded", result.Mode);
        Assert.False(result.IsVerified);
    }

    [Fact]
    public async Task Auto_ToolsCalledWithEvidence_RunsToolGrounded()
    {
        // Auto mode now uses ToolGrounded heuristic (zero extra LLM cost) when tools+evidence present.
        // Use LlmVerifier or Strict explicitly for cross-checking claims vs evidence.
        var anthropic = Substitute.For<IAnthropicProvider>();

        var verifier = BuildVerifier("Auto", anthropic: anthropic);

        var result = await verifier.VerifyAsync(
            "Total revenue was $24,500 last month, with 1,250 transactions recorded and a strong 7.9% growth rate.",
            toolsUsed: ["GetMetrics"],
            toolEvidence: "[Tool: GetMetrics]\n{\"revenue\": 24500, \"transactions\": 1250, \"growth\": 0.079}",
            CancellationToken.None);

        Assert.Equal("ToolGrounded", result.Mode);
        Assert.True(result.IsVerified);
        // No extra LLM call — heuristic is zero-cost
        await anthropic.DidNotReceive().GetClaudeMessageAsync(Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>());
    }

    // ── ToolGrounded variable confidence (action/delivery claims) ────────────

    [Fact]
    public async Task ToolGrounded_ToolsCalled_ActionClaim_LowersConfidence()
    {
        var verifier = BuildVerifier("ToolGrounded");

        var result = await verifier.VerifyAsync(
            "I've sent the daily briefing email to the manager and CC'd the regional director successfully.",
            toolsUsed: ["SendEmail"],
            toolEvidence: "[Tool: SendEmail]\n{\"status\": \"ok\"}",
            CancellationToken.None);

        Assert.True(result.IsVerified);
        Assert.Equal("ToolGrounded", result.Mode);
        // Action/delivery claim → confidence lowered below the plain-data 0.85 baseline
        Assert.True(result.Confidence < 0.85f);
    }

    [Fact]
    public async Task ToolGrounded_ToolsCalled_PlainData_KeepsBaselineConfidence()
    {
        var verifier = BuildVerifier("ToolGrounded");

        var result = await verifier.VerifyAsync(
            "Total revenue was $24,500 last month across 1,250 transactions, a 7.9% increase.",
            toolsUsed: ["GetMetrics"],
            toolEvidence: "[Tool: GetMetrics]\n{\"revenue\": 24500}",
            CancellationToken.None);

        Assert.True(result.IsVerified);
        Assert.Equal(0.85f, result.Confidence);
    }

    // ── Auto mode escalation ─────────────────────────────────────────────────

    [Fact]
    public async Task Auto_ToolsCalled_ActionClaim_EscalatesToLlmVerifier()
    {
        // Action/delivery claim drops heuristic confidence to 0.6 (< default 0.7 threshold)
        // and evidence is present → Auto escalates to a non-blocking LLM cross-check.
        var anthropic = Substitute.For<IAnthropicProvider>();
        anthropic.GetClaudeMessageAsync(Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>())
            .Returns(MakeAnthropicResponse(
                "{\"confidence\": 0.3, \"is_verified\": false, " +
                "\"ungrounded_claims\": [\"email delivery cannot be confirmed from tool data\"], " +
                "\"reasoning\": \"delivery is self-reported\"}"));

        var verifier = BuildVerifier("Auto", anthropic: anthropic);

        var result = await verifier.VerifyAsync(
            "I've sent the daily briefing email and it was delivered successfully to all recipients.",
            toolsUsed: ["SendEmail"],
            toolEvidence: "[Tool: SendEmail]\n{\"queued\": true}",
            CancellationToken.None);

        Assert.Equal("Auto", result.Mode);
        Assert.False(result.IsVerified);
        Assert.NotEmpty(result.UngroundedClaims);
        // Escalation must actually call the LLM verifier
        await anthropic.Received(1).GetClaudeMessageAsync(Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Auto_ActionClaim_NoEvidence_DoesNotEscalate()
    {
        // Low confidence but no evidence to check against → return heuristic, no LLM call.
        var anthropic = Substitute.For<IAnthropicProvider>();

        var verifier = BuildVerifier("Auto", anthropic: anthropic);

        var result = await verifier.VerifyAsync(
            "I've sent the daily briefing email and it was delivered successfully to all recipients.",
            toolsUsed: ["SendEmail"],
            toolEvidence: "",   // no evidence
            CancellationToken.None);

        Assert.Equal("ToolGrounded", result.Mode);
        await anthropic.DidNotReceive().GetClaudeMessageAsync(Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Auto_EscalationDisabled_DoesNotEscalate()
    {
        // AutoEscalateThreshold = 0 disables escalation entirely — heuristic verdict stands.
        var anthropic = Substitute.For<IAnthropicProvider>();

        var verifier = BuildVerifierWithOptions(
            new VerificationOptions { Mode = "Auto", AutoEscalateThreshold = 0f },
            anthropic: anthropic);

        var result = await verifier.VerifyAsync(
            "I've sent the daily briefing email and it was delivered successfully to all recipients.",
            toolsUsed: ["SendEmail"],
            toolEvidence: "[Tool: SendEmail]\n{\"queued\": true}",
            CancellationToken.None);

        Assert.Equal("ToolGrounded", result.Mode);
        await anthropic.DidNotReceive().GetClaudeMessageAsync(Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>());
    }

    // ── modeOverride ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ModeOverride_PerAgentOverridesGlobal_Off_SkipsEvenForFactualContent()
    {
        // Global = LlmVerifier, per-agent override = Off
        var anthropic = Substitute.For<IAnthropicProvider>();
        var verifier = BuildVerifierWithOverride("LlmVerifier", anthropic);

        var result = await verifier.VerifyAsync(
            "Revenue was $24,500 last month with 1,250 transactions.",
            toolsUsed: ["GetMetrics"],
            toolEvidence: "[Tool: GetMetrics]\n{\"total\": 24500}",
            CancellationToken.None,
            modelId: null,
            modeOverride: "Off");   // per-agent override

        Assert.True(result.IsVerified);
        Assert.Equal("Off", result.Mode);
        // LLM should never be called
        await anthropic.DidNotReceive().GetClaudeMessageAsync(Arg.Any<MessageParameters>(), Arg.Any<CancellationToken>());
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static MessageResponse MakeAnthropicResponse(string text)
    {
        return new MessageResponse
        {
            Content = [new Anthropic.SDK.Messaging.TextContent { Text = text }],
            StopReason = "end_turn",
            Model = "claude-sonnet-4-20250514"
        };
    }

    // ── UI directives ────────────────────────────────────────────────────────

    [Fact]
    public void StripUiDirectives_RemovesSuggestionsMarker()
    {
        var text = "Which course would you like?\n\n[[SUGGESTIONS:[\"8:00 AM\",\"2 players\"]]]";

        var stripped = ResponseVerifier.StripUiDirectives(text);

        Assert.Equal("Which course would you like?", stripped);
        Assert.DoesNotContain("8:00", stripped);
    }

    [Fact]
    public void StripUiDirectives_RemovesMultiSelectBuilderSpanningLines()
    {
        var text = "Pick a few details:\n[[SUGGESTIONS:{\"type\":\"multi\",\"groups\":[\n"
                 + "{\"label\":\"Players\",\"options\":[\"2 players\",\"4 players\"]}]}]]";

        Assert.Equal("Pick a few details:", ResponseVerifier.StripUiDirectives(text));
    }

    [Fact]
    public void StripUiDirectives_LeavesProseDigitsIntact()
    {
        var text = "12:00 PM is available at $175 per player. [[SUGGESTIONS:[\"Book it\"]]]";

        var stripped = ResponseVerifier.StripUiDirectives(text);

        Assert.Contains("12:00 PM", stripped);
        Assert.Contains("$175", stripped);
        Assert.DoesNotContain("SUGGESTIONS", stripped);
    }

    [Fact]
    public async Task ToolGrounded_ClarifyingQuestion_IsVerifiedDespiteChipDigits()
    {
        var verifier = BuildVerifier("ToolGrounded");
        var question = "I'd be happy to help! Which course, and how many players?\n"
                     + "[[SUGGESTIONS:{\"type\":\"multi\",\"groups\":["
                     + "{\"label\":\"Time\",\"options\":[\"8:00 AM\",\"2:00 PM\"]},"
                     + "{\"label\":\"Players\",\"options\":[\"2 players\",\"4 players\"]}]}]]";

        var result = await verifier.VerifyAsync(question, [], string.Empty, CancellationToken.None);

        Assert.True(result.IsVerified);
        Assert.Empty(result.UngroundedClaims);
    }

    [Fact]
    public async Task ToolGrounded_ProseDigitsWithoutTools_StillFlagged()
    {
        var verifier = BuildVerifier("ToolGrounded");

        var result = await verifier.VerifyAsync(
            "12:00 PM is available at $175 per player.", [], string.Empty, CancellationToken.None);

        Assert.False(result.IsVerified);
        Assert.NotEmpty(result.UngroundedClaims);
    }

    // ── JSON payloads ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("{\"type\":\"flat\",\"suggestions\":[\"2 players\",\"8:00 AM\"]}")]
    [InlineData("[{\"label\":\"Players\",\"options\":[\"4 players\"]}]")]
    [InlineData("  {\"a\":1}  ")]
    public void LooksLikeJsonDocument_WholePayload_IsRecognised(string text)
    {
        Assert.True(ResponseVerifier.LooksLikeJsonDocument(text));
    }

    [Theory]
    [InlineData("Your tee time is confirmed at 12:00 PM.")]
    [InlineData("{ this is not json")]
    [InlineData("Use {braces} in your reply")]
    [InlineData("")]
    [InlineData(null)]
    public void LooksLikeJsonDocument_Prose_IsNotRecognised(string? text)
    {
        Assert.False(ResponseVerifier.LooksLikeJsonDocument(text));
    }

    [Fact]
    public async Task ToolGrounded_JsonOnlyResponse_IsSkippedNotFlagged()
    {
        var verifier = BuildVerifier("ToolGrounded");
        var payload = "{\"type\":\"multi\",\"groups\":[{\"label\":\"Time\",\"options\":[\"8:00 AM\","
                    + "\"10:00 AM\"]},{\"label\":\"Players\",\"options\":[\"2 players\"]}],"
                    + "\"submitLabel\":\"Search\"}";

        var result = await verifier.VerifyAsync(payload, [], string.Empty, CancellationToken.None);

        Assert.True(result.IsVerified);
        Assert.Empty(result.UngroundedClaims);
    }
}
