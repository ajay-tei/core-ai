using Diva.Rag.Abstractions;
using Microsoft.Extensions.Logging;

namespace Diva.Rag.Ingestion;

/// <summary>
/// Simple metadata enricher that applies taxonomy values from the source config to each chunk.
/// </summary>
public sealed class TaxonomyMetadataEnricher(ILogger<TaxonomyMetadataEnricher> logger) : IMetadataEnricher
{
    public DocumentChunk Enrich(DocumentChunk chunk, MetadataTaxonomy taxonomy)
    {
        chunk.Domain = taxonomy.Domain;
        chunk.Product = taxonomy.Product;
        chunk.Module = taxonomy.Module;
        chunk.ContentType = taxonomy.ContentType;
        chunk.SecurityLevel = taxonomy.SecurityLevel;
        chunk.Owner = taxonomy.Owner;
        if (taxonomy.CustomTags.Count > 0)
            chunk.Tags.AddRange(taxonomy.CustomTags);

        return chunk;
    }
}
