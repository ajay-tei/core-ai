using System.Text.RegularExpressions;

namespace Diva.Infrastructure.LiteLLM;

/// <summary>
/// Pure static helpers for detecting and parsing ReAct plans from LLM text output.
/// </summary>
internal static class ReActPlanParser
{
    private static readonly Regex PlanStepPattern =
        new(@"(?m)^\d+\.\s+.+", RegexOptions.Compiled);

    private static readonly Regex QuestionStepPattern =
        new(@"\?[\s*_`""'\)\]]*$", RegexOptions.Compiled);

    /// <summary>
    /// Extracts numbered plan steps (e.g. "1. Do X") from raw LLM text.
    /// Returns an empty array when no steps are found.
    /// </summary>
    internal static string[] ParsePlanSteps(string text) =>
        PlanStepPattern.Matches(text)
                       .Select(m => m.Value.Trim())
                       .ToArray();

    /// <summary>
    /// Numbered steps that read as actions rather than questions addressed to the user.
    /// A clarifying list ("1. Which course? 2. What time?") is a finished answer, not a
    /// ReAct plan, and must not be treated as an unfinished planning preamble.
    /// </summary>
    internal static string[] ParseActionPlanSteps(string text) =>
        ParsePlanSteps(text)
            .Where(s => !QuestionStepPattern.IsMatch(s))
            .ToArray();

    /// <summary>
    /// Returns true when the text qualifies as a plan emission:
    /// first iteration, plan not yet emitted, and at least 2 numbered steps.
    /// </summary>
    internal static bool IsPlanEmission(string text, bool isFirstIteration, bool planAlreadyEmitted) =>
        isFirstIteration && !planAlreadyEmitted && ParseActionPlanSteps(text).Length >= 2;
}
