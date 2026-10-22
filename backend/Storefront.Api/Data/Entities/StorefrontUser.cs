using Microsoft.AspNetCore.Identity;

namespace Storefront.Api.Data.Entities;

/// <summary>
/// A customer account, managed by ASP.NET Core Identity (password hashing, lockout, tokens).
/// No extra fields yet; the subclass exists so the model can grow without a breaking change.
/// </summary>
public sealed class StorefrontUser : IdentityUser
{
    public List<Order> Orders { get; } = [];
}
