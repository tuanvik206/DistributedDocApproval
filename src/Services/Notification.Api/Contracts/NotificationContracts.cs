namespace Notification.Api.Contracts;

public sealed record NotificationResponse(
    Guid Id,
    Guid DocumentId,
    string Type,
    string Message,
    bool IsRead,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? ReadAtUtc);

public sealed record ReadNotificationRequest(bool IsRead);
