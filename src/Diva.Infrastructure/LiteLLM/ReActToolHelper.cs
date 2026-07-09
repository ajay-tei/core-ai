using System.Text;
using System.Text.Json;
using Diva.Core.Configuration;
using Diva.Core.Models;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace Diva.Infrastructure.LiteLLM;

/// <summary>
/// Pure static helpers for tool result processing in the ReAct loop:
/// error detection, output truncation, continuation context, and retry prompts.
/// </summary>
internal static class ReActToolHelper
{
    internal const string ToolErrorRetryPrompt =
        "The previous tool call failed. Please retry with corrected parameters — make the corrected tool call now.";

    /// <summary>
    /// Prompt used when the model announced it would call tools but ended its turn
    /// with only preamble text (no tool_use / tool_calls). Nudges it to actually
    /// emit the tool calls it described instead of stalling.
    /// </summary>
    internal const string ToolPreambleNudgePrompt =
        "You described the data you intend to gather but did not actually call any tools. " +
        "Call those tools NOW in this turn — emit the tool calls directly, in parallel where independent, " +
        "with no further preamble. Do not describe what you are about to do; just make the tool calls.";

    // Phrases that strongly signal the model is announcing imminent tool use
    // rather than delivering a final answer (case-insensitive substring match).
    private static readonly string[] ToolPreambleMarkers =
    {
        "let me gather", "let me get", "let me query", "let me fetch", "let me pull",
        "let me retrieve", "let me collect", "let me call", "let me use", "let me run",
        "let me check", "let me look", "let me start by", "let me first", "let's gather",
        "let's start by", "i'll gather", "i'll get", "i'll query", "i'll fetch",
        "i'll retrieve", "i'll call", "i'll use", "i'll start by", "i'll first",
        "i will gather", "i will query", "i will call", "i'm going to", "i am going to",
        "in parallel", "gather all the data", "let me now", "now let me",
    };

    /// <summary>
    /// Heuristic: returns true when the model's text reads like an announcement of
    /// imminent tool use ("Let me gather the data…", "I'll query…") rather than a
    /// final answer. Used to nudge providers (typically local/OpenAI-compatible
    /// models) that emit a planning preamble and then stop without a tool call.
    /// </summary>
    internal static bool LooksLikeToolPreamble(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var lower = text.ToLowerInvariant();
        foreach (var marker in ToolPreambleMarkers)
            if (lower.Contains(marker, StringComparison.Ordinal))
                return true;
        return false;
    }

    /// <summary>
    /// Builds a selective retry prompt that tells the LLM exactly which tool calls failed
    /// and which succeeded, so it retries only the failed ones instead of re-executing the
    /// entire batch (which would cause duplicate side-effects for succeeded actions).
    /// Falls back to the generic <see cref="ToolErrorRetryPrompt"/> when breakdown is unavailable.
    /// </summary>
    internal static string BuildSelectiveRetryPrompt(
        IReadOnlyList<(string ToolName, string InputJson, bool Failed)> results)
    {
        var failed = results.Where(r => r.Failed).ToList();
        var succeeded = results.Where(r => !r.Failed).ToList();
        if (failed.Count == 0) return ToolErrorRetryPrompt;

        var sb = new StringBuilder();
        sb.AppendLine($"{failed.Count} of {results.Count} tool call(s) FAILED. Retry ONLY the failed ones:");
        foreach (var (name, input, _) in failed)
        {
            var shortInput = input.Length > 200 ? input[..200] + "…" : input;
            sb.AppendLine($"  ✗ {name}({shortInput})");
        }
        if (succeeded.Count > 0)
        {
            sb.AppendLine("Do NOT re-execute these — they already succeeded:");
            foreach (var (name, input, _) in succeeded)
            {
                var shortInput = input.Length > 120 ? input[..120] + "…" : input;
                sb.AppendLine($"  ✓ {name}({shortInput})");
            }
        }
        sb.AppendLine("Make the corrected tool call(s) now.");
        return sb.ToString();
    }

