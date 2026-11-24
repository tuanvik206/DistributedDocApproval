using System.Security.Claims;
using System.Text.Json;
using Approval.Api.Contracts;
using Approval.Api.Domain;
using Approval.Api.Infrastructure;
using IntegrationContracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Approval.Api.Endpoints;

public static class ApprovalEndpoints
{
    public static IEndpointRouteBuilder MapApprovalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var approvals = endpoints.MapGroup("/api/approvals")
            .RequireAuthorization(policy => policy.RequireRole("Creator", "Approver"));

        approvals.MapGet("/my-tasks", GetMyTasksAsync)
            .RequireAuthorization(policy => policy.RequireRole("Approver"));
        approvals.MapGet("/documents/{documentId:guid}", GetWorkflowAsync);
        approvals.MapPost("/tasks/{taskId:guid}/decisions", DecideAsync)
            .RequireAuthorization(policy => policy.RequireRole("Approver"));
        return endpoints;
    }

    private static async Task<IResult> GetMyTasksAsync(
        ClaimsPrincipal principal,
        ApprovalDbContext db,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(principal, out var approverId))
        {
            return Results.Unauthorized();
        }

        var tasks = await db.Steps.AsNoTracking()
            .Where(step => step.ApproverId == approverId
                && step.Status == ApprovalStepStatus.Active
                && step.Workflow.Status == WorkflowStatus.Pending)
            .OrderBy(step => step.Workflow.CreatedAtUtc)
            .Select(step => new TaskResponse(
                step.Id,
                step.WorkflowId,
                step.Workflow.DocumentId,
                step.Workflow.Title,
                step.Status.ToString(),
                step.StepOrder,
                step.Workflow.Steps.Count,
                step.Workflow.CreatedAtUtc))
            .ToListAsync(cancellationToken);

        return Results.Ok(tasks);
    }

    private static async Task<IResult> GetWorkflowAsync(
        Guid documentId,
        ClaimsPrincipal principal,
        ApprovalDbContext db,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(principal, out var userId))
        {
            return Results.Unauthorized();
        }

        var workflow = await db.Workflows.AsNoTracking()
            .Include(item => item.Steps)
            .ThenInclude(step => step.Decision)
            .SingleOrDefaultAsync(item => item.DocumentId == documentId, cancellationToken);
        if (workflow is null || (workflow.CreatorId != userId && workflow.Steps.All(step => step.ApproverId != userId)))
        {
            return Results.NotFound();
        }

        return Results.Ok(ToResponse(workflow));
    }

    private static async Task<IResult> DecideAsync(
        Guid taskId,
        CreateDecisionRequest request,
        HttpContext httpContext,
        ClaimsPrincipal principal,
        ApprovalDbContext db,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        if (!TryGetUserId(principal, out var approverId))
        {
            return Results.Unauthorized();
        }

        if (!Enum.TryParse<DecisionKind>(request.Decision, ignoreCase: true, out var decision)
            || !Enum.IsDefined(decision))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["decision"] = ["Decision must be Approve or Reject."]
            }, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var comment = request.Comment?.Trim() ?? string.Empty;
        if (comment.Length > 2000 || (decision == DecisionKind.Reject && comment.Length == 0))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["comment"] = [decision == DecisionKind.Reject
                    ? "A rejection reason is required and must be at most 2000 characters."
                    : "Comment must be at most 2000 characters."]
            }, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var step = await db.Steps
            .Include(item => item.Workflow)
            .SingleOrDefaultAsync(item => item.Id == taskId, cancellationToken);
        if (step is null || step.ApproverId != approverId)
        {
            return Results.NotFound();
        }

        if (step.Status != ApprovalStepStatus.Active || step.Workflow.Status != WorkflowStatus.Pending)
        {
            return Results.Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "This approval task is not active."
            });
        }

        var now = DateTimeOffset.UtcNow;
        var nextStepStatus = decision == DecisionKind.Approve
            ? ApprovalStepStatus.Approved
            : ApprovalStepStatus.Rejected;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var updatedRows = await db.Steps
            .Where(item => item.Id == taskId
                && item.ApproverId == approverId
                && item.Status == ApprovalStepStatus.Active)
            .ExecuteUpdateAsync(update => update
                .SetProperty(item => item.Status, nextStepStatus)
                .SetProperty(item => item.DecidedAtUtc, now)
                .SetProperty(item => item.Version, item => item.Version + 1), cancellationToken);

        if (updatedRows != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return Results.Conflict(new ProblemDetails
            {
                Status = StatusCodes.Status409Conflict,
                Title = "This approval task was already processed."
            });
        }

        db.Decisions.Add(new ApprovalDecision
        {
            StepId = step.Id,
            ApproverId = approverId,
            Decision = decision,
            Comment = comment,
            CreatedAtUtc = now
        });

        var correlationId = GetCorrelationId(httpContext);
        db.AuditEntries.Add(new AuditEntry
        {
            ActorId = approverId,
            Action = decision.ToString(),
            WorkflowId = step.WorkflowId,
            DocumentId = step.Workflow.DocumentId,
            CreatedAtUtc = now,
            CorrelationId = correlationId
        });

        if (decision == DecisionKind.Reject)
        {
            step.Workflow.Status = WorkflowStatus.Rejected;
            step.Workflow.CompletedAtUtc = now;
            await db.Steps
                .Where(item => item.WorkflowId == step.WorkflowId && item.Status == ApprovalStepStatus.Waiting)
                .ExecuteUpdateAsync(update => update.SetProperty(item => item.Status, ApprovalStepStatus.Skipped), cancellationToken);
            AddResultEvent(db, step.Workflow, comment, approved: false, correlationId, now);
        }
        else
        {
            var next = await db.Steps
                .Where(item => item.WorkflowId == step.WorkflowId && item.Status == ApprovalStepStatus.Waiting)
                .OrderBy(item => item.StepOrder)
                .FirstOrDefaultAsync(cancellationToken);
            if (next is null)
            {
                step.Workflow.Status = WorkflowStatus.Approved;
                step.Workflow.CompletedAtUtc = now;
                AddResultEvent(db, step.Workflow, comment, approved: true, correlationId, now);
            }
            else
            {
                next.Status = ApprovalStepStatus.Active;
                AddTaskAssignedEvent(db, step.Workflow, next, correlationId, now);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        loggerFactory.CreateLogger("Approval.Api.Decisions").LogInformation(
            "Recorded {Decision} for document {DocumentId}, workflow {WorkflowId}, step {StepOrder}; correlation {CorrelationId}",
            decision,
            step.Workflow.DocumentId,
            step.WorkflowId,
            step.StepOrder,
            correlationId);

        return Results.Ok(new DecisionResponse(
            db.Decisions.Local.Single(item => item.StepId == step.Id).Id,
            step.Id,
            approverId,
            decision.ToString(),
            comment,
            now));
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
        db.OutboxMessages.Add(ToOutbox(envelope, IntegrationRoutingKeys.ApprovalTaskAssigned, now));
    }

    private static void AddResultEvent(
        ApprovalDbContext db,
        ApprovalWorkflow workflow,
        string comment,
        bool approved,
        string correlationId,
        DateTimeOffset now)
    {
        var eventType = approved ? IntegrationEventTypes.DocumentApproved : IntegrationEventTypes.DocumentRejected;
        var routingKey = approved ? IntegrationRoutingKeys.DocumentApproved : IntegrationRoutingKeys.DocumentRejected;
        var envelope = IntegrationEventEnvelope.Create(
            eventType,
            correlationId,
            new DocumentCompletedV1(workflow.DocumentId, workflow.SubmissionId, workflow.CreatorId, workflow.Title, comment));
        db.OutboxMessages.Add(ToOutbox(envelope, routingKey, now));
    }

    private static OutboxMessage ToOutbox(
        IntegrationEventEnvelope envelope,
        string routingKey,
        DateTimeOffset now) => new()
    {
        EventId = envelope.EventId,
        EventType = envelope.EventType,
        RoutingKey = routingKey,
        PayloadJson = JsonSerializer.Serialize(envelope),
        CorrelationId = envelope.CorrelationId,
        CreatedAtUtc = now
    };

    private static string GetCorrelationId(HttpContext context)
    {
        var supplied = context.Request.Headers["X-Correlation-Id"].FirstOrDefault();
        return string.IsNullOrWhiteSpace(supplied) || supplied.Length > 128
            ? context.TraceIdentifier
            : supplied.Trim();
    }

    private static bool TryGetUserId(ClaimsPrincipal principal, out Guid userId) =>
        Guid.TryParse(principal.FindFirstValue("sub"), out userId);

    private static WorkflowResponse ToResponse(ApprovalWorkflow workflow) => new(
        workflow.Id,
        workflow.DocumentId,
        workflow.CreatorId,
        workflow.Title,
        workflow.Status.ToString(),
        workflow.CreatedAtUtc,
        workflow.CompletedAtUtc,
        workflow.Steps.OrderBy(step => step.StepOrder)
            .Select(step => new ApprovalStepResponse(
                step.Id,
                step.StepOrder,
                step.ApproverId,
                step.Status.ToString(),
                step.DecidedAtUtc,
                step.Decision is null
                    ? null
                    : new DecisionResponse(
                        step.Decision.Id,
                        step.Decision.StepId,
                        step.Decision.ApproverId,
                        step.Decision.Decision.ToString(),
                        step.Decision.Comment,
                        step.Decision.CreatedAtUtc)))
            .ToArray());
}
