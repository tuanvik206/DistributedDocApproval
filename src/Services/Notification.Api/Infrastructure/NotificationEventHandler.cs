using System.Text.Json;
using IntegrationContracts;
using IntegrationContracts.Messaging;
using Microsoft.EntityFrameworkCore;
using Notification.Api.Domain;
using Npgsql;

namespace Notification.Api.Infrastructure;

public sealed class NotificationEventHandler(
    NotificationDbContext db,
    ILogger<NotificationEventHandler> logger) : IIntegrationEventHandler
{
    public async Task HandleAsync(IntegrationEventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (await db.InboxMessages.AnyAsync(message => message.EventId == envelope.EventId, cancellationToken))
        {
            return;
        }

        var notification = CreateNotification(envelope);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.InboxMessages.Add(new InboxMessage { EventId = envelope.EventId, ProcessedAtUtc = DateTimeOffset.UtcNow });
        if (notification is not null)
        {
            db.Notifications.Add(notification);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(cancellationToken);
            logger.LogInformation("Ignoring duplicate notification event {EventId}", envelope.EventId);
        }

        if (notification is not null)
        {
            logger.LogInformation(
                "Stored notification {NotificationId} for user {UserId}, document {DocumentId}; correlation {CorrelationId}",
                notification.Id,
                notification.UserId,
                notification.DocumentId,
                envelope.CorrelationId);
        }
    }

    private static NotificationRecord? CreateNotification(IntegrationEventEnvelope envelope) => envelope.EventType switch
    {
        IntegrationEventTypes.ApprovalTaskAssigned => CreateAssignment(envelope),
        IntegrationEventTypes.DocumentApproved => CreateCompletion(envelope, "DocumentApproved", "đã được phê duyệt"),
        IntegrationEventTypes.DocumentRejected => CreateCompletion(envelope, "DocumentRejected", "đã bị từ chối"),
        _ => throw new JsonException($"Unsupported event type '{envelope.EventType}'.")
    };

    private static NotificationRecord CreateAssignment(IntegrationEventEnvelope envelope)
    {
        var payload = envelope.ReadPayload<ApprovalTaskAssignedV1>()
            ?? throw new JsonException("Approval task event has no payload.");
        return new NotificationRecord
        {
            UserId = payload.ApproverId,
            EventId = envelope.EventId,
            DocumentId = payload.DocumentId,
            Type = "ApprovalTaskAssigned",
            Message = $"Tài liệu '{payload.Title}' đang chờ bạn duyệt ở bước {payload.StepOrder}/{payload.TotalSteps}.",
            CreatedAtUtc = envelope.OccurredAtUtc
        };
    }

    private static NotificationRecord CreateCompletion(
        IntegrationEventEnvelope envelope,
        string type,
        string statusText)
    {
        var payload = envelope.ReadPayload<DocumentCompletedV1>()
            ?? throw new JsonException("Document completion event has no payload.");
        return new NotificationRecord
        {
            UserId = payload.CreatorId,
            EventId = envelope.EventId,
            DocumentId = payload.DocumentId,
            Type = type,
            Message = $"Tài liệu '{payload.Title}' {statusText}.",
            CreatedAtUtc = envelope.OccurredAtUtc
        };
    }
}
