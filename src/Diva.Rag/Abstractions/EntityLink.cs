namespace Diva.Rag.Abstractions;

/// <summary>Cross-system entity reference extracted from chunk text.</summary>
public record EntityLink(string Type, string Id);
