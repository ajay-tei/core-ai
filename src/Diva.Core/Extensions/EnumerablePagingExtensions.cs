namespace Diva.Core.Extensions;

/// <summary>
/// Synchronous in-memory counterpart to <c>Diva.Infrastructure</c>'s EF Core-based
/// <c>IQueryable&lt;T&gt;.ToPagedResultAsync</c>. Use this one when the final sequence being
/// paged is no longer a translatable <see cref="IQueryable{T}"/> — e.g. a controller that
/// merges a tenant's own rows with separately-fetched shared/group-template rows before
/// paging (as <c>AgentsController</c> does for Agents). Has no external dependencies, so it
/// lives alongside <see cref="Models.PagedResult{T}"/> in <c>Diva.Core</c>.
/// </summary>
public static class EnumerablePagingExtensions
{
    public static Models.PagedResult<T> ToPagedResult<T>(this IEnumerable<T> source, int page, int pageSize)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize < 1 ? 1 : pageSize;

        var materialized = source as IReadOnlyCollection<T> ?? source.ToList();
        var total = materialized.Count;
        var items = materialized.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return new Models.PagedResult<T>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
            TotalPages = (int)Math.Ceiling((double)total / pageSize),
        };
    }

    /// <summary>
    /// Maps a <see cref="Models.PagedResult{T}"/>'s <c>Items</c> through <paramref name="selector"/>
    /// (e.g. entity → DTO, or a decrypt/mask step) while keeping Page/PageSize/TotalCount/TotalPages
    /// unchanged. Lets a controller paginate on the raw entity/projection query first, then apply a
    /// more expensive per-item transform only to the current page's rows.
    /// </summary>
    public static Models.PagedResult<TDto> MapItems<T, TDto>(this Models.PagedResult<T> source, Func<T, TDto> selector) => new()
    {
        Items = source.Items.Select(selector).ToList(),
        Page = source.Page,
        PageSize = source.PageSize,
        TotalCount = source.TotalCount,
        TotalPages = source.TotalPages,
    };
}
