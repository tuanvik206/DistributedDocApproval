namespace Document.Api.Contracts;

public sealed record UpdateDocumentRequest(string Title, string Description, int ExpectedVersion);

public sealed record SubmitDocumentRequest(IReadOnlyList<Guid> ApproverIdsInOrder, int ExpectedVersion);

public sealed record DocumentResponse(
    Guid Id,
    Guid CreatorId,
    string Title,
    string Description,
    string OriginalFileName,
    string ContentType,
    long SizeBytes,
    string Sha256,
    string Status,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? SubmittedAtUtc,
    DateTimeOffset? CompletedAtUtc,
    int Version);
