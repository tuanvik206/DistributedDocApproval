namespace Approval.Api.Domain;

public sealed class AuditEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ActorId { get; set; }

    public string Action { get; set; } = string.Empty;

    public Guid WorkflowId { get; set; }

    public Guid DocumentId { get; set; }

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public string CorrelationId { get; set; } = string.Empty;
}
