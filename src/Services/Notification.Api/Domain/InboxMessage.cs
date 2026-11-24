namespace Notification.Api.Domain;

public sealed class InboxMessage
{
    public Guid EventId { get; set; }

    public DateTimeOffset ProcessedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
