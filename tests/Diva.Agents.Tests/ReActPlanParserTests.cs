using Diva.Infrastructure.LiteLLM;

namespace Diva.Agents.Tests;

/// <summary>
/// Unit tests for ReActPlanParser — plan step detection helpers.
/// </summary>
public class ReActPlanParserTests
{
    // ── ParsePlanSteps ────────────────────────────────────────────────────────

    [Fact]
    public void ParsePlanSteps_NumberedList_ExtractsAllSteps()
    {
        var text = """
            I will help you with this task:
            1. Get the current weather data
            2. Check flight availability
            3. Book the hotel
            """;

        var steps = ReActPlanParser.ParsePlanSteps(text);

        Assert.Equal(3, steps.Length);
        Assert.Equal("1. Get the current weather data", steps[0]);
        Assert.Equal("2. Check flight availability", steps[1]);
        Assert.Equal("3. Book the hotel", steps[2]);
    }

    [Fact]
    public void ParsePlanSteps_NoNumberedSteps_ReturnsEmpty()
    {
        var steps = ReActPlanParser.ParsePlanSteps("Just a normal response without numbered steps.");
        Assert.Empty(steps);
    }

    [Fact]
    public void ParsePlanSteps_SingleStep_ReturnsSingleItem()
    {
        var steps = ReActPlanParser.ParsePlanSteps("1. Only one step here");
        Assert.Single(steps);
    }

    [Fact]
    public void ParsePlanSteps_EmptyString_ReturnsEmpty()
    {
        Assert.Empty(ReActPlanParser.ParsePlanSteps(string.Empty));
    }

    [Fact]
    public void ParsePlanSteps_TrimsLeadingWhitespace()
    {
        var text = "  1. Step with leading whitespace";
        // Leading whitespace before the digit means it doesn't match ^\d+ (^ is start-of-line)
        // so the result depends on whether the line starts at column 0
        // The pattern is (?m)^\d+ so spaces before the digit break the match — verify this
        var steps = ReActPlanParser.ParsePlanSteps(text);
        Assert.Empty(steps); // leading spaces prevent the match
    }

    [Fact]
    public void ParsePlanSteps_InlineNumberDoesNotMatch()
    {
        var text = "The 1. answer is 42";
        // "1." here is not at the start of the line, so it should not match
        var steps = ReActPlanParser.ParsePlanSteps(text);
        Assert.Empty(steps);
    }

    [Fact]
    public void ParsePlanSteps_MultilineText_OnlyMatchesLineStartSteps()
    {
        var text = "Let me break this down:\n1. First step\n   - sub-item\n2. Second step";

        var steps = ReActPlanParser.ParsePlanSteps(text);

        Assert.Equal(2, steps.Length);
        Assert.Equal("1. First step", steps[0]);
        Assert.Equal("2. Second step", steps[1]);
    }

    // ── IsPlanEmission ────────────────────────────────────────────────────────

    [Fact]
    public void IsPlanEmission_FirstIterationTwoSteps_ReturnsTrue()
    {
        var text = "1. Get data\n2. Process data";
        Assert.True(ReActPlanParser.IsPlanEmission(text, isFirstIteration: true, planAlreadyEmitted: false));
    }

    [Fact]
    public void IsPlanEmission_FirstIterationOneStep_ReturnsFalse()
    {
        var text = "1. Only one step";
        Assert.False(ReActPlanParser.IsPlanEmission(text, isFirstIteration: true, planAlreadyEmitted: false));
    }

    [Fact]
    public void IsPlanEmission_NotFirstIteration_ReturnsFalse()
    {
        var text = "1. Get data\n2. Process data\n3. Return result";
        Assert.False(ReActPlanParser.IsPlanEmission(text, isFirstIteration: false, planAlreadyEmitted: false));
    }

    [Fact]
    public void IsPlanEmission_PlanAlreadyEmitted_ReturnsFalse()
    {
        var text = "1. Get data\n2. Process data";
        Assert.False(ReActPlanParser.IsPlanEmission(text, isFirstIteration: true, planAlreadyEmitted: true));
    }

    // ── ParseActionPlanSteps ──────────────────────────────────────────────────

    [Fact]
    public void ParseActionPlanSteps_ActionSteps_AreKept()
    {
        var text = "1. Get the current weather data\n2. Check flight availability";

        Assert.Equal(2, ReActPlanParser.ParseActionPlanSteps(text).Length);
    }

    [Fact]
    public void ParseActionPlanSteps_NumberedQuestionsToUser_AreExcluded()
    {
        var text = """
            To find the right tee time, I need a few details:
            1. Which course would you like to play?
            2. What time works best for you?
            3. How many players?
            """;

        Assert.Empty(ReActPlanParser.ParseActionPlanSteps(text));
        Assert.Equal(3, ReActPlanParser.ParsePlanSteps(text).Length);
    }

    [Fact]
    public void ParseActionPlanSteps_QuestionsWithTrailingMarkdown_AreExcluded()
    {
        var text = "1. **Date** — which day would you like to play?**\n2. Course — Orca, or a different one?*";

        Assert.Empty(ReActPlanParser.ParseActionPlanSteps(text));
    }

    [Fact]
    public void ParseActionPlanSteps_MixedList_KeepsOnlyActions()
    {
        var text = "1. Which course would you like?\n2. Search the tee sheet\n3. Book the slot";

        var steps = ReActPlanParser.ParseActionPlanSteps(text);

        Assert.Equal(2, steps.Length);
        Assert.Equal("2. Search the tee sheet", steps[0]);
    }

    [Fact]
    public void IsPlanEmission_NumberedQuestionsToUser_ReturnsFalse()
    {
        var text = "1. Which course would you like to play?\n2. What time works best for you?";

        Assert.False(ReActPlanParser.IsPlanEmission(text, isFirstIteration: true, planAlreadyEmitted: false));
    }
}
