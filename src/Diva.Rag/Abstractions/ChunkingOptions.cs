namespace Diva.Rag.Abstractions;

/// <summary>Options for text chunking.</summary>
public sealed class ChunkingOptions
{
    public int MaxTokens { get; set; } = 512;
    public int Overlap { get; set; } = 50;
}
