using Microsoft.EntityFrameworkCore;

namespace Notification.Api.Infrastructure;

public sealed class NotificationDatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    ILogger<NotificationDatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<NotificationDbContext>();
        logger.LogInformation("Applying Notification database migrations");
        await db.Database.MigrateAsync(cancellationToken);
        logger.LogInformation("Notification database is ready");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
