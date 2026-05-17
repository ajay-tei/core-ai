using System;
using Diva.Core.Models;

namespace Diva.Infrastructure.Data.Entities;

/// <summary>
/// Structured key-value user preference. Stored in SQLite for fast, deterministic retrieval.
/// Injected into agent system prompts by TenantAwarePromptBuilder at runtime.
/// </summary>
public class UserPreferenceEntity : ITenantEntity
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public string UserId { get; set; } = null!;
    public string Category { get; set; } = null!;   // e.g. "coding_style", "communication", "domain", "general"
    public string Key { get; set; } = null!;         // e.g. "preferred_language", "output_format"
    public string Value { get; set; } = null!;       // e.g. "TypeScript", "JSON"
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
