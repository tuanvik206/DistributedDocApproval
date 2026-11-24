using System.Text;
using System.Text.Json;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace IntegrationContracts.Messaging;

public interface IIntegrationEventHandler
{
    Task HandleAsync(IntegrationEventEnvelope envelope, CancellationToken cancellationToken);
}

public sealed class RabbitMqConsumerWorker(
    RabbitMqOptions options,
    RabbitMqConnectionProvider connectionProvider,
    IServiceScopeFactory scopeFactory,
    ILogger<RabbitMqConsumerWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var connection = await connectionProvider.GetConnectionAsync(stoppingToken);
                await using var channel = await connection.CreateChannelAsync(cancellationToken: stoppingToken);
                await DeclareTopologyAsync(channel, stoppingToken);
                await channel.BasicQosAsync(0, 1, global: false, cancellationToken: stoppingToken);

                var channelShutdown = new TaskCompletionSource<ShutdownEventArgs>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                channel.ChannelShutdownAsync += (_, shutdown) =>
                {
                    channelShutdown.TrySetResult(shutdown);
                    return Task.CompletedTask;
                };

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += async (_, delivery) =>
                {
                    // RabbitMQ.Client 7 reuses the delivery buffer after this callback returns.
                    var body = delivery.Body.ToArray();
                    IntegrationEventEnvelope envelope;
                    try
                    {
                        envelope = JsonSerializer.Deserialize<IntegrationEventEnvelope>(body)
                            ?? throw new JsonException("The event envelope was empty.");
                    }
                    catch (JsonException exception)
                    {
                        logger.LogError(exception, "Discarding malformed message from queue {QueueName}", options.QueueName);
                        await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, cancellationToken: stoppingToken);
                        return;
                    }

                    for (var attempt = 1; attempt <= 5; attempt++)
                    {
                        try
                        {
                            using var scope = scopeFactory.CreateScope();
                            var handler = scope.ServiceProvider.GetRequiredService<IIntegrationEventHandler>();
                            await handler.HandleAsync(envelope, stoppingToken);
                            await channel.BasicAckAsync(delivery.DeliveryTag, multiple: false, cancellationToken: stoppingToken);
                            logger.LogInformation(
                                "Consumed event {EventId} ({EventType}) from {QueueName}; correlation {CorrelationId}",
                                envelope.EventId,
                                envelope.EventType,
                                options.QueueName,
                                envelope.CorrelationId);
                            return;
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                        {
                            return;
                        }
                        catch (JsonException exception)
                        {
                            logger.LogError(exception, "Event {EventId} payload is invalid; moving it to the dead-letter queue", envelope.EventId);
                            await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: false, cancellationToken: stoppingToken);
                            return;
                        }
                        catch (Exception exception)
                        {
                            logger.LogWarning(exception, "Event {EventId} processing attempt {Attempt} failed", envelope.EventId, attempt);
                            if (attempt < 5)
                            {
                                await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt - 1)), stoppingToken);
                            }
                        }
                    }

                    // Leave transient failures available for recovery. The delay prevents a tight requeue loop.
                    await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
                    await channel.BasicNackAsync(delivery.DeliveryTag, multiple: false, requeue: true, cancellationToken: stoppingToken);
                };

                await channel.BasicConsumeAsync(options.QueueName, autoAck: false, consumer, cancellationToken: stoppingToken);
                logger.LogInformation("Consuming RabbitMQ queue {QueueName}", options.QueueName);
                var shutdownReason = await channelShutdown.Task.WaitAsync(stoppingToken);
                logger.LogWarning(
                    "RabbitMQ consumer channel for {QueueName} closed ({Reason}); recreating the consumer",
                    options.QueueName,
                    shutdownReason);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "RabbitMQ consumer {QueueName} unavailable; retrying in 5s", options.QueueName);
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }

    private async Task DeclareTopologyAsync(IChannel channel, CancellationToken cancellationToken)
    {
        await channel.ExchangeDeclareAsync(options.Exchange, ExchangeType.Topic, durable: true, cancellationToken: cancellationToken);
        await channel.ExchangeDeclareAsync(options.DeadLetterExchange, ExchangeType.Direct, durable: true, cancellationToken: cancellationToken);
        var deadQueue = $"{options.QueueName}.dead";
        await channel.QueueDeclareAsync(deadQueue, durable: true, exclusive: false, autoDelete: false, cancellationToken: cancellationToken);
        await channel.QueueBindAsync(deadQueue, options.DeadLetterExchange, options.QueueName, cancellationToken: cancellationToken);

        var arguments = new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = options.DeadLetterExchange,
            ["x-dead-letter-routing-key"] = options.QueueName
        };
        await channel.QueueDeclareAsync(
            options.QueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: arguments,
            cancellationToken: cancellationToken);
        foreach (var routingKey in options.RoutingKeys)
        {
            await channel.QueueBindAsync(options.QueueName, options.Exchange, routingKey, cancellationToken: cancellationToken);
        }
    }
}

public static class RabbitMqServiceCollectionExtensions
{
    public static RabbitMqOptions AddRabbitMqConnection(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("RabbitMq").Get<RabbitMqOptions>()
            ?? throw new InvalidOperationException("RabbitMq configuration is required.");
        if (string.IsNullOrWhiteSpace(options.Host)
            || string.IsNullOrWhiteSpace(options.Username)
            || string.IsNullOrWhiteSpace(options.Password)
            || string.IsNullOrWhiteSpace(options.Exchange))
        {
            throw new InvalidOperationException("RabbitMq host, credentials, and exchange are required.");
        }

        services.AddSingleton(options);
        services.AddSingleton<RabbitMqConnectionProvider>();
        services.AddSingleton<IIntegrationEventPublisher, RabbitMqEventPublisher>();
        return options;
    }

    public static void AddRabbitMqConsumer(this IServiceCollection services) =>
        services.AddHostedService<RabbitMqConsumerWorker>();
}
