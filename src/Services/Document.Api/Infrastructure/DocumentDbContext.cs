using Document.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Document.Api.Infrastructure;

public sealed class DocumentDbContext(DbContextOptions<DocumentDbContext> options) : DbContext(options)
{
    public DbSet<DocumentRecord> Documents => Set<DocumentRecord>();

    public DbSet<DocumentReader> DocumentReaders => Set<DocumentReader>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var documents = modelBuilder.Entity<DocumentRecord>();
        documents.ToTable("Documents");
        documents.HasKey(document => document.Id);
        documents.Property(document => document.Title).HasMaxLength(200).IsRequired();
        documents.Property(document => document.Description).HasMaxLength(2000).IsRequired();
        documents.Property(document => document.OriginalFileName).HasMaxLength(255).IsRequired();
        documents.Property(document => document.ObjectKey).HasMaxLength(200).IsRequired();
        documents.Property(document => document.ContentType).HasMaxLength(128).IsRequired();
        documents.Property(document => document.Sha256).HasMaxLength(64).IsFixedLength().IsRequired();
        documents.Property(document => document.Status).HasConversion<string>().HasMaxLength(16).IsRequired();
        documents.Property(document => document.Version).IsConcurrencyToken();
        documents.HasIndex(document => document.CreatorId);
        documents.HasIndex(document => document.SubmissionId).IsUnique();

        var readers = modelBuilder.Entity<DocumentReader>();
        readers.ToTable("DocumentReaders");
        readers.HasKey(reader => new { reader.DocumentId, reader.UserId });
        readers.HasIndex(reader => reader.UserId);
        readers.HasOne(reader => reader.Document)
            .WithMany(document => document.Readers)
            .HasForeignKey(reader => reader.DocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        var outbox = modelBuilder.Entity<OutboxMessage>();
        outbox.ToTable("OutboxMessages");
        outbox.HasKey(message => message.EventId);
        outbox.Property(message => message.EventType).HasMaxLength(160).IsRequired();
        outbox.Property(message => message.RoutingKey).HasMaxLength(160).IsRequired();
        outbox.Property(message => message.PayloadJson).IsRequired();
        outbox.Property(message => message.CorrelationId).HasMaxLength(128).IsRequired();
        outbox.Property(message => message.LastError).HasMaxLength(2000);
        outbox.HasIndex(message => new { message.PublishedAtUtc, message.CreatedAtUtc });

        var inbox = modelBuilder.Entity<InboxMessage>();
        inbox.ToTable("InboxMessages");
        inbox.HasKey(message => message.EventId);
    }
}
