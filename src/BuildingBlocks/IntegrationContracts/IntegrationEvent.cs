using System.Text.Json;

namespace IntegrationContracts;

public static class IntegrationEventTypes
{
    public const string DocumentSubmitted = "DocumentSubmitted.v1";
    public const string ApprovalTaskAssigned = "ApprovalTaskAssigned.v1";
    public const string DocumentApproved = "DocumentApproved.v1";
    public const string DocumentRejected = "DocumentRejected.v1";
}

public static class IntegrationRoutingKeys
{
    public const string DocumentSubmitted = "document.submitted.v1";
    public const string ApprovalTaskAssigned = "approval.task-assigned.v1";
    public const string DocumentApproved = "document.approved.v1";
    public const string DocumentRejected = "document.rejected.v1";
}

public sealed record IntegrationEventEnvelope(
    Guid EventId,
    string EventType,
    DateTimeOffset OccurredAtUtc,
    string CorrelationId,
    JsonElement Payload)
{
    public static IntegrationEventEnvelope Create<T>(string eventType, string correlationId, T payload) => new(
        Guid.NewGuid(),
        eventType,
        DateTimeOffset.UtcNow,
        correlationId,
        JsonSerializer.SerializeToElement(payload));

    public T? ReadPayload<T>() => Payload.Deserialize<T>();
}

public sealed record DocumentSubmittedV1(
    Guid DocumentId,
    Guid SubmissionId,
    Guid CreatorId,
    string Title,
    IReadOnlyList<Guid> ApproverIdsInOrder);

public sealed record ApprovalTaskAssignedV1(
    Guid WorkflowId,
    Guid DocumentId,
    Guid CreatorId,
    Guid ApproverId,
    string Title,
    int StepOrder,
    int TotalSteps);

public sealed record DocumentCompletedV1(
    Guid DocumentId,
    Guid SubmissionId,
    Guid CreatorId,
    string Title,
    string Comment);
