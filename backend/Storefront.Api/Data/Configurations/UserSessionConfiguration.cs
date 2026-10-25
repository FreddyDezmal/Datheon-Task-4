using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Storefront.Api.Data.Entities;

namespace Storefront.Api.Data.Configurations;

public sealed class UserSessionConfiguration : IEntityTypeConfiguration<UserSession>
{
    public const int IdLength = 32;

    public void Configure(EntityTypeBuilder<UserSession> session)
    {
        session.ToTable("UserSessions");
        session.HasKey(s => s.Id);
        session.Property(s => s.Id).HasMaxLength(IdLength);
        session.Property(s => s.CurrentTokenId).HasMaxLength(IdLength);
        session.Property(s => s.PreviousTokenId).HasMaxLength(IdLength);
        session.Property(s => s.SecurityStamp).HasMaxLength(256);
        session.Property(s => s.RevokedReason).HasMaxLength(32);

        // Always UTC (see OrderConfiguration).
        session.Property(s => s.CreatedAtUtc).HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        session.Property(s => s.ExpiresAtUtc).HasConversion(v => v, v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
        session.Property(s => s.LastRefreshedAtUtc).HasConversion(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
        session.Property(s => s.PreviousTokenValidUntilUtc).HasConversion(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);
        session.Property(s => s.RevokedAtUtc).HasConversion(v => v, v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

        // Listing and pruning a user's sessions at sign-in.
        session.HasIndex(s => new { s.UserId, s.ExpiresAtUtc });

        // Deleting an account deletes its sessions.
        session.HasOne(s => s.User)
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
