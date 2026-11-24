namespace Approval.Api.Domain;

public enum DecisionKind
{
    Approve,
    Reject
}

public sealed class ApprovalDecision
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid StepId { get; set; }

    public ApprovalStep Step { get; set; } = null!;

    public Guid ApproverId { get; set; }

    public DecisionKind Decision { get; set; }

    public string Comment { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
