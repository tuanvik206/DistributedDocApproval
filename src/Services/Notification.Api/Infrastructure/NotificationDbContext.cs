using Microsoft.EntityFrameworkCore;
using Notification.Api.Domain;

namespace Notification.Api.Infrastructure;

public sealed class NotificationDbContext(DbContextOptions<NotificationDbContext> options) : DbContext(options)
{
    public DbSet<NotificationRecord> Notifications => Set<NotificationRecord>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var notifications = modelBuilder.Entity<NotificationRecord>();
        notifications.ToTable("Notifications");
        notifications.HasKey(notification => notification.Id);
        notifications.Property(notification => notification.Type).HasMaxLength(80).IsRequired();
        notifications.Property(notification => notification.Message).HasMaxLength(500).IsRequired();
        notifications.HasIndex(notification => notification.EventId).IsUnique();
        notifications.HasIndex(notification => new { notification.UserId, notification.CreatedAtUtc });

        var inbox = modelBuilder.Entity<InboxMessage>();
        inbox.ToTable("InboxMessages");
        inbox.HasKey(message => message.EventId);
    }
}
