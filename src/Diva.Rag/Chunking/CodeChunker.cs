using Diva.Rag.Abstractions;
using Microsoft.Extensions.Logging;

namespace Diva.Rag.Chunking;

/// <summary>
/// Splits code files at class/method boundaries. Default: 768 tokens, no overlap.
/// Falls back to recursive line splitting when no boundaries are found.
/// </summary>
public sealed class CodeChunker(ILogger<CodeChunker> logger) : IDocumentChunker
{
    private const int CharsPerToken = 4;

    private static readonly string[] BoundaryPatterns =
    [
        "\npublic class ",
        "\npublic sealed class ",
        "\npublic interface ",
        "\npublic record ",
        "\npublic enum ",
        "\ninternal class ",
        "\ninternal sealed class ",
        "\nprivate class ",
        "\npublic async ",
        "\npublic static ",
        "\npublic override ",
        "\npublic virtual ",
        "\npublic ",          // catch-all for public methods/props
        "\nfunction ",        // JS/TS
        "\nexport function ",
        "\nexport class ",
        "\nexport interface ",
        "\nexport default ",
        "\nclass ",           // Python/general
        "\ndef ",             // Python
        "\n\n",               // paragraph
        "\n",                 // line
    ];

    public IReadOnlyList<DocumentChunk> Chunk(string content, ChunkingOptions options)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var maxChars = options.MaxTokens * CharsPerToken;
        var segments = SplitAtBoundaries(content, maxChars);
        var chunks = new List<DocumentChunk>();

        for (int i = 0; i < segments.Count; i++)
        {
            var text = segments[i].Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            chunks.Add(new DocumentChunk
            {
                DocumentId = "",
                ChunkIndex = i,
                Text = text,
                TokenCount = (text.Length + CharsPerToken - 1) / CharsPerToken,
            });
        }

        logger.LogDebug("CodeChunker produced {Count} chunks from {Chars} chars", chunks.Count, content.Length);
        return chunks;
    }

    private static List<string> SplitAtBoundaries(string text, int maxChars)
    {
        if (text.Length <= maxChars)
            return [text];

        foreach (var pattern in BoundaryPatterns)
        {
            var parts = SplitKeepingSeparator(text, pattern);
            if (parts.Count <= 1) continue;

            var result = new List<string>();
            var current = "";

            foreach (var part in parts)
            {
                var candidate = current + part;
                if (candidate.Length <= maxChars)
                {
                    current = candidate;
                }
                else
                {
                    if (!string.IsNullOrWhiteSpace(current))
                        result.Add(current);
                    current = part.Length > maxChars ? "" : part;
                    if (part.Length > maxChars)
                    {
                        // Hard split oversized segments
                        for (int i = 0; i < part.Length; i += maxChars)
                            result.Add(part.Substring(i, Math.Min(maxChars, part.Length - i)));
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(current))
                result.Add(current);

            if (result.Count > 1) return result;
        }

        // Hard split fallback
        var hard = new List<string>();
        for (int i = 0; i < text.Length; i += maxChars)
            hard.Add(text.Substring(i, Math.Min(maxChars, text.Length - i)));
        return hard;
    }

    private static List<string> SplitKeepingSeparator(string text, string separator)
    {
        var result = new List<string>();
        int start = 0;
        int idx;

        while ((idx = text.IndexOf(separator, start, StringComparison.Ordinal)) >= 0)
        {
            if (idx > start)
                result.Add(text[start..idx]);
            start = idx;
            // Next occurrence after current separator
            var nextIdx = text.IndexOf(separator, start + separator.Length, StringComparison.Ordinal);
            if (nextIdx < 0)
                break;
            result.Add(text[start..nextIdx]);
            start = nextIdx;
        }

        if (start < text.Length)
            result.Add(text[start..]);

        return result;
    }
}
