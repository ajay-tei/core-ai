using Diva.Core.Models;

namespace Diva.Infrastructure.LiteLLM;

public sealed class ToolExecutorResult
{
    public string Output { get; init; } = "";
    public IReadOnlyList<ContentPart>? ContentParts { get; init; }
    public bool Failed { get; init; }
    public Exception? Error { get; init; }
    public long DurationMs { get; init; }
}
