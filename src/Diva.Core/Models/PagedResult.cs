namespace Diva.Core.Models;

/// <summary>
/// Generic server-side paged list result. Shared by every admin-portal list endpoint
/// (originally introduced for <c>GET /api/sessions</c>; relocated out of the Session-specific
/// namespace so it can be reused by ~20 other controllers without a misleading namespace).
/// </summary>
public sealed class PagedResult<T>
{
    public List<T> Items { get; init; } = [];
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages { get; init; }
}
