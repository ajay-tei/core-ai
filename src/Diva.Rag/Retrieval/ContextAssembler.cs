using Diva.Rag.Abstractions;

namespace Diva.Rag.Retrieval;

/// <summary>
/// Assembles retrieved chunks into a formatted context string for LLM injection.
/// </summary>
public sealed class ContextAssembler : IContextAssembler
{
    public string Assemble(IReadOnlyList<RetrievedChunk> chunks, int maxTokens)
    {
        if (chunks.Count == 0) return string.Empty;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Retrieved Knowledge Context");
        sb.AppendLine();

        int estimatedTokens = 10; // Header overhead

        foreach (var chunk in chunks)
        {
            // Rough token estimate: chars / 4
            var chunkTokens = chunk.Text.Length / 4;
            if (estimatedTokens + chunkTokens > maxTokens)
                break;

            sb.AppendLine($"### [{chunk.Title}]({chunk.SourceUri}) (score: {chunk.Score:F2})");
            if (!string.IsNullOrEmpty(chunk.Domain) || !string.IsNullOrEmpty(chunk.Product))
            {
                var parts = new List<string>();
                if (!string.IsNullOrEmpty(chunk.Domain)) parts.Add($"domain: {chunk.Domain}");
                if (!string.IsNullOrEmpty(chunk.Product)) parts.Add($"product: {chunk.Product}");
                if (!string.IsNullOrEmpty(chunk.ContentType)) parts.Add($"type: {chunk.ContentType}");
                sb.AppendLine($"*{string.Join(" | ", parts)}*");
            }
            sb.AppendLine();
            sb.AppendLine(chunk.Text);
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();

            estimatedTokens += chunkTokens + 10; // Overhead per chunk
        }

        return sb.ToString().TrimEnd();
    }
}