    /// <summary>
    /// Returns true when a tool's text output represents a failure.
    /// Covers: thrown exceptions ("Error: ..."), timeouts, empty output,
    /// JSON {"status":"error",...} objects, and the FileSystem tool's {"error":"...","message":"..."} format.
    /// </summary>
    internal static bool IsToolOutputError(string output) =>
        string.IsNullOrWhiteSpace(output) ||
        output.StartsWith("Error:") ||
        output.Contains("timed out") ||
        (output.TrimStart().StartsWith("{") &&
         (output.Contains("\"status\":\"error\"") || output.Contains("\"status\": \"error\"") ||
          output.Contains("\"error\":\"AccessDenied\"") ||
          output.Contains("\"error\":\"IoError\"") ||
          output.Contains("\"error\":\"ToolDisabled\"") ||
          output.Contains("\"error\":\"WriteDisabled\"") ||
          output.Contains("\"error\":\"ScriptDisabled\"")));

    /// <summary>
    /// Truncates <paramref name="output"/> to <paramref name="maxChars"/> characters,
    /// appending a hint to re-query with narrower parameters when truncated.
    /// </summary>
    internal static string TruncateResult(string output, int maxChars) =>
        output.Length <= maxChars
            ? output
            : output[..maxChars] +
              $"\n[truncated — {output.Length} chars total. Re-query with narrower parameters if needed.]";

