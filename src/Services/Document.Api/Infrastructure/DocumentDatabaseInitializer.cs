using Document.Api.Storage;
using Microsoft.EntityFrameworkCore;

namespace Document.Api.Infrastructure;

public sealed class DocumentDatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    ILogger<DocumentDatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<DocumentDbContext>();
        var objectStore = scope.ServiceProvider.GetRequiredService<IPrivateObjectStore>();

        logger.LogInformation("Applying Document database migrations");
        await db.Database.MigrateAsync(cancellationToken);
        await objectStore.EnsureBucketAsync(cancellationToken);
        logger.LogInformation("Document database and private object bucket are ready");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
