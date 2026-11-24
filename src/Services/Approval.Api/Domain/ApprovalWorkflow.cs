namespace Approval.Api.Domain;

public enum WorkflowStatus
{
    Pending,
    Approved,
    Rejected
}

public sealed class ApprovalWorkflow
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid DocumentId { get; set; }

    public Guid SubmissionId { get; set; }

    public Guid CreatorId { get; set; }

    public string Title { get; set; } = string.Empty;

    public WorkflowStatus Status { get; set; } = WorkflowStatus.Pending;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAtUtc { get; set; }

    public List<ApprovalStep> Steps { get; set; } = [];
}
