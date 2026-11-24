using System.Text.Json;
using Document.Api.Domain;
using IntegrationContracts;
using IntegrationContracts.Messaging;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Document.Api.Infrastructure;

public sealed class DocumentResultEventHandler(
    DocumentDbContext db,
    ILogger<DocumentResultEventHandler> logger) : IIntegrationEventHandler
{
    public async Task HandleAsync(IntegrationEventEnvelope envelope, CancellationToken cancellationToken)
    {
        var newStatus = envelope.EventType switch
        {
            IntegrationEventTypes.DocumentApproved => DocumentStatus.Approved,
            IntegrationEventTypes.DocumentRejected => DocumentStatus.Rejected,
            _ => throw new JsonException($"Unsupported event type '{envelope.EventType}'.")
        };
        var payload = envelope.ReadPayload<DocumentCompletedV1>()
            ?? throw new JsonException("Document completion event has no payload.");

        if (await db.InboxMessages.AnyAsync(message => message.EventId == envelope.EventId, cancellationToken))
        {
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.InboxMessages.Add(new InboxMessage { EventId = envelope.EventId, ProcessedAtUtc = DateTimeOffset.UtcNow });
        var document = await db.Documents.SingleOrDefaultAsync(
            item => item.Id == payload.DocumentId && item.SubmissionId == payload.SubmissionId,
            cancellationToken);
        if (document is null)
        {
            logger.LogWarning(
                "Completion event {EventId} does not match a document submission; correlation {CorrelationId}",
                envelope.EventId,
                envelope.CorrelationId);
        }
        else if (document.Status == DocumentStatus.Pending)
        {
            document.Status = newStatus;
            document.CompletedAtUtc = DateTimeOffset.UtcNow;
            document.Version++;
            logger.LogInformation(
                "Applying {Status} to document {DocumentId} from event {EventId}; correlation {CorrelationId}",
                newStatus,
                document.Id,
                envelope.EventId,
                envelope.CorrelationId);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(cancellationToken);
            logger.LogInformation("Ignoring duplicate completion event {EventId}", envelope.EventId);
        }
    }
}
