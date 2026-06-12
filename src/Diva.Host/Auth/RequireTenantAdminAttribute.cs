using Diva.Infrastructure.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Diva.Host.Auth;

/// <summary>
/// Authorization filter that restricts an action or controller to tenant
/// administrators (role <c>admin</c>) and platform master admins.
///
/// Regular tenant users (e.g. roles <c>user</c> / <c>viewer</c>) receive 403.
/// These users may still chat with agents they have access to and view their
/// own session history; everything else (configuration, user management,
/// credentials, rule packs, agent authoring, etc.) is admin-only.
///
/// When no <see cref="Diva.Core.Models.TenantContext"/> is present (anonymous /
/// pre-auth requests such as login or public widget endpoints) this filter does
/// not block — authentication is enforced earlier by the JWT middleware.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RequireTenantAdminAttribute : Attribute, IAsyncAuthorizationFilter
{
    public Task OnAuthorizationAsync(AuthorizationFilterContext context)
    {
        var tenant = context.HttpContext.TryGetTenantContext();

        // No authenticated tenant context → leave to the auth pipeline.
        if (tenant is null)
            return Task.CompletedTask;

        if (tenant.IsAdmin || tenant.IsMasterAdmin)
            return Task.CompletedTask;

        context.Result = new ObjectResult(new
        {
            error = "forbidden",
            message = "This action requires tenant administrator privileges.",
        })
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };

        return Task.CompletedTask;
    }
}
