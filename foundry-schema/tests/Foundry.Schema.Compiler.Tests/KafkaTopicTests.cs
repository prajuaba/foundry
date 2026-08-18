using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// What the compiler emits for an entity that publishes domain events.
/// </summary>
/// <remarks>
/// A declared <c>kafkaTopic</c> used to reach exactly one place — the generated consumer's
/// subscription — and nothing carried it to the publisher, which derived its own name from the event
/// type. Declaring a topic produced a consumer listening where nothing was written. The agreement
/// between this rule and the dispatcher's is asserted in <c>Foundry.IntegrationTests</c>, which is
/// the only project that can reference both.
/// </remarks>
public class KafkaTopicTests
{
    private static SchemaModel SchemaWith(string entityName, string? topic, bool outbox = true) => new()
    {
        Namespace = "Test.Domain",
        Entities =
        [
            new Entity
            {
                Name = entityName,
                KafkaOutboxEnabled = outbox,
                KafkaTopic = topic,
                ApiEnabledMethods = ["GET"],
                Properties =
                [
                    new Property { Name = "Id", Type = "ObjectId", IsKey = true },
                    new Property { Name = "Reference", Type = "string" }
                ]
            }
        ]
    };

    private static string EntityCode(SchemaModel schema, string entityName)
        => PocoGenerator.Generate(schema)[$"Entities/{entityName}"];

    [Fact]
    public void ADeclaredTopicIsCarriedOntoTheEntity()
    {
        // The attribute is the only route a declaration has to the publishing side.
        var code = EntityCode(SchemaWith("Order", "orders.v2"), "Order");

        Assert.Contains("[KafkaTopic(\"orders.v2\")]", code);
        Assert.Contains("using Foundry.Core.Attributes;", code);
    }

    [Fact]
    public void NoAttributeIsEmittedWhenNoTopicIsDeclared()
    {
        // Absent means "the dispatcher's default applies". Emitting a derived name here would put a
        // second copy of the naming rule in the generated code, which is how this diverged before.
        var code = EntityCode(SchemaWith("Order", null), "Order");

        Assert.DoesNotContain("KafkaTopic", code);
    }

    [Fact]
    public void ADeclaredTopicContainingAQuoteCannotEscapeTheAttribute()
    {
        // Same class as the schema-to-code injection this compiler has had once.
        var code = EntityCode(SchemaWith("Order", "orders\", Evil = \"x"), "Order");

        Assert.DoesNotContain("Evil = \"x\")]", code);
        Assert.Contains("\\\"", code);
    }

    [Theory]
    [InlineData("Order", "order-events")]
    [InlineData("PurchaseOrder", "purchase-order-events")]
    [InlineData("CustomerAccountNote", "customer-account-note-events")]
    public void TheGeneratedConsumerSubscribesToTheDerivedTopic(string entityName, string expected)
    {
        // This lower-cased the whole name, so a multi-word entity's consumer subscribed to a topic
        // with no publisher — the same defect as the declared-topic one, reached without declaring
        // anything at all.
        var consumer = PocoGenerator.Generate(SchemaWith(entityName, null))[$"Kafka/{entityName}KafkaConsumer"];

        Assert.Contains($"public string Topic => \"{expected}\";", consumer);
    }

    [Fact]
    public void TheGeneratedConsumerSubscribesToADeclaredTopic()
    {
        var consumer = PocoGenerator.Generate(SchemaWith("Order", "orders.v2"))["Kafka/OrderKafkaConsumer"];

        Assert.Contains("public string Topic => \"orders.v2\";", consumer);
    }

    [Fact]
    public void AnEntityThatEnablesTheOutboxAndNamesNoTopicStillGetsKafkaOutbox()
    {
        var code = EntityCode(SchemaWith("Order", null), "Order");

        Assert.Contains("[KafkaOutbox]", code);
        Assert.DoesNotContain("KafkaTopic", code);
        Assert.Contains("using Foundry.Core.Attributes;", code);
    }

    [Fact]
    public void AnEntityThatNamesATopicButDoesNotEnableTheOutboxGetsKafkaTopicButNotKafkaOutbox()
    {
        var code = EntityCode(SchemaWith("Order", "orders.v2", outbox: false), "Order");

        Assert.Contains("[KafkaTopic(\"orders.v2\")]", code);
        Assert.DoesNotContain("[KafkaOutbox]", code);
    }

    [Fact]
    public void AnEntityThatNeitherEnablesTheOutboxNorNamesATopicGetsNeitherAttribute()
    {
        var code = EntityCode(SchemaWith("Order", null, outbox: false), "Order");

        Assert.DoesNotContain("[KafkaOutbox]", code);
        Assert.DoesNotContain("[KafkaTopic", code);
    }
}
