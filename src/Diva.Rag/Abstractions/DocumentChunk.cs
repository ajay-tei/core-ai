using System.Security.Cryptography;
using System.Text;

namespace Diva.Rag.Abstractions;

/// <summary>A chunk of text split from a document, ready for embedding.</summary>
public sealed class DocumentChunk
{
    public string DocumentId { get; set; } = "";
    public required int ChunkIndex { get; init; }
    public required string Text { get; set; }
    public int TokenCount { get; set; }
    public string Hash => ComputeHash(Text);

    // Metadata — populated by IMetadataEnricher
    public string? Domain { get; set; }
    public string? Product { get; set; }
    public string? Module { get; set; }
    public string? ContentType { get; set; }
    public string? SecurityLevel { get; set; }
    public string? Owner { get; set; }
    public List<string> Tags { get; set; } = [];
    public List<EntityLink> EntityLinks { get; set; } = [];

    private static string ComputeHash(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(hash);
    }
}
