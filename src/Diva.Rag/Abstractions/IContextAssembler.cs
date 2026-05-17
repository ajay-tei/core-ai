namespace Diva.Rag.Abstractions;

/// <summary>
/// Assembles retrieved chunks into a context string for LLM injection.
/// </summary>
public interface IContextAssembler
{
    /// <summary>
    /// Format retrieved chunks into a single context string, limited to maxTokens.
    /// </summary>
    string Assemble(IReadOnlyList<RetrievedChunk> chunks, int maxTokens);
}
