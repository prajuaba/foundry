using System;

namespace Foundry.Schema.Compiler
{
    /// <summary>
    /// The topic an entity's or DTO's domain events travel on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This must agree with <c>Foundry.Kafka.Bridge.KafkaOutboxDispatcher.ResolveTopicName</c>, which
    /// is the rule that actually runs. The compiler cannot reference <c>Foundry.Kafka</c> — it has no
    /// project references at all — so the agreement is held by a test in
    /// <c>Foundry.IntegrationTests</c>, which references both and compares the two implementations
    /// directly rather than comparing two copies of a table.
    /// </para>
    /// <para>
    /// The default was written twice here, in <c>PocoGenerator</c> and <c>AsyncApiExporter</c>, and
    /// both lower-cased the whole name where the dispatcher kebab-cases it. A single-word entity
    /// agreed by luck; every multi-word one did not, so <c>PurchaseOrder</c>'s generated consumer
    /// subscribed to <c>purchaseorder-events</c> while the publisher wrote to
    /// <c>purchase-order-events</c>.
    /// </para>
    /// </remarks>
    public static class KafkaTopicNaming
    {
        private const string TopicSuffix = "-events";

        /// <summary>The topic for a target, honouring a declared name and otherwise deriving one.</summary>
        public static string TopicFor(string name, string? declaredTopic)
            => !string.IsNullOrWhiteSpace(declaredTopic) ? declaredTopic!.Trim() : Default(name);

        /// <summary>The name derived from the target's own name, when none is declared.</summary>
        public static string Default(string name) => CamelToKebab(name) + TopicSuffix;

        private static string CamelToKebab(string input)
            => string.IsNullOrEmpty(input)
                ? input
                : System.Text.RegularExpressions.Regex
                    .Replace(input, "([a-z0-9])([A-Z])", "$1-$2")
                    .ToLowerInvariant();
    }
}
