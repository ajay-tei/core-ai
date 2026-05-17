using System.Security.Cryptography;
using System.Text;
using Diva.Infrastructure.Data;
using Diva.Infrastructure.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Diva.Rag.Services;

/// <summary>
/// Manages KnowledgeDocumentEntity versions with 3-tier hash-diff logic.
/// </summary>
public sealed class KnowledgeDocumentService(
    IDatabaseProviderFactory db,
    ILogger<KnowledgeDocumentService> logger)
{
    /// <summary>
    /// Check if a document needs re-indexing using the 3-tier diff strategy.
    /// Returns: (needsReindex, existingDoc)
    /// </summary>
    public async Task<(bool NeedsReindex, KnowledgeDocumentEntity? Existing)> CheckVersionAsync(
        string documentId, string sourceId, int tenantId,
        string? externalVersion, string content, CancellationToken ct)
    {
        using var ctx = db.CreateDbContext();
        var doc = await ctx.KnowledgeDocuments
            .FirstOrDefaultAsync(d => d.DocumentId == documentId && d.SourceId == sourceId, ct);

        if (doc is null)
            return (true, null);  // New document — index it

        // Tier 1: ExternalVersion fast-path (zero API cost)
        if (!string.IsNullOrEmpty(externalVersion) && doc.ExternalVersion == externalVersion)
        {
            logger.LogDebug("Document '{DocId}' skipped — ExternalVersion unchanged ({Version})", documentId, externalVersion);
            return (false, doc);
        }

        // Tier 2: Content hash check (fetch cost only)
        var contentHash = ComputeHash(content);
        if (doc.ContentHash == contentHash)
        {
            // Update ExternalVersion even if content unchanged (source might have metadata-only changes)
            doc.ExternalVersion = externalVersion;
            doc.LastIndexedAt = DateTime.UtcNow;
            ctx.KnowledgeDocuments.Update(doc);
            await ctx.SaveChangesAsync(ct);

            logger.LogDebug("Document '{DocId}' skipped — ContentHash unchanged", documentId);
            return (false, doc);
        }

        // Tier 3: Content changed — needs chunk-level re-index
        return (true, doc);
    }

    /// <summary>
    /// Create or update a document record and its version snapshot.
    /// </summary>
    public async Task<KnowledgeDocumentEntity> UpsertAsync(
        string documentId, string sourceId, int tenantId,
        string title, string uri, string content,
        string? externalVersion, string versionSource,
        int chunksAdded, int chunksUpdated, int chunksRemoved,
        CancellationToken ct)
    {
        using var ctx = db.CreateDbContext();
        var contentHash = ComputeHash(content);

        var doc = await ctx.KnowledgeDocuments
            .FirstOrDefaultAsync(d => d.DocumentId == documentId && d.SourceId == sourceId, ct);

        int versionNumber;
        if (doc is null)
        {
            versionNumber = 1;
            doc = new KnowledgeDocumentEntity
            {
                DocumentId = documentId,
                TenantId = tenantId,
                SourceId = sourceId,
                Title = title,
                Uri = uri,
                CurrentVersion = versionNumber,
                ExternalVersion = externalVersion,
                ContentHash = contentHash,
                LastModifiedAt = DateTime.UtcNow,
                LastIndexedAt = DateTime.UtcNow,
            };
            ctx.KnowledgeDocuments.Add(doc);
        }
        else
        {
            versionNumber = doc.CurrentVersion + 1;
            doc.Title = title;
            doc.Uri = uri;
            doc.CurrentVersion = versionNumber;
            doc.ExternalVersion = externalVersion;
            doc.ContentHash = contentHash;
            doc.LastModifiedAt = DateTime.UtcNow;
            doc.LastIndexedAt = DateTime.UtcNow;
            ctx.KnowledgeDocuments.Update(doc);
        }

        // Immutable version snapshot
        ctx.KnowledgeDocumentVersions.Add(new KnowledgeDocumentVersionEntity
        {
            TenantId = tenantId,
            DocumentId = documentId,
            VersionNumber = versionNumber,
            ContentHash = contentHash,
            ExternalVersion = externalVersion,
            Source = versionSource,
            ChunksAdded = chunksAdded,
            ChunksUpdated = chunksUpdated,
            ChunksRemoved = chunksRemoved,
        });

        await ctx.SaveChangesAsync(ct);
        logger.LogDebug("Upserted document '{DocId}' v{Version} ({Added}+/{Updated}~/{Removed}-)",
            documentId, versionNumber, chunksAdded, chunksUpdated, chunksRemoved);
        return doc;
    }

    private static string ComputeHash(string content)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexStringLower(hash);
    }
}
