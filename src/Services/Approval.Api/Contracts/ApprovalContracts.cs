namespace Approval.Api.Contracts;

public sealed record CreateDecisionRequest(string Decision, string? Comment);

public sealed record TaskResponse(
    Guid TaskId,
    Guid WorkflowId,
    Guid DocumentId,
    string Title,
    string Status,
    int StepOrder,
    int TotalSteps,
    DateTimeOffset CreatedAtUtc);

public sealed record DecisionResponse(
    Guid Id,
    Guid StepId,
    Guid ApproverId,
    string Decision,
    string Comment,
    DateTimeOffset CreatedAtUtc);

public sealed record ApprovalStepResponse(
    Guid Id,
    int StepOrder,
    Guid ApproverId,
    string Status,
    DateTimeOffset? DecidedAtUtc,
    DecisionResponse? Decision);

public sealed record WorkflowResponse(
    Guid Id,
    Guid DocumentId,
    Guid CreatorId,
    string Title,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    IReadOnlyList<ApprovalStepResponse> Steps);
