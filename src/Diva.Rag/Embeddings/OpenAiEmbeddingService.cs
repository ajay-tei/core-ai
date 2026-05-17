using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Diva.Rag.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Diva.Rag.Embeddings;

/// <summary>
/// OpenAI-compatible embedding service. Posts to /v1/embeddings endpoint.
/// Supports batching up to <see cref="RagOptions.EmbeddingBatchSize"/>.
/// </summary>
public sealed class OpenAiEmbeddingService : IEmbeddingService
{
    private readonly HttpClient _http;
    private readonly RagOptions _opts;
    private readonly ILogger<OpenAiEmbeddingService> _logger;
    private readonly bool _isRealOpenAi;

    public OpenAiEmbeddingService(
        IHttpClientFactory httpFactory,
        IOptions<RagOptions> opts,
        ILogger<OpenAiEmbeddingService> logger)
    {
        _opts = opts.Value;
        _logger = logger;
        _http = httpFactory.CreateClient("RagEmbedding");

        _isRealOpenAi = string.IsNullOrWhiteSpace(_opts.EmbeddingEndpoint);
        var endpoint = _isRealOpenAi
            ? "https://api.openai.com"
            : _opts.EmbeddingEndpoint!.TrimEnd('/');
        _http.BaseAddress = new Uri(endpoint);
        if (!string.IsNullOrEmpty(_opts.EmbeddingApiKey))
            _http.DefaultRequestHeaders.Authorization = new("Bearer", _opts.EmbeddingApiKey);
    }

    public int Dimensions => _opts.EmbeddingDimensions;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct)
    {
        var results = await EmbedBatchAsync([text], ct);
        return results[0];
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (texts.Count == 0) return [];

        var request = new EmbeddingRequest
        {
            Model = _opts.EmbeddingModel,
            Input = texts.ToList(),
            // Only pass 'dimensions' for real OpenAI (text-embedding-3-*) which supports
            // truncated output dimensions. LM Studio, Ollama-compat, and fixed-dim models
            // (nomic-embed, mxbai-embed) reject or ignore this field — omit it for them.
            Dimensions = _isRealOpenAi ? _opts.EmbeddingDimensions : null,
        };

        var response = await _http.PostAsJsonAsync("/v1/embeddings", request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<EmbeddingResponse>(ct);
        if (result?.Data is null || result.Data.Count != texts.Count)
            throw new InvalidOperationException($"Embedding API returned {result?.Data?.Count ?? 0} results for {texts.Count} inputs");

        _logger.LogDebug("Embedded {Count} texts, {Tokens} total tokens", texts.Count, result.Usage?.TotalTokens);

        return result.Data
            .OrderBy(d => d.Index)
            .Select(d => d.Embedding)
            .ToList();
    }

    private sealed class EmbeddingRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "";

        [JsonPropertyName("input")]
        public List<string> Input { get; set; } = [];

        [JsonPropertyName("dimensions")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? Dimensions { get; set; }
    }

    private sealed class EmbeddingResponse
    {
        [JsonPropertyName("data")]
        public List<EmbeddingData> Data { get; set; } = [];

        [JsonPropertyName("usage")]
        public EmbeddingUsage? Usage { get; set; }
    }

    private sealed class EmbeddingData
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("embedding")]
        public float[] Embedding { get; set; } = [];
    }

    private sealed class EmbeddingUsage
    {
        [JsonPropertyName("total_tokens")]
        public int TotalTokens { get; set; }
    }
}
