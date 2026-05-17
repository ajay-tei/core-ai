namespace Diva.Rag.Abstractions;

/// <summary>
/// Scrubs PII and secrets from content before embedding.
/// </summary>
public interface IContentScrubber
{
    /// <summary>
    /// Redact sensitive patterns from text.
    /// </summary>
    string Scrub(string text);
}
