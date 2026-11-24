namespace Document.Api.Domain;

public sealed class DocumentReader
{
    public Guid DocumentId { get; set; }

    public Guid UserId { get; set; }

    public DocumentRecord Document { get; set; } = null!;
}
