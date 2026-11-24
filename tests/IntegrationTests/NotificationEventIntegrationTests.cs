extern alias NotificationApi;

using IntegrationContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NotificationDbContext = NotificationApi::Notification.Api.Infrastructure.NotificationDbContext;
using NotificationEventHandler = NotificationApi::Notification.Api.Infrastructure.NotificationEventHandler;
using Testcontainers.PostgreSql;

namespace IntegrationTests;

[CollectionDefinition(nameof(NotificationEventIntegrationCollection), DisableParallelization = true)]
public sealed class NotificationEventIntegrationCollection { }

[Collection(nameof(NotificationEventIntegrationCollection))]
public sealed class NotificationEventIntegrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("NotificationIntegration")
        .WithUsername("notification_test")
        .WithPassword("Integration-Only-936!")
        .Build();
    private string _connectionString = string.Empty;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        _connectionString = _postgres.GetConnectionString();
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync() => await _postgres.DisposeAsync();

    [Fact]
    public async Task Notification_events_are_private_and_idempotent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var creatorId = Guid.NewGuid();
        var approverId = Guid.NewGuid();
        var documentId = Guid.NewGuid();
        var workflowId = Guid.NewGuid();
        await using var processingDb = CreateDbContext();
        var handler = new NotificationEventHandler(processingDb, NullLogger<NotificationEventHandler>.Instance);

        var taskEvent = IntegrationEventEnvelope.Create(
            IntegrationEventTypes.ApprovalTaskAssigned,
            "test-notification-assignment",
            new ApprovalTaskAssignedV1(workflowId, documentId, creatorId, approverId, "Thiết bị phòng lab", 1, 2));
        await handler.HandleAsync(taskEvent, cancellationToken);
        await handler.HandleAsync(taskEvent, cancellationToken);

        var resultEvent = IntegrationEventEnvelope.Create(
            IntegrationEventTypes.DocumentApproved,
            "test-notification-result",
            new DocumentCompletedV1(documentId, Guid.NewGuid(), creatorId, "Thiết bị phòng lab", "Đã duyệt"));
        await handler.HandleAsync(resultEvent, cancellationToken);
        await handler.HandleAsync(resultEvent, cancellationToken);

        await using var verify = CreateDbContext();
        var notifications = await verify.Notifications.AsNoTracking().OrderBy(item => item.Type).ToListAsync(cancellationToken);
        Assert.Equal(2, notifications.Count);
        Assert.Equal(2, await verify.InboxMessages.CountAsync(cancellationToken));

        var assigned = Assert.Single(notifications, item => item.Type == "ApprovalTaskAssigned");
        Assert.Equal(approverId, assigned.UserId);
        Assert.Equal(documentId, assigned.DocumentId);
        Assert.False(assigned.IsRead);
        Assert.Contains("bước 1/2", assigned.Message);

        var completed = Assert.Single(notifications, item => item.Type == "DocumentApproved");
        Assert.Equal(creatorId, completed.UserId);
        Assert.Equal(documentId, completed.DocumentId);
        Assert.False(completed.IsRead);
    }

    private NotificationDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<NotificationDbContext>().UseNpgsql(_connectionString).Options);
}
