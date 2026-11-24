using Approval.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Approval.Api.Infrastructure;

public sealed class ApprovalDbContext(DbContextOptions<ApprovalDbContext> options) : DbContext(options)
{
    public DbSet<ApprovalWorkflow> Workflows => Set<ApprovalWorkflow>();

    public DbSet<ApprovalStep> Steps => Set<ApprovalStep>();

    public DbSet<ApprovalDecision> Decisions => Set<ApprovalDecision>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var workflows = modelBuilder.Entity<ApprovalWorkflow>();
        workflows.ToTable("ApprovalWorkflows");
        workflows.HasKey(workflow => workflow.Id);
        workflows.Property(workflow => workflow.Title).HasMaxLength(200).IsRequired();
        workflows.Property(workflow => workflow.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        workflows.HasIndex(workflow => workflow.DocumentId).IsUnique();
        workflows.HasIndex(workflow => workflow.SubmissionId).IsUnique();
        workflows.HasMany(workflow => workflow.Steps)
            .WithOne(step => step.Workflow)
            .HasForeignKey(step => step.WorkflowId)
            .OnDelete(DeleteBehavior.Cascade);

        var steps = modelBuilder.Entity<ApprovalStep>();
        steps.ToTable("ApprovalSteps");
        steps.HasKey(step => step.Id);
        steps.Property(step => step.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        steps.Property(step => step.Version).IsConcurrencyToken();
        steps.HasIndex(step => new { step.WorkflowId, step.StepOrder }).IsUnique();
        steps.HasIndex(step => new { step.ApproverId, step.Status });
        steps.HasOne(step => step.Decision)
            .WithOne(decision => decision.Step)
            .HasForeignKey<ApprovalDecision>(decision => decision.StepId)
            .OnDelete(DeleteBehavior.Cascade);

        var decisions = modelBuilder.Entity<ApprovalDecision>();
        decisions.ToTable("ApprovalDecisions");
        decisions.HasKey(decision => decision.Id);
        decisions.Property(decision => decision.Decision).HasConversion<string>().HasMaxLength(16).IsRequired();
        decisions.Property(decision => decision.Comment).HasMaxLength(2000).IsRequired();
        decisions.HasIndex(decision => decision.StepId).IsUnique();

        var auditEntries = modelBuilder.Entity<AuditEntry>();
        auditEntries.ToTable("AuditEntries");
        auditEntries.HasKey(entry => entry.Id);
        auditEntries.Property(entry => entry.Action).HasMaxLength(80).IsRequired();
        auditEntries.Property(entry => entry.CorrelationId).HasMaxLength(128).IsRequired();
        auditEntries.HasIndex(entry => new { entry.WorkflowId, entry.CreatedAtUtc });

        var inbox = modelBuilder.Entity<InboxMessage>();
        inbox.ToTable("InboxMessages");
        inbox.HasKey(message => message.EventId);

        var outbox = modelBuilder.Entity<OutboxMessage>();
        outbox.ToTable("OutboxMessages");
        outbox.HasKey(message => message.EventId);
        outbox.Property(message => message.EventType).HasMaxLength(160).IsRequired();
        outbox.Property(message => message.RoutingKey).HasMaxLength(160).IsRequired();
        outbox.Property(message => message.PayloadJson).IsRequired();
        outbox.Property(message => message.CorrelationId).HasMaxLength(128).IsRequired();
        outbox.Property(message => message.LastError).HasMaxLength(2000);
        outbox.HasIndex(message => new { message.PublishedAtUtc, message.CreatedAtUtc });
    }
}
