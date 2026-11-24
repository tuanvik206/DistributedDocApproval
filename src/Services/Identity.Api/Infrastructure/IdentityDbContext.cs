using Identity.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Identity.Api.Infrastructure;

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public DbSet<UserAccount> Users => Set<UserAccount>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var users = modelBuilder.Entity<UserAccount>();
        users.ToTable("Users");
        users.HasKey(user => user.Id);
        users.Property(user => user.Username).HasMaxLength(64).IsRequired();
        users.Property(user => user.NormalizedUsername).HasMaxLength(64).IsRequired();
        users.Property(user => user.FullName).HasMaxLength(160).IsRequired();
        users.Property(user => user.Email).HasMaxLength(254).IsRequired();
        users.Property(user => user.NormalizedEmail).HasMaxLength(254).IsRequired();
        users.Property(user => user.PasswordHash).HasMaxLength(512).IsRequired();
        users.Property(user => user.Role).HasConversion<string>().HasMaxLength(16).IsRequired();
        users.Property(user => user.CreatedAtUtc).IsRequired();
        users.HasIndex(user => user.NormalizedUsername).IsUnique();
        users.HasIndex(user => user.NormalizedEmail).IsUnique();
    }
}