    /// <summary>
    /// Builds the context message injected at the start of a new continuation window.
    /// Summarises what was accomplished so the model can continue the task.
    /// </summary>
    internal static string BuildContinuationContext(
        int windowNumber,
        int maxIterations,
        IReadOnlyList<string> toolEvidence)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[Continuation window {windowNumber + 1}]");
        sb.AppendLine($"The previous window used all {maxIterations} iterations without completing the task.");
        if (toolEvidence.Count > 0)
        {
            sb.AppendLine("Evidence gathered so far:");
            foreach (var e in toolEvidence)
                sb.AppendLine(e);
        }
        sb.AppendLine("Please continue executing the remaining steps needed to complete the task.");
        return sb.ToString();
    }

    // ── Per-agent tool filtering ─────────────────────────────────────────────

    /// <summary>
    /// Filters <paramref name="toolClientMap"/> and <paramref name="allMcpTools"/> in place
    /// based on the agent's <paramref name="toolFilterJson"/> configuration.
    /// JSON format: <c>{"mode":"allow","tools":["tool1","tool2"]}</c> or <c>{"mode":"deny","tools":["tool3"]}</c>.
    /// Null or empty JSON = all tools allowed (no-op).
    /// </summary>
    internal static void FilterTools(
        string? toolFilterJson,
        Dictionary<string, McpClient> toolClientMap,
        List<McpClientTool> allMcpTools,
        ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(toolFilterJson)) return;
        try
        {
            var filter = JsonSerializer.Deserialize<ToolFilterConfig>(toolFilterJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (filter?.Tools is null || filter.Tools.Count == 0) return;

            var filterSet = new HashSet<string>(filter.Tools, StringComparer.OrdinalIgnoreCase);
            bool isAllow = filter.Mode?.Equals("allow", StringComparison.OrdinalIgnoreCase) == true;

            var toRemove = toolClientMap.Keys
                .Where(name => isAllow ? !filterSet.Contains(name) : filterSet.Contains(name))
                .ToList();

            foreach (var name in toRemove)
            {
                toolClientMap.Remove(name);
                allMcpTools.RemoveAll(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            }

            if (toRemove.Count > 0)
                logger.LogInformation("Tool filter ({Mode}): removed {Count} tool(s): [{Names}]",
                    filter.Mode, toRemove.Count, string.Join(", ", toRemove));
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex, "Invalid ToolFilterJson — ignoring filter");
        }
    }

    // ── Intra-batch tool call deduplication ──────────────────────────────────

    /// <summary>
    /// Groups <paramref name="calls"/> by (toolName, inputJson).
    /// Returns one group per unique key; each group holds all original items that share it.
    /// Callers execute one task per group and fan the result out to every original item.
    /// Order of first occurrence is preserved.
    /// </summary>
    internal static List<(string Name, string InputJson, List<T> Originals)>
        DeduplicateCalls<T>(IList<T> calls, Func<T, (string Name, string InputJson)> keySelector)
        => DeduplicateCalls(calls, keySelector, neverDeduplicatePrefixes: null);

    /// <summary>
    /// Groups <paramref name="calls"/> by (toolName, inputJson), but skips deduplication for
    /// tools whose name starts with any of the <paramref name="neverDeduplicatePrefixes"/>.
    /// Action tools (send_, create_, post_, etc.) must always execute individually even when
    /// two calls share identical input JSON, because they have side effects.
    /// </summary>
    internal static List<(string Name, string InputJson, List<T> Originals)>
        DeduplicateCalls<T>(IList<T> calls, Func<T, (string Name, string InputJson)> keySelector,
            IReadOnlyList<string>? neverDeduplicatePrefixes)
    {
        var groups = new List<(string Name, string InputJson, List<T> Originals)>();
        var seen = new Dictionary<(string, string), int>(calls.Count);
        foreach (var call in calls)
        {
            var (name, inputJson) = keySelector(call);

            // Action tools bypass dedup — each call executes individually
            bool skipDedup = neverDeduplicatePrefixes is { Count: > 0 }
                && neverDeduplicatePrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

            var key = (name, inputJson);
            if (!skipDedup && seen.TryGetValue(key, out var idx))
                groups[idx].Originals.Add(call);
            else
            {
                seen[key] = groups.Count;
                groups.Add((name, inputJson, [call]));
            }
        }
        return groups;
    }

    // ── ExecutionMode enforcement ────────────────────────────────────────

    /// <summary>
    /// Enforces the agent's <paramref name="mode"/> by removing tools that are not
    /// permitted for the current execution mode.
    /// No-op for <see cref="AgentExecutionMode.Full"/> and <see cref="AgentExecutionMode.Supervised"/>.
    /// </summary>
    internal static void ApplyExecutionModeFilter(
        AgentExecutionMode mode,
        string? toolBindingsJson,
        Dictionary<string, McpClient> toolClientMap,
        List<McpClientTool> allMcpTools,
        ILogger logger)
    {
        switch (mode)
        {
            case AgentExecutionMode.ChatOnly:
                if (toolClientMap.Count > 0)
                    logger.LogInformation("ExecutionMode=ChatOnly: removing all {Count} tools", toolClientMap.Count);
                toolClientMap.Clear();
                allMcpTools.Clear();
                break;

            case AgentExecutionMode.ReadOnly:
                var bindingList = !string.IsNullOrWhiteSpace(toolBindingsJson)
                    ? JsonSerializer.Deserialize<List<McpToolBinding>>(toolBindingsJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? []
                    : new List<McpToolBinding>();
                var readOnlyNames = bindingList
                    .Where(b => b.Access.Equals("ReadOnly", StringComparison.OrdinalIgnoreCase))
                    .Select(b => b.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var toRemove = toolClientMap.Keys.Where(name => !readOnlyNames.Contains(name)).ToList();
                foreach (var name in toRemove)
                {
                    toolClientMap.Remove(name);
                    allMcpTools.RemoveAll(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                }
                if (toRemove.Count > 0)
                    logger.LogInformation("ExecutionMode=ReadOnly: removed {Count} non-read tool(s)", toRemove.Count);
                break;

            case AgentExecutionMode.Supervised:
                logger.LogInformation("ExecutionMode=Supervised: tools loaded, approval required per call");
                break;
        }
    }
}

// ── Tool filter configuration ────────────────────────────────────────────────

internal sealed class ToolFilterConfig
{
    public string? Mode { get; set; }        // "allow" or "deny"
    public List<string>? Tools { get; set; } // tool names
}
