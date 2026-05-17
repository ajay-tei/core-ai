using Diva.Infrastructure.Data.Entities;

namespace Diva.Rag.Services;

/// <summary>
/// Structured user preference storage — deterministic key-value retrieval (no vector search).
/// Preferences are injected into agent prompts at runtime for personalization.
/// </summary>
public interface IUserPreferenceService
{
    /// <summary>Save or update a user preference. Upserts by (tenantId, userId, category, key).</summary>
    Task<UserPreferenceDto> SaveAsync(int tenantId, string userId, string category, string key, string value, CancellationToken ct = default);

    /// <summary>Get all preferences for a user, optionally filtered by category.</summary>
    Task<IReadOnlyList<UserPreferenceDto>> GetAsync(int tenantId, string userId, string? category = null, CancellationToken ct = default);

    /// <summary>Delete a specific preference by ID.</summary>
    Task<bool> DeleteAsync(int id, CancellationToken ct = default);

    /// <summary>Delete all preferences for a user in a category.</summary>
    Task<int> DeleteCategoryAsync(int tenantId, string userId, string category, CancellationToken ct = default);

    /// <summary>
    /// Get preferences formatted as a prompt-injectable string.
    /// Returns null if the user has no preferences.
    /// </summary>
    Task<string?> GetPromptContextAsync(int tenantId, string userId, CancellationToken ct = default);
}

public sealed record UserPreferenceDto(
    int Id, string UserId, string Category, string Key, string Value,
    DateTime CreatedAt, DateTime UpdatedAt);
