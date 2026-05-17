using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Diva.Rag.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Diva.Rag.Embeddings;

/// <summary>
/// Ollama-compatible embedding service for air-gapped deployments.
/// Posts to /api/embeddings endpoint.
/// </summary>
public sealed class OllamaEmbeddingService : IEmbeddingService
{
    private readonly HttpClient _http;
    private readonly RagOptions _opts;
    private readonly ILogger<OllamaEmbeddingService> _logger;

    public OllamaEmbeddingService(
        IHttpClientFactory httpFactory,
        IOptions<RagOptions> opts,
        ILogger<OllamaEmbeddingService> logger)
    {
        _opts = opts.Value;
        _logger = logger;
        _http = httpFactory.CreateClient("RagEmbedding");
        var endpoint = _opts.EmbeddingEndpoint?.TrimEnd('/') ?? "http://localhost:11434";
        _http.BaseAddress = new Uri(endpoint);
    }

    public int Dimensions => _opts.EmbeddingDimensions;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var request = new { model = _opts.EmbeddingModel, prompt = text };
        var response = await _http.PostAsJsonAsync("/api/embeddings", request, ct);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<OllamaResponse>(ct);
        return result?.Embedding ?? throw new InvalidOperationException("Ollama returned null embedding");
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        // Ollama doesn't support batch — embed sequentially
        var results = new List<float[]>(texts.Count);
        foreach (var text in texts)
            results.Add(await EmbedAsync(text, ct));
        _logger.LogDebug("Ollama embedded {Count} texts sequentially", texts.Count);
        return results;
    }

    private sealed class OllamaResponse
    {
        [JsonPropertyName("embedding")]
        public float[] Embedding { get; set; } = [];
    }
}
