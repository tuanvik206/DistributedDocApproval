using System.Text;
using RabbitMQ.Client;

namespace IntegrationContracts.Messaging;

public interface IIntegrationEventPublisher
{
    Task PublishAsync(string routingKey, string payloadJson, string correlationId, CancellationToken cancellationToken);
}

public sealed class RabbitMqEventPublisher(
    RabbitMqOptions options,
    RabbitMqConnectionProvider connectionProvider) : IIntegrationEventPublisher
{
    public async Task PublishAsync(string routingKey, string payloadJson, string correlationId, CancellationToken cancellationToken)
    {
        var connection = await connectionProvider.GetConnectionAsync(cancellationToken);
        var channelOptions = new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true);
        await using var channel = await connection.CreateChannelAsync(channelOptions, cancellationToken);
        await channel.ExchangeDeclareAsync(
            options.Exchange,
            ExchangeType.Topic,
            durable: true,
            cancellationToken: cancellationToken);

        var properties = new BasicProperties
        {
            ContentType = "application/json",
            ContentEncoding = "utf-8",
            CorrelationId = correlationId,
            Persistent = true
        };
        await channel.BasicPublishAsync(
            exchange: options.Exchange,
            routingKey: routingKey,
            mandatory: true,
            basicProperties: properties,
            body: Encoding.UTF8.GetBytes(payloadJson),
            cancellationToken: cancellationToken);
    }
}
