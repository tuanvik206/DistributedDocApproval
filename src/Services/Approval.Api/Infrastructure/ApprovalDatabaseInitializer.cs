using Microsoft.EntityFrameworkCore;

namespace Approval.Api.Infrastructure;

public sealed class ApprovalDatabaseInitializer(
    IServiceScopeFactory scopeFactory,
    ILogger<ApprovalDatabaseInitializer> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApprovalDbContext>();
        logger.LogInformation("Applying Approval database migrations");
        await db.Database.MigrateAsync(cancellationToken);
        logger.LogInformation("Approval database is ready");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
