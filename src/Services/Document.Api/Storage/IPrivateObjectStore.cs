namespace Document.Api.Storage;

public interface IPrivateObjectStore
{
    Task EnsureBucketAsync(CancellationToken cancellationToken);

    Task<bool> IsBucketReadyAsync(CancellationToken cancellationToken);

    Task PutAsync(string objectKey, Stream content, long sizeBytes, string contentType, CancellationToken cancellationToken);

    Task<Stream> GetAsync(string objectKey, CancellationToken cancellationToken);

    Task DeleteAsync(string objectKey, CancellationToken cancellationToken);
}
