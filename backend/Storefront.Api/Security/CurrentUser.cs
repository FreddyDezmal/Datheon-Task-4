using System.Security.Claims;

namespace Storefront.Api.Security;

public static class CurrentUser
{
    /// <summary>The signed-in user's ID from the validated token, or null for an anonymous request.</summary>
    public static string? GetUserId(this ClaimsPrincipal user) =>
        user.Identity?.IsAuthenticated == true ? user.FindFirstValue(ClaimTypes.NameIdentifier) : null;
}
