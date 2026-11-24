using Approval.Api.Domain;
using IntegrationContracts.Messaging;
using Microsoft.EntityFrameworkCore;

namespace Approval.Api.Infrastructure;

public sealed class ApprovalOutboxMessageStore(ApprovalDbContext db) : IOutboxMessageStore
{
    public async Task<IReadOnlyList<PendingOutboxMessage>> GetPendingAsync(int limit, CancellationToken cancellationToken) =>
        await db.OutboxMessages.AsNoTracking()
            .Where(message => message.PublishedAtUtc == null)
            .OrderBy(message => message.CreatedAtUtc)
            .Take(limit)
            .Select(message => new PendingOutboxMessage(
                message.EventId,
                message.RoutingKey,
                message.PayloadJson,
                message.CorrelationId,
                message.AttemptCount))
            .ToListAsync(cancellationToken);

    public async Task MarkPublishedAsync(Guid eventId, CancellationToken cancellationToken)
    {
        var message = await db.OutboxMessages.SingleAsync(item => item.EventId == eventId, cancellationToken);
        message.PublishedAtUtc = DateTimeOffset.UtcNow;
        message.LastError = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordFailureAsync(Guid eventId, string error, CancellationToken cancellationToken)
    {
        var message = await db.OutboxMessages.SingleAsync(item => item.EventId == eventId, cancellationToken);
        message.AttemptCount++;
        message.LastError = error.Length > 2000 ? error[..2000] : error;
        await db.SaveChangesAsync(cancellationToken);
    }
}
