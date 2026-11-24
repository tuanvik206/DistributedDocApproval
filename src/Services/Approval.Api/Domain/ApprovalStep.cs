namespace Approval.Api.Domain;

public enum ApprovalStepStatus
{
    Waiting,
    Active,
    Approved,
    Rejected,
    Skipped
}

public sealed class ApprovalStep
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WorkflowId { get; set; }

    public ApprovalWorkflow Workflow { get; set; } = null!;

    public int StepOrder { get; set; }

    public Guid ApproverId { get; set; }

    public ApprovalStepStatus Status { get; set; }

    public DateTimeOffset? DecidedAtUtc { get; set; }

    public int Version { get; set; } = 1;

    public ApprovalDecision? Decision { get; set; }
}
