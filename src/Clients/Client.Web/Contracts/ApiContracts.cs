namespace Client.Web.Contracts;

public sealed record UserResponse(Guid Id, string Username, string FullName, string Email, string Role, bool IsActive, DateTimeOffset CreatedAtUtc);

public sealed record DocumentResponse(Guid Id, Guid CreatorId, string Title, string Description, string OriginalFileName, string ContentType, long SizeBytes, string Sha256, string Status, DateTimeOffset CreatedAtUtc, DateTimeOffset? SubmittedAtUtc, DateTimeOffset? CompletedAtUtc, int Version);

public sealed record TaskResponse(Guid TaskId, Guid WorkflowId, Guid DocumentId, string Title, string Status, int StepOrder, int TotalSteps, DateTimeOffset CreatedAtUtc);

public sealed record DecisionRequest(string Decision, string? Comment);

public sealed record DecisionResponse(Guid Id, Guid StepId, Guid ApproverId, string Decision, string Comment, DateTimeOffset CreatedAtUtc);

public sealed record ApprovalStepResponse(Guid Id, int StepOrder, Guid ApproverId, string Status, DateTimeOffset? DecidedAtUtc, DecisionResponse? Decision);

public sealed record WorkflowResponse(Guid Id, Guid DocumentId, Guid CreatorId, string Title, string Status, DateTimeOffset CreatedAtUtc, DateTimeOffset? CompletedAtUtc, IReadOnlyList<ApprovalStepResponse> Steps);

public sealed record NotificationResponse(Guid Id, Guid DocumentId, string Type, string Message, bool IsRead, DateTimeOffset CreatedAtUtc, DateTimeOffset? ReadAtUtc);

public sealed record CreateUserRequest(string Username, string FullName, string Email, string Password, string Role);

public sealed record ChangeUserStatusRequest(bool IsActive);

public sealed record ChangeUserRoleRequest(string Role);

public sealed record ReadNotificationRequest(bool IsRead);

public sealed record SubmitDocumentRequest(IReadOnlyList<Guid> ApproverIdsInOrder, int ExpectedVersion);

public sealed record UpdateDocumentRequest(string Title, string Description, int ExpectedVersion);
