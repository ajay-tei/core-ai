using Diva.Rag.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Diva.Rag.Embeddings;

/// <summary>
/// Factory that selects the appropriate embedding provider based on RagOptions.EmbeddingProvider.
/// </summary>
public sealed class EmbeddingServiceFactory(
    IHttpClientFactory httpFactory,
    IOptions<RagOptions> opts,
    ILoggerFactory loggerFactory)
{
    public IEmbeddingService Create()
    {
        // Match case-insensitively. "LmStudio" routes to the OpenAI-compatible service
        // (LM Studio exposes a /v1/embeddings endpoint). "Ollama" routes to the Ollama
        // native /api/embeddings endpoint. Everything else defaults to real OpenAI.
        return opts.Value.EmbeddingProvider.Trim().ToLowerInvariant() switch
        {
            "ollama" => new OllamaEmbeddingService(httpFactory, opts,
                loggerFactory.CreateLogger<OllamaEmbeddingService>()),
            "lmstudio" or "lm_studio" or "lm-studio" => new OpenAiEmbeddingService(httpFactory, opts,
                loggerFactory.CreateLogger<OpenAiEmbeddingService>()),
            _ => new OpenAiEmbeddingService(httpFactory, opts,
                loggerFactory.CreateLogger<OpenAiEmbeddingService>()),
        };
    }
}
