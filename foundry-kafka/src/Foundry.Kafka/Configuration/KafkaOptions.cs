using System.Collections.Generic;

namespace Foundry.Kafka.Configuration;

/// <summary>
/// Represents the configuration options for Kafka.
/// </summary>
public class KafkaOptions
{
    /// <summary>
    /// Gets or sets the bootstrap servers for the Kafka cluster.
    /// </summary>
    public string BootstrapServers { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the client ID for the Kafka producer/consumer.
    /// </summary>
    public string ClientId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the producer options.
    /// </summary>
    public ProducerOptions ProducerOptions { get; set; } = new();

    /// <summary>
    /// Gets or sets the consumer options.
    /// </summary>
    public ConsumerOptions ConsumerOptions { get; set; } = new();
}

/// <summary>
/// Represents the producer-specific configuration options for Kafka.
/// </summary>
public class ProducerOptions
{
    /// <summary>
    /// Gets or sets the linger milliseconds for batching messages.
    /// </summary>
    public int LingerMs { get; set; } = 5;

    /// <summary>
    /// Gets or sets the acknowledgment level for message delivery.
    /// </summary>
    public int Acks { get; set; } = 1;

    /// <summary>
    /// Gets or sets the compression type for messages.
    /// </summary>
    public string CompressionType { get; set; } = "none";
}

/// <summary>
/// Represents the consumer-specific configuration options for Kafka.
/// </summary>
public class ConsumerOptions
{
    /// <summary>
    /// Gets or sets the group ID for the consumer.
    /// </summary>
    public string GroupId { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the auto offset reset policy.
    /// </summary>
    public string AutoOffsetReset { get; set; } = "latest";

    /// <summary>
    /// Gets or sets whether to enable auto commit of offsets.
    /// </summary>
    public bool EnableAutoCommit { get; set; } = true;

    /// <summary>
    /// Gets or sets the session timeout in milliseconds.
    /// </summary>
    public int SessionTimeoutMs { get; set; } = 60000;

    /// <summary>
    /// Gets or sets the mapping of topic names to API endpoint URLs.
    /// </summary>
    public Dictionary<string, string> TopicApiMappings { get; set; } = new();
}
