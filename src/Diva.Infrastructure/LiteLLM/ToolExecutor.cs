using System.Text.Json;
using System.Text.Json.Nodes;
using Diva.Core.Configuration;
using Diva.Core.Models;
using Diva.Infrastructure.LiteLLM;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Diva.Infrastructure.LiteLLM;

/// <summary>
/// Encapsulates the low-level MCP tool invocation: timeout, error classification, and result
/// truncation. Extracted from <see cref="AnthropicAgentRunner"/> for independent testability.
/// Registered as Singleton (ILogger + IOptions&lt;AgentOptions&gt; dependencies — stateless).
/// </summary>
public sealed class ToolExecutor(ILogger<ToolExecutor> logger, IOptions<AgentOptions> agentOptions)
{
    /// <summary>
    /// Calls one MCP tool and returns a <see cref="ToolExecutorResult"/> containing text output
    /// and any image content blocks returned by the MCP server.
    /// </summary>
    /// <returns>
    /// <c>Output</c> — text content (never null/empty; contains an error message on failure).
    /// <c>ContentParts</c> — non-null when the MCP tool returned image content blocks.
    /// <c>Failed</c> — true when the result was an MCP error, looked like an error string, or timed out.
    /// <c>Error</c> — the underlying exception; null for MCP-level errors reported in <c>Output</c>.
    /// </returns>
    public async Task<ToolExecutorResult> ExecuteAsync(
        string toolName,
        string inputJson,
        Dictionary<string, McpClient> toolClientMap,
        Dictionary<string, McpClient> mcpClients,
        int maxToolResultChars,
        CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        var toolTimeoutSeconds = agentOptions.Value.ToolTimeoutSeconds;
        logger.LogInformation("Starting tool execution: {ToolName} at {Time}",
            toolName, startTime.ToString("HH:mm:ss.fff"));

        try
        {
            var owningClient = toolClientMap.GetValueOrDefault(toolName)
                ?? mcpClients.Values.First();
            using var toolCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            toolCts.CancelAfter(TimeSpan.FromSeconds(toolTimeoutSeconds));

            Dictionary<string, object?> toolArgs;
            try
            {
                toolArgs = JsonSerializer.Deserialize<Dictionary<string, object?>>(inputJson) ?? [];
            }
            catch (JsonException argEx)
            {
                logger.LogWarning(argEx,
                    "Tool {ToolName}: input JSON is malformed — calling with empty arguments. Input: {Input}",
                    toolName, inputJson.Length > 200 ? inputJson[..200] + "…" : inputJson);
                toolArgs = [];
            }

            var callResult = await owningClient.CallToolAsync(
                toolName,
                toolArgs,
                cancellationToken: toolCts.Token);

            var rawText = string.Join("\n", callResult.Content.OfType<TextContentBlock>().Select(c => c.Text));

            // Promote embedded imageBase64 fields (e.g. from read_image with includeBase64=true)
            // into proper image content parts so the LLM receives pixels, not a base64 text blob.
            var (cleanedText, embeddedImageParts) = ExtractEmbeddedImageParts(rawText);

            var textOutput = ReActToolHelper.TruncateResult(cleanedText, maxToolResultChars);

            // Capture image content blocks returned by MCP tools (e.g. ImageReader, screenshot tools).
            // MCP protocol supports ImageContentBlock alongside TextContentBlock in tool responses.
            var imageParts = callResult.Content
                .OfType<ImageContentBlock>()
                .Select(img => (ContentPart)new ImageContentPart
                {
                    MediaType = img.MimeType ?? "image/jpeg",
                    Data = Convert.ToBase64String(img.Data.Span)
                })
                .Concat(embeddedImageParts)
                .ToList();

            var failed = (callResult.IsError == true) || ReActToolHelper.IsToolOutputError(textOutput);

            if (imageParts.Count > 0)
            {
                var totalKb = imageParts.OfType<ImageContentPart>().Sum(p => (p.Data?.Length ?? 0) / 1024);
                logger.LogInformation(
                    "Tool {ToolName}: passing {Count} image(s) to LLM (~{SizeKb} KB base64)",
                    toolName, imageParts.Count, totalKb);
            }

            var endTime = DateTime.UtcNow;
            logger.LogInformation(
                "Completed tool execution: {ToolName} at {Time} (duration: {Duration}ms)",
                toolName, endTime.ToString("HH:mm:ss.fff"),
                (endTime - startTime).TotalMilliseconds.ToString("F0"));

            return new ToolExecutorResult
            {
                Output = textOutput,
                ContentParts = imageParts.Count > 0 ? imageParts : null,
                Failed = failed,
                Error = null,
                DurationMs = (long)(endTime - startTime).TotalMilliseconds,
            };
        }
        catch (OperationCanceledException)
        {
            var endTime = DateTime.UtcNow;
            logger.LogWarning(
                "Tool execution timed out: {ToolName} at {Time} (duration: {Duration}ms)",
                toolName, endTime.ToString("HH:mm:ss.fff"),
                (endTime - startTime).TotalMilliseconds.ToString("F0"));

            return new ToolExecutorResult
            {
                Output = $"Tool '{toolName}' timed out after {toolTimeoutSeconds}s. Try a narrower query (e.g. shorter date range).",
                Failed = true,
                Error = new TimeoutException($"Tool '{toolName}' timed out after {toolTimeoutSeconds}s."),
                DurationMs = (long)(endTime - startTime).TotalMilliseconds,
            };
        }
        catch (Exception ex)
        {
            var endTime = DateTime.UtcNow;
            logger.LogWarning(ex,
                "Tool execution failed: {ToolName} at {Time} (duration: {Duration}ms)",
                toolName, endTime.ToString("HH:mm:ss.fff"),
                (endTime - startTime).TotalMilliseconds.ToString("F0"));

            return new ToolExecutorResult
            {
                Output = $"Error: {ex.Message}",
                Failed = true,
                Error = ex,
                DurationMs = (long)(endTime - startTime).TotalMilliseconds,
            };
        }
    }

    /// <summary>
    /// Detects the imageBase64 + imageMediaType pattern produced by the read_image tool
    /// (and any future tool that follows the same convention) and promotes the embedded
    /// image into a proper <see cref="ImageContentPart"/> so the LLM sees actual pixels.
    /// Returns the cleaned text (base64 field replaced with a placeholder) plus the extracted parts.
    /// </summary>
    private static (string CleanedText, List<ContentPart> Parts) ExtractEmbeddedImageParts(string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.TrimStart()[0] != '{')
            return (text, []);

        try
        {
            var node = JsonNode.Parse(text);
            if (node is not JsonObject obj)
                return (text, []);

            var parts = new List<ContentPart>();

            if (obj["imageBase64"]?.GetValue<string>() is { Length: > 0 } b64 &&
                obj["imageMediaType"]?.GetValue<string>() is { Length: > 0 } mime)
            {
                parts.Add(new ImageContentPart { Data = b64, MediaType = mime });
                obj["imageBase64"] = "<image passed to LLM as content block>";
            }

            // Thumbnail is also base64 — strip it to avoid token waste
            if (obj["thumbnailBase64"] is { } thumb && thumb.GetValueKind() != JsonValueKind.Null)
                obj["thumbnailBase64"] = "<thumbnail stripped>";

            return (obj.ToJsonString(), parts);
        }
        catch (JsonException)
        {
            return (text, []);
        }
    }
}
