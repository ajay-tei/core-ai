using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Diva.Core.Prompts;

/// <summary>
/// Resolves {{variable}} placeholders in assembled system prompts.
/// Built-ins: {{current_date}}, {{current_time}}, {{current_datetime}}.
/// Runtime variables (user identity, tenant, session) are injected per-request:
///   {{user_id}}, {{user_email}}, {{user_name}}, {{tenant_id}}, {{tenant_name}}, {{session_id}}.
/// Custom variables are supplied per-agent via CustomVariablesJson.
/// Precedence (highest wins): customVariables > runtimeVariables > builtIns.
/// Unresolved placeholders are left unchanged so they remain visible in LLM output.
/// </summary>
public static class PromptVariableResolver
{
    private static readonly Regex VarPattern =
        new(@"\{\{(\w+)\}\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Replaces {{variable}} placeholders in <paramref name="prompt"/>.
    /// Precedence: <paramref name="customVariables"/> > <paramref name="runtimeVariables"/> > built-ins.
    /// Unresolved → left as-is.
    /// </summary>
    public static string Resolve(
        string prompt,
        IReadOnlyDictionary<string, string>? customVariables,
        ILogger? logger = null)
        => Resolve(prompt, customVariables, null, logger);

    /// <summary>
    /// Replaces {{variable}} placeholders in <paramref name="prompt"/>.
    /// Precedence: <paramref name="customVariables"/> > <paramref name="runtimeVariables"/> > built-ins.
    /// Unresolved → left as-is.
    /// </summary>
    public static string Resolve(
        string prompt,
        IReadOnlyDictionary<string, string>? customVariables,
        IReadOnlyDictionary<string, string>? runtimeVariables,
        ILogger? logger = null)
    {
        // Fast path — no allocations when there are no placeholders
        if (!prompt.Contains("{{")) return prompt;

        // Capture once so all built-ins share the same timestamp within a single build call
        var now = DateTime.UtcNow;
        var builtIns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["current_date"] = now.ToString("yyyy-MM-dd"),
            ["current_time"] = now.ToString("HH:mm") + " UTC",
            ["current_datetime"] = now.ToString("yyyy-MM-dd HH:mm") + " UTC",
        };

        return VarPattern.Replace(prompt, match =>
        {
            var key = match.Groups[1].Value;

            // Custom variables take precedence so admins can override any built-in or runtime var
            if (customVariables is not null &&
                customVariables.TryGetValue(key, out var custom))
                return custom;

            // Runtime variables (user identity, tenant) take precedence over built-ins
            if (runtimeVariables is not null &&
                runtimeVariables.TryGetValue(key, out var runtime))
                return runtime;

            if (builtIns.TryGetValue(key, out var builtin))
                return builtin;

            logger?.LogDebug(
                "Prompt variable {{{{{Key}}}}} is not defined — leaving as-is", key);
            return match.Value;
        });
    }

    /// <summary>
    /// Parses the <c>CustomVariablesJson</c> column value into a lookup dictionary.
    /// Returns <c>null</c> (not an empty dict) when the JSON is null, empty, or invalid —
    /// so callers can skip the resolver fast path without allocating an empty collection.
    /// </summary>
    public static IReadOnlyDictionary<string, string>? ParseJson(
        string? json, ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}") return null;
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (raw is null) return null;

            var result = new Dictionary<string, string>(raw.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, element) in raw)
                result[key] = JsonElementToString(element);
            return result;
        }
        catch (JsonException ex)
        {
            logger?.LogWarning(ex,
                "Invalid CustomVariablesJson — variable substitution skipped");
            return null;
        }
    }

    private static string JsonElementToString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString() ?? "",
        JsonValueKind.Number => element.GetRawText(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Null => "",
        JsonValueKind.Array => string.Join(", ", element.EnumerateArray().Select(JsonElementToString)),
        JsonValueKind.Object => element.GetRawText(),
        _ => element.GetRawText(),
    };
}
