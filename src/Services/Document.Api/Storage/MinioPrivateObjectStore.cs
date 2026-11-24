using Minio;
using Minio.DataModel.Args;

namespace Document.Api.Storage;

public sealed class MinioPrivateObjectStore(IMinioClient client, DocumentStorageOptions options) : IPrivateObjectStore
{
    public async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        var exists = await client.BucketExistsAsync(
            new BucketExistsArgs().WithBucket(options.Bucket),
            cancellationToken);
        if (!exists)
        {
            await client.MakeBucketAsync(
                new MakeBucketArgs().WithBucket(options.Bucket),
                cancellationToken);
        }
    }

    public Task<bool> IsBucketReadyAsync(CancellationToken cancellationToken) => client.BucketExistsAsync(
        new BucketExistsArgs().WithBucket(options.Bucket),
        cancellationToken);

    public async Task PutAsync(
        string objectKey,
        Stream content,
        long sizeBytes,
        string contentType,
        CancellationToken cancellationToken)
    {
        var args = new PutObjectArgs()
            .WithBucket(options.Bucket)
            .WithObject(objectKey)
            .WithStreamData(content)
            .WithObjectSize(sizeBytes)
            .WithContentType(contentType);

        await client.PutObjectAsync(args, cancellationToken);
    }

    public async Task<Stream> GetAsync(string objectKey, CancellationToken cancellationToken)
    {
        var content = new MemoryStream();
        try
        {
            var args = new GetObjectArgs()
                .WithBucket(options.Bucket)
                .WithObject(objectKey)
                .WithCallbackStream(stream => stream.CopyTo(content));
            await client.GetObjectAsync(args, cancellationToken);
            content.Position = 0;
            return content;
        }
        catch
        {
            await content.DisposeAsync();
            throw;
        }
    }

    public Task DeleteAsync(string objectKey, CancellationToken cancellationToken) => client.RemoveObjectAsync(
        new RemoveObjectArgs().WithBucket(options.Bucket).WithObject(objectKey),
        cancellationToken);
}
