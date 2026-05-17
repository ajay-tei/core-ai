namespace Diva.Rag;

/// <summary>RAG pipeline configuration. Bound from "RAG" config section.</summary>
public sealed class RagOptions
{
    public const string SectionName = "RAG";

    public bool Enabled { get; set; }
    public string QdrantUrl { get; set; } = "http://qdrant:6333";
    public string? QdrantApiKey { get; set; }
    public string QdrantGrpcUrl { get; set; } = "http://qdrant:6334";
    public string CollectionName { get; set; } = "diva_knowledge";
    public string EmbeddingProvider { get; set; } = "OpenAI";
    public string EmbeddingModel { get; set; } = "text-embedding-3-small";
    public string? EmbeddingApiKey { get; set; }
    public string? EmbeddingEndpoint { get; set; }
    public int EmbeddingDimensions { get; set; } = 1536;
    public int EmbeddingBatchSize { get; set; } = 100;
    public int MaxEmbeddingBatchesPerJob { get; set; }
    public int DefaultChunkSize { get; set; } = 512;
    public int DefaultChunkOverlap { get; set; } = 50;
    public int DefaultMaxResults { get; set; } = 5;
    public int DefaultTopK { get; set; } = 5;
    public float DefaultMinScore { get; set; } = 0.3f;
    public int MaxRetrievalTokens { get; set; } = 4000;
    public int RerankerCandidates { get; set; } = 20;
    public float MinScoreThreshold { get; set; } = 0.45f;
    public bool EnableHybridSearch { get; set; }
    public bool EnableReranking { get; set; } = true;
    public int MaxPinnedChunks { get; set; } = 3;
    public int MaxConcurrentIngestionJobs { get; set; } = 3;
    public int IngestionPollIntervalSeconds { get; set; } = 5;
    public int AgentFileMaxSizeKb { get; set; } = 1024;
    public int AgentIndexingQuotaChunksPerDay { get; set; }

    // ── Agent Memory (Phase 26.2) ─────────────────────────────────────────────
    public bool EnableAgentMemory { get; set; }
    public string MemoryCollectionName { get; set; } = "diva_agent_memory";
    public int WorkingMemoryDefaultTtlMinutes { get; set; } = 480;
    public int EpisodicMemoryDefaultTtlDays { get; set; } = 90;
    public int MemoryCleanupIntervalMinutes { get; set; } = 60;
}
