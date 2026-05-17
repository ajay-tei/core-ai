using System.Text.Json;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Diva.Rag.Abstractions;
using Diva.Rag.Chunking;
using Diva.Rag.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Diva.Rag.Ingestion;

/// <summary>
/// Orchestrates document ingestion: connect → chunk → enrich → 3-tier diff → embed → upsert.
/// </summary>
public sealed class DocumentIngestionPipeline(
    IEnumerable<IDocumentConnector> connectors,
    IDocumentChunker textChunker,
    IEmbeddingService embedding,
    IVectorRepository vectorRepo,
    IMetadataEnricher enricher,
    KnowledgeDocumentService docService,
    IDatabaseProviderFactory db,
    IOptions<RagOptions> opts,
    ILogger<DocumentIngestionPipeline> logger) : IDocumentIngestionPipeline
{
    private readonly RagOptions _opts = opts.Value;

    public async Task IngestAsync(
        string sourceId,
        int tenantId,
        string? documentUri = null,
        Action<IngestionProgressEvent>? onProgress = null,
        CancellationToken ct = default)
    {
        using var ctx = db.CreateDbContext();
        var source = await ctx.KnowledgeSources.FindAsync([sourceId], ct)
            ?? throw new InvalidOperationException($"Knowledge source '{sourceId}' not found");

        var connector = connectors.FirstOrDefault(c =>
            c.SourceType.Equals(source.SourceType, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No connector registered for source type '{source.SourceType}'");

        var taxonomy = !string.IsNullOrEmpty(source.TaxonomyJson)
            ? JsonSerializer.Deserialize<MetadataTaxonomy>(source.TaxonomyJson,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
              ?? new MetadataTaxonomy()
            : new MetadataTaxonomy();

        var sourceConfig = new DocumentSourceConfig
        {
            SourceId = sourceId,
            SourceType = source.SourceType,
            TenantId = tenantId,
            AgentId = source.AgentId,
            ScopeType = source.ScopeType,
            Taxonomy = taxonomy,
            Config = !string.IsNullOrEmpty(source.ConfigJson)
                ? JsonDocument.Parse(source.ConfigJson)
                : null,
        };

        var chunkingOptions = new ChunkingOptions
        {
            MaxTokens = _opts.DefaultChunkSize,
            Overlap = _opts.DefaultChunkOverlap,
        };

        int docsProcessed = 0, chunksAdded = 0, chunksUpdated = 0, chunksSkipped = 0;
        int batchCount = 0;

        onProgress?.Invoke(new IngestionProgressEvent { Phase = "connecting", CurrentDocument = source.Name });

        await foreach (var rawDoc in connector.ConnectAsync(sourceConfig, ct))
        {
            ct.ThrowIfCancellationRequested();

            // Filter by documentUri if specified (single-document re-index)
            if (documentUri is not null && !rawDoc.Uri.Equals(documentUri, StringComparison.OrdinalIgnoreCase))
                continue;

            // 3-tier version check
            var (needsReindex, existingDoc) = await docService.CheckVersionAsync(
                rawDoc.DocumentId, sourceId, tenantId, rawDoc.ExternalVersion, rawDoc.Content, ct);

            if (!needsReindex)
            {
                chunksSkipped++;
                docsProcessed++;
                continue;
            }

            onProgress?.Invoke(new IngestionProgressEvent
            {
                Phase = "chunking",
                CurrentDocument = rawDoc.Title,
                DocumentsProcessed = docsProcessed,
            });

            // Chunk
            var chunks = textChunker.Chunk(rawDoc.Content, chunkingOptions);

            // Enrich metadata
            var enrichedChunks = new List<DocumentChunk>(chunks.Count);
            foreach (var chunk in chunks)
            {
                chunk.DocumentId = rawDoc.DocumentId;
                var enriched = enricher.Enrich(chunk, taxonomy);
                enrichedChunks.Add(enriched);
            }

            // Check chunk-level hashes against existing chunks (Tier 3)
            var existingChunks = await ctx.KnowledgeChunks
                .Where(c => c.DocumentId == rawDoc.DocumentId)
                .AsNoTracking()
                .ToListAsync(ct);
            var existingHashMap = existingChunks.ToDictionary(c => c.ChunkIndex, c => c);

            var toEmbed = new List<(DocumentChunk Chunk, string VectorId, bool IsNew)>();
            int docChunksAdded = 0, docChunksUpdated = 0, docChunksRemoved = 0;

            foreach (var chunk in enrichedChunks)
            {
                if (existingHashMap.TryGetValue(chunk.ChunkIndex, out var existing) && existing.ChunkHash == chunk.Hash)
                {
                    chunksSkipped++;
                    continue;
                }

                var vectorId = existing?.VectorId ?? Guid.NewGuid().ToString();
                var isNew = existing is null;
                toEmbed.Add((chunk, vectorId, isNew));

                if (isNew) docChunksAdded++;
                else docChunksUpdated++;
            }

            // Mark removed chunks (exist in DB but not in new chunk set)
            var newIndexes = enrichedChunks.Select(c => c.ChunkIndex).ToHashSet();
            foreach (var existing in existingChunks.Where(c => !newIndexes.Contains(c.ChunkIndex)))
            {
                await vectorRepo.DeleteByIdsAsync([existing.VectorId], ct: ct);
                docChunksRemoved++;
            }

            // Embed in batches
            if (toEmbed.Count > 0)
            {
                onProgress?.Invoke(new IngestionProgressEvent
                {
                    Phase = "embedding",
                    CurrentDocument = rawDoc.Title,
                    DocumentsProcessed = docsProcessed,
                });

                for (int i = 0; i < toEmbed.Count; i += _opts.EmbeddingBatchSize)
                {
                    if (_opts.MaxEmbeddingBatchesPerJob > 0 && batchCount >= _opts.MaxEmbeddingBatchesPerJob)
                    {
                        logger.LogWarning("MaxEmbeddingBatchesPerJob ({Max}) reached — halting ingestion", _opts.MaxEmbeddingBatchesPerJob);
                        break;
                    }

                    var batch = toEmbed.Skip(i).Take(_opts.EmbeddingBatchSize).ToList();
                    var texts = batch.Select(b => b.Chunk.Text).ToList();
                    var embeddings = await embedding.EmbedBatchAsync(texts, ct);

                    var vectors = batch.Zip(embeddings, (b, emb) => new ChunkVector
                    {
                        VectorId = b.VectorId,
                        Chunk = b.Chunk,
                        Dense = emb,
                        ScopeType = source.ScopeType,
                        ScopeId = tenantId,
                        SourceId = sourceId,
                        AgentId = source.AgentId,
                        Title = rawDoc.Title,
                        SourceUri = rawDoc.Uri,
                        SourceType = source.SourceType,
                        ExternalVersion = rawDoc.ExternalVersion,
                        DocumentVersion = (existingDoc?.CurrentVersion ?? 0) + 1,
                    }).ToList();

                    await vectorRepo.UpsertAsync(vectors, ct: ct);
                    batchCount++;

                    // Upsert chunk records in DB
                    foreach (var (b, emb) in batch.Zip(embeddings))
                    {
                        var chunkEntity = existingHashMap.TryGetValue(b.Chunk.ChunkIndex, out var ex)
                            ? await ctx.KnowledgeChunks.FindAsync([ex.Id], ct)
                            : null;

                        if (chunkEntity is not null)
                        {
                            chunkEntity.ChunkHash = b.Chunk.Hash;
                            chunkEntity.TokenCount = b.Chunk.TokenCount;
                            chunkEntity.DocumentVersion = (existingDoc?.CurrentVersion ?? 0) + 1;
                            chunkEntity.IndexedAt = DateTime.UtcNow;
                            chunkEntity.IsStale = false;
                        }
                        else
                        {
                            ctx.KnowledgeChunks.Add(new KnowledgeChunkEntity
                            {
                                TenantId = tenantId,
                                DocumentId = rawDoc.DocumentId,
                                DocumentVersion = (existingDoc?.CurrentVersion ?? 0) + 1,
                                ChunkIndex = b.Chunk.ChunkIndex,
                                ChunkHash = b.Chunk.Hash,
                                VectorId = b.VectorId,
                                TokenCount = b.Chunk.TokenCount,
                            });
                        }
                    }

                    await ctx.SaveChangesAsync(ct);
                }
            }

            // Upsert document record and version snapshot
            await docService.UpsertAsync(
                rawDoc.DocumentId, sourceId, tenantId,
                rawDoc.Title, rawDoc.Uri, rawDoc.Content,
                rawDoc.ExternalVersion, "initial_ingest",
                docChunksAdded, docChunksUpdated, docChunksRemoved, ct);

            chunksAdded += docChunksAdded;
            chunksUpdated += docChunksUpdated;
            docsProcessed++;

            onProgress?.Invoke(new IngestionProgressEvent
            {
                Phase = "indexed",
                CurrentDocument = rawDoc.Title,
                DocumentsProcessed = docsProcessed,
                ChunksAdded = chunksAdded,
                ChunksUpdated = chunksUpdated,
                ChunksSkipped = chunksSkipped,
            });
        }

        // Update source stats
        source.LastIngestedAt = DateTime.UtcNow;
        source.DocumentCount = await ctx.KnowledgeDocuments.CountAsync(d => d.SourceId == sourceId, ct);
        source.ChunkCount = await ctx.KnowledgeChunks
            .Where(c => ctx.KnowledgeDocuments.Where(d => d.SourceId == sourceId).Select(d => d.DocumentId).Contains(c.DocumentId))
            .CountAsync(ct);
        ctx.KnowledgeSources.Update(source);
        await ctx.SaveChangesAsync(ct);

        onProgress?.Invoke(new IngestionProgressEvent
        {
            Phase = "completed",
            DocumentsProcessed = docsProcessed,
            TotalDocuments = docsProcessed,
            ChunksAdded = chunksAdded,
            ChunksUpdated = chunksUpdated,
            ChunksSkipped = chunksSkipped,
        });

        logger.LogInformation(
            "Ingestion completed for source '{SourceId}': {Docs} docs, {Added}+/{Updated}~/{Skipped}= chunks",
            sourceId, docsProcessed, chunksAdded, chunksUpdated, chunksSkipped);
    }
}
