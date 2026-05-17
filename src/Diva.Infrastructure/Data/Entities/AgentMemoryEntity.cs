using System;
using Diva.Core.Models;

namespace Diva.Infrastructure.Data.Entities;

public class AgentMemoryEntity : ITenantEntity
{
    public Guid Id { get; set; }
    public int TenantId { get; set; }
    public string AgentId { get; set; } // FK to AgentDefinitionEntity
    public string? UserId { get; set; } // Set for user-scoped memories; null for agent-only
    public string? SessionId { get; set; } // FK to AgentSessionEntity for working memory
    public string MemoryType { get; set; } // "working", "episodic", "semantic"
    public string Content { get; set; } // Original text
    public string VectorId { get; set; } // Qdrant point ID
    public string? TagsJson { get; set; } // JSON array of tags
    public DateTime? ExpiresAt { get; set; } // Null for permanent memories
    public DateTime CreatedAt { get; set; }
}