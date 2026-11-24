namespace IntegrationContracts.Messaging;

public sealed class RabbitMqOptions
{
    public string Host { get; init; } = "localhost";

    public int Port { get; init; } = 5672;

    public string Username { get; init; } = "guest";

    public string Password { get; init; } = "guest";

    public string VirtualHost { get; init; } = "/";

    public string Exchange { get; init; } = "distributed-documents";

    public string DeadLetterExchange { get; init; } = "distributed-documents.dlx";

    public string QueueName { get; init; } = string.Empty;

    public string[] RoutingKeys { get; init; } = [];
}
