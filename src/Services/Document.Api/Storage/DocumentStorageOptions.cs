namespace Document.Api.Storage;

public sealed class DocumentStorageOptions
{
    public const string SectionName = "Minio";

    public string Endpoint { get; init; } = string.Empty;

    public string AccessKey { get; init; } = string.Empty;

    public string SecretKey { get; init; } = string.Empty;

    public string Bucket { get; init; } = "documents";

    public bool UseSsl { get; init; }
}
