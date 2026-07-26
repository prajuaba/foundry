using System.Text.Json;
using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// Guards the AI skill bundle: the IR schema must stay compilable into a sampling grammar,
/// and every shipped example must stay valid.
/// </summary>
public class AiSpecTests
{
    /// <summary>
    /// Ollama compiles the supplied JSON Schema into a GBNF grammar, and rejects the whole
    /// request if any single pattern is unconvertible. Establishing this by test rather than by
    /// review matters because the attribute pattern is assembled from <see cref="Vocabulary"/>,
    /// so adding a parameterised attribute can silently reintroduce an unsupported construct.
    /// </summary>
    [Fact]
    public void IrSchema_ContainsNoGrammarUnsafePatterns()
    {
        var problems = IrSchemaGenerator.FindGrammarUnsafePatterns();

        Assert.True(
            problems.Count == 0,
            "The IR schema contains patterns Ollama cannot compile into a grammar:\n"
            + string.Join("\n", problems));
    }

    /// <summary>
    /// The attribute pattern must accept every form the compiler honours and reject the
    /// injection payloads the validator exists to stop.
    /// </summary>
    [Theory]
    [InlineData("Required", true)]
    [InlineData("Indexed", true)]
    [InlineData("Unique", true)]
    [InlineData("MaskEmail", true)]
    [InlineData("MinLength(3)", true)]
    [InlineData("MaxLength(120)", true)]
    [InlineData("Range(0, 100)", true)]
    [InlineData("Range(-1.5, 2.75)", true)]
    [InlineData("Regex(\"^[A-Z]+$\")", true)]
    [InlineData("NotARealAttribute", false)]
    [InlineData("MaxLength(5)] public class X {} [Obsolete(", false)]
    [InlineData("Required; DROP TABLE", false)]
    [InlineData("MinLength(abc)", false)]
    public void AttributePattern_MatchesExactlyTheSupportedVocabulary(string attribute, bool expected)
    {
        var pattern = IrSchemaGenerator.BuildAttributePattern();
        var actual = System.Text.RegularExpressions.Regex.IsMatch(attribute, pattern);
        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// A shipped example that the compiler would reject teaches the model to produce invalid IR,
    /// so the bundle must never contain one.
    /// </summary>
    [Fact]
    public void GoldenExamples_AllValidateWithoutErrors()
    {
        Assert.NotEmpty(AiSpecBundle.Examples);

        foreach (var (name, json) in AiSpecBundle.Examples)
        {
            var schema = JsonSerializer.Deserialize<SchemaModel>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var bag = SchemaValidator.Validate(schema);

            // Warnings count here, unlike elsewhere. These examples are teaching material: the
            // model is shown them and copies their shape. 04-workflow.json previously declared a
            // ClaimStage enum nothing used -- demonstrating the exact mistake the eval suite then
            // caught the model making.
            Assert.True(
                bag.Items.Count == 0,
                $"Golden example '{name}' must be exemplary, not merely valid:\n{bag.Render()}");
        }
    }

    /// <summary>
    /// The schema is emitted as the model's contract, so its shape is load-bearing: closed
    /// objects stop a model inventing fields the compiler would silently discard.
    /// </summary>
    [Fact]
    public void IrSchema_ClosesObjectsAndRequiresCoreFields()
    {
        using var doc = JsonDocument.Parse(IrSchemaGenerator.Generate());
        var root = doc.RootElement;

        Assert.False(root.GetProperty("additionalProperties").GetBoolean());

        var required = root.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("namespace", required);
        Assert.Contains("entities", required);

        var entity = root.GetProperty("$defs").GetProperty("Entity");
        Assert.False(entity.GetProperty("additionalProperties").GetBoolean());
    }

    /// <summary>
    /// The schema must describe the wire format the compiler actually deserialises. Several
    /// entity flags carry [JsonPropertyName] overrides, and a schema that advertised the C#
    /// names would send the model straight into fields the compiler ignores.
    /// </summary>
    [Fact]
    public void IrSchema_UsesSerializedPropertyNames()
    {
        using var doc = JsonDocument.Parse(IrSchemaGenerator.Generate());
        var entityProps = doc.RootElement.GetProperty("$defs").GetProperty("Entity").GetProperty("properties");

        Assert.True(entityProps.TryGetProperty("enableKafkaOutbox", out _));
        Assert.True(entityProps.TryGetProperty("fileIOAllowedExtensions", out _));
        Assert.True(entityProps.TryGetProperty("multiTenant", out _));

        // The C# spellings must not leak through.
        Assert.False(entityProps.TryGetProperty("kafkaOutboxEnabled", out _));
        Assert.False(entityProps.TryGetProperty("fileIoEnabled", out _));
    }
}
