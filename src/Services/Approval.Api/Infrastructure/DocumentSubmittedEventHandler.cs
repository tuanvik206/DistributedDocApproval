using System.Text.Json;
using Approval.Api.Domain;
using IntegrationContracts;
using IntegrationContracts.Messaging;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Approval.Api.Infrastructure;

public sealed class DocumentSubmittedEventHandler(
    ApprovalDbContext db,
    ILogger<DocumentSubmittedEventHandler> logger) : IIntegrationEventHandler
{
    public async Task HandleAsync(IntegrationEventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.EventType != IntegrationEventTypes.DocumentSubmitted)
        {
            throw new JsonException($"Unsupported event type '{envelope.EventType}'.");
        }

        var payload = envelope.ReadPayload<DocumentSubmittedV1>()
            ?? throw new JsonException("Document submission event has no payload.");
        if (payload.DocumentId == Guid.Empty
            || payload.SubmissionId == Guid.Empty
            || payload.CreatorId == Guid.Empty
            || string.IsNullOrWhiteSpace(payload.Title)
            || payload.ApproverIdsInOrder is null
            || payload.ApproverIdsInOrder.Count is < 1 or > 3
            || payload.ApproverIdsInOrder.Any(id => id == Guid.Empty)
            || payload.ApproverIdsInOrder.Distinct().Count() != payload.ApproverIdsInOrder.Count)
        {
            throw new JsonException("Document submission event violates the approval workflow rules.");
        }

        if (await db.InboxMessages.AnyAsync(message => message.EventId == envelope.EventId, cancellationToken))
        {
            return;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.InboxMessages.Add(new InboxMessage { EventId = envelope.EventId, ProcessedAtUtc = DateTimeOffset.UtcNow });

        var existing = await db.Workflows.AnyAsync(
            workflow => workflow.SubmissionId == payload.SubmissionId || workflow.DocumentId == payload.DocumentId,
            cancellationToken);
        if (existing)
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Ignoring duplicate workflow creation for document {DocumentId}", payload.DocumentId);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var workflow = new ApprovalWorkflow
        {
            DocumentId = payload.DocumentId,
            SubmissionId = payload.SubmissionId,
            CreatorId = payload.CreatorId,
            Title = payload.Title.Trim(),
            Status = WorkflowStatus.Pending,
            CreatedAtUtc = now
        };
        for (var index = 0; index < payload.ApproverIdsInOrder.Count; index++)
        {
            workflow.Steps.Add(new ApprovalStep
            {
                WorkflowId = workflow.Id,
                StepOrder = index + 1,
                ApproverId = payload.ApproverIdsInOrder[index],
                Status = index == 0 ? ApprovalStepStatus.Active : ApprovalStepStatus.Waiting
            });
        }

        db.Workflows.Add(workflow);
        AddTaskAssignedEvent(db, workflow, workflow.Steps[0], envelope.CorrelationId, now);
        db.AuditEntries.Add(new AuditEntry
        {
            ActorId = payload.CreatorId,
            Action = "Submitted",
            WorkflowId = workflow.Id,
            DocumentId = payload.DocumentId,
            CreatedAtUtc = now,
            CorrelationId = envelope.CorrelationId
        });

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }

        logger.LogInformation(
            "Created sequential approval workflow {WorkflowId} for document {DocumentId} with {StepCount} steps; correlation {CorrelationId}",
            workflow.Id,
            payload.DocumentId,
            workflow.Steps.Count,
            envelope.CorrelationId);
    }

    private static void AddTaskAssignedEvent(
        ApprovalDbContext db,
        ApprovalWorkflow workflow,
        ApprovalStep step,
        string correlationId,
        DateTimeOffset now)
    {
        var envelope = IntegrationEventEnvelope.Create(
            IntegrationEventTypes.ApprovalTaskAssigned,
            correlationId,
            new ApprovalTaskAssignedV1(
                workflow.Id,
                workflow.DocumentId,
                workflow.CreatorId,
                step.ApproverId,
                workflow.Title,
                step.StepOrder,
                workflow.Steps.Count));
        db.OutboxMessages.Add(new OutboxMessage
        {
            EventId = envelope.EventId,
            EventType = envelope.EventType,
            RoutingKey = IntegrationRoutingKeys.ApprovalTaskAssigned,
            PayloadJson = JsonSerializer.Serialize(envelope),
            CorrelationId = correlationId,
            CreatedAtUtc = now
        });
    }
}
