using Diva.Rag.Abstractions;
using Microsoft.Extensions.Logging;

namespace Diva.Rag.Chunking;

/// <summary>
/// Splits text recursively at heading boundaries first, then paragraph/sentence boundaries.
/// Default: 512 tokens, 50-token overlap.
/// </summary>
public sealed class RecursiveTextChunker(ILogger<RecursiveTextChunker> logger) : IDocumentChunker
{
    // Rough token estimate: 1 token ≈ 4 chars (conservative for English)
    private const int CharsPerToken = 4;

    private static readonly string[] Separators =
    [
        "\n## ",     // H2
        "\n### ",    // H3
        "\n#### ",   // H4
        "\n\n",      // paragraph
        "\n",        // line
        ". ",        // sentence
        " ",         // word
    ];

    public IReadOnlyList<DocumentChunk> Chunk(string content, ChunkingOptions options)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var maxChars = options.MaxTokens * CharsPerToken;
        var overlapChars = options.Overlap * CharsPerToken;

        var segments = SplitRecursive(content, maxChars, 0);
        var chunks = new List<DocumentChunk>();

        for (int i = 0; i < segments.Count; i++)
        {
            var text = segments[i];

            // Add overlap from previous segment
            if (i > 0 && overlapChars > 0)
            {
                var prev = segments[i - 1];
                var overlapText = prev.Length > overlapChars
                    ? prev[^overlapChars..]
                    : prev;
                text = overlapText + text;
            }

            chunks.Add(new DocumentChunk
            {
                DocumentId = "",  // Set by caller
                ChunkIndex = i,
                Text = text.Trim(),
                TokenCount = EstimateTokens(text),
            });
        }

        logger.LogDebug("RecursiveTextChunker produced {Count} chunks from {Chars} chars", chunks.Count, content.Length);
        return chunks;
    }

    private static List<string> SplitRecursive(string text, int maxChars, int separatorIndex)
    {
        if (text.Length <= maxChars)
            return [text];

        if (separatorIndex >= Separators.Length)
        {
            // Hard split at maxChars as last resort
            var hardChunks = new List<string>();
            for (int i = 0; i < text.Length; i += maxChars)
                hardChunks.Add(text.Substring(i, Math.Min(maxChars, text.Length - i)));
            return hardChunks;
        }

        var separator = Separators[separatorIndex];
        var parts = text.Split(separator);

        if (parts.Length <= 1)
            return SplitRecursive(text, maxChars, separatorIndex + 1);

        var result = new List<string>();
        var current = "";

        foreach (var part in parts)
        {
            var candidate = string.IsNullOrEmpty(current) ? part : current + separator + part;

            if (candidate.Length <= maxChars)
            {
                current = candidate;
            }
            else
            {
                if (!string.IsNullOrWhiteSpace(current))
                    result.Add(current);

                if (part.Length > maxChars)
                    result.AddRange(SplitRecursive(part, maxChars, separatorIndex + 1));
                else
                    current = part;
            }
        }

        if (!string.IsNullOrWhiteSpace(current))
            result.Add(current);

        return result;
    }

    private static int EstimateTokens(string text) => (text.Length + CharsPerToken - 1) / CharsPerToken;
}
