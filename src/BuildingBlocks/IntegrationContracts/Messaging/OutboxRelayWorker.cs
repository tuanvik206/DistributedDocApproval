namespace IntegrationContracts.Messaging;

public sealed record PendingOutboxMessage(Guid EventId, string RoutingKey, string PayloadJson, string CorrelationId, int AttemptCount);

public interface IOutboxMessageStore
{
    Task<IReadOnlyList<PendingOutboxMessage>> GetPendingAsync(int limit, CancellationToken cancellationToken);

    Task MarkPublishedAsync(Guid eventId, CancellationToken cancellationToken);

    Task RecordFailureAsync(Guid eventId, string error, CancellationToken cancellationToken);
}

public sealed class OutboxRelayWorker(
    IServiceScopeFactory scopeFactory,
    IIntegrationEventPublisher publisher,
    ILogger<OutboxRelayWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var store = scope.ServiceProvider.GetRequiredService<IOutboxMessageStore>();
                var messages = await store.GetPendingAsync(20, stoppingToken);
                if (messages.Count == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                    continue;
                }

                foreach (var message in messages)
                {
                    try
                    {
                        await publisher.PublishAsync(message.RoutingKey, message.PayloadJson, message.CorrelationId, stoppingToken);
                        await store.MarkPublishedAsync(message.EventId, stoppingToken);
                        logger.LogInformation("Published outbox event {EventId} with route {RoutingKey}", message.EventId, message.RoutingKey);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        await store.RecordFailureAsync(message.EventId, exception.Message, stoppingToken);
                        var delaySeconds = Math.Min(30, Math.Pow(2, Math.Min(message.AttemptCount, 5)));
                        logger.LogWarning(exception, "Outbox event {EventId} publish failed; retrying after {DelaySeconds}s", message.EventId, delaySeconds);
                        await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Outbox relay iteration failed; retrying shortly");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}
