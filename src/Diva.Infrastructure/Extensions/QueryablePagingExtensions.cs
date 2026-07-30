using Diva.Core.Models;
using Microsoft.EntityFrameworkCore;

namespace Diva.Infrastructure.Extensions;

/// <summary>
/// Shared server-side pagination helper for admin-portal list endpoints. Mirrors the
/// <c>CountAsync</c> + <c>Skip/Take</c> logic originally written by hand in
/// <c>SessionsController.GetSessions</c> so every other controller can reuse the same
/// implementation instead of duplicating it.
///
/// Lives in <c>Diva.Infrastructure</c> (not <c>Diva.Core</c>) because it depends on
/// <c>Microsoft.EntityFrameworkCore</c>'s async <see cref="IQueryable{T}"/> operators, and
/// <c>Diva.Core</c> is documented to have no external dependencies.
/// </summary>
public static class QueryablePagingExtensions
{
    /// <summary>
    /// Executes <paramref name="query"/> as a paged result: counts the total matching rows,
    /// then applies <c>Skip((page - 1) * pageSize).Take(pageSize)</c>. <paramref name="page"/>
    /// is clamped to a minimum of 1 and <paramref name="pageSize"/> to a minimum of 1 so a
    /// bad/zero query-string value can't produce a negative <c>Skip</c>.
    /// </summary>
    public static async Task<PagedResult<T>> ToPagedResultAsync<T>(
        this IQueryable<T> query,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        page = page < 1 ? 1 : page;
        pageSize = pageSize < 1 ? 1 : pageSize;

        var total = await query.CountAsync(ct);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return new PagedResult<T>
        {
            Items = items,
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
            TotalPages = (int)Math.Ceiling((double)total / pageSize),
        };
    }
}
