using System.Linq;
using System.Text.Json;
using Foundry.Schema.Compiler;
using Xunit;

namespace Foundry.Schema.Compiler.Tests;

/// <summary>
/// Studio canvas documents convert to normative IR.
/// </summary>
/// <remarks>
/// <para>
/// FDY1010 fires on a canvas document and its hint says to run <c>foundry migrate</c>. The command
/// did not exist, so the one diagnostic that explains the difference between the two formats ended
/// in an instruction that printed the help banner. The VS Code extension's "New Schema" command
/// emitted precisely the document that trips it, so a user reached that dead end without doing
/// anything unusual.
/// </para>
/// <para>
/// Two canvas shapes are covered because two have shipped: Studio nests the entity under
/// <c>data.entity</c>, and older Studio versions — and the extension — put its fields directly on
/// <c>data</c>. A migrator that reads only the current shape cannot migrate the documents that
/// need it.
/// </para>
/// </remarks>
public class CanvasMigratorTests
{
    /// <summary>The shape the VS Code extension writes: fields directly on <c>data</c>, PascalCase.</summary>
    private const string LegacyCanvas = """
    {
      "namespace": "MyDomain",
      "nodes": [
        {
          "id": "node-1",
          "type": "classNode",
          "position": { "x": 250, "y": 150 },
          "data": {
            "Name": "User",
            "BaseClass": "",
            "SoftDelete": true,
            "Properties": [
              { "Name": "Id", "Type": "ObjectId", "IsKey": true, "Attributes": [] },
              { "Name": "Email", "Type": "string", "IsKey": false, "Attributes": ["Unique", "Required"] }
            ],
            "Indexes": []
          }
        }
      ],
      "edges": [], "customEndpoints": [], "dtos": [], "workflows": []
    }
    """;

    /// <summary>The shape Studio writes today: the entity nested under <c>data.entity</c>, camelCase.</summary>
    private const string CurrentCanvas = """
    {
      "namespace": "Sales.Domain",
      "nodes": [
        {
          "id": "n1",
          "type": "classNode",
          "position": { "x": 0, "y": 0 },
          "data": {
            "entity": {
              "name": "Order",
              "softDelete": true,
              "enableGraphQL": true,
              "apiEnabledMethods": ["GET", "POST"],
              "apiRoles": { "POST": ["Admin"] },
              "properties": [
                { "name": "Id", "type": "ObjectId", "isKey": true },
                { "name": "Total", "type": "decimal" }
              ]
            }
          }
        },
        {
          "id": "n2",
          "type": "enumNode",
          "position": { "x": 400, "y": 0 },
          "data": { "enum": { "name": "OrderStatus", "values": ["Pending", "Shipped"] } }
        }
      ],
      "edges": []
    }
    """;

    private static SchemaModel Parse(string irJson)
        => JsonSerializer.Deserialize<SchemaModel>(
            irJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

    // ── Detection ───────────────────────────────────────────────────────────

    [Fact]
    public void ACanvasDocumentIsRecognised()
    {
        Assert.True(CanvasMigrator.IsCanvasDocument(LegacyCanvas));
        Assert.True(CanvasMigrator.IsCanvasDocument(CurrentCanvas));
    }

    [Fact]
    public void AnIrDocumentIsNotACanvasDocument()
    {
        Assert.False(CanvasMigrator.IsCanvasDocument("""
        { "namespace": "X", "entities": [ { "name": "A", "properties": [] } ] }
        """));
    }

    [Fact]
    public void MalformedJsonIsNotACanvasDocument()
    {
        // Reported by the caller as a read failure rather than crashing the detection.
        Assert.False(CanvasMigrator.IsCanvasDocument("{ not json"));
    }

    // ── Both shapes ─────────────────────────────────────────────────────────

    [Fact]
    public void TheLegacyShapeMigrates()
    {
        var model = CanvasMigrator.Migrate(LegacyCanvas);

        Assert.Equal("MyDomain", model.Namespace);
        var entity = Assert.Single(model.Entities);
        Assert.Equal("User", entity.Name);
        Assert.True(entity.SoftDelete);
        Assert.Equal(["Id", "Email"], entity.Properties.Select(p => p.Name));
        Assert.True(entity.Properties[0].IsKey);
        Assert.Contains("Unique", entity.Properties[1].Attributes);
    }

    [Fact]
    public void TheCurrentShapeMigrates()
    {
        var model = CanvasMigrator.Migrate(CurrentCanvas);

        var entity = Assert.Single(model.Entities);
        Assert.Equal("Order", entity.Name);
        Assert.True(entity.GraphQlEnabled);
        Assert.Equal(["GET", "POST"], entity.ApiEnabledMethods);
        Assert.Equal(["Admin"], entity.ApiRoles["POST"]);
    }

    [Fact]
    public void AnEnumNodeBecomesAnEnum()
    {
        var model = CanvasMigrator.Migrate(CurrentCanvas);

        var declared = Assert.Single(model.Enums);
        Assert.Equal("OrderStatus", declared.Name);
        Assert.Equal(["Pending", "Shipped"], declared.Values);
    }

    [Fact]
    public void ANodeWithNoNameIsDropped()
    {
        // A half-drawn diagram is normal. An entity with no name cannot be emitted and would fail
        // validation with a message about the IR rather than about the node the user left blank.
        var model = CanvasMigrator.Migrate("""
        { "namespace": "X", "nodes": [ { "type": "classNode", "data": { "entity": { "name": "" } } } ] }
        """);

        Assert.Empty(model.Entities);
    }

    [Fact]
    public void TopLevelSectionsAreCarriedAcross()
    {
        // Only entities and enums were ever drawn on the canvas; the rest sits at the top level in
        // IR form already, so it is carried rather than re-derived.
        var model = CanvasMigrator.Migrate("""
        {
          "namespace": "X",
          "nodes": [],
          "dtos": [ { "name": "OrderSummary", "properties": [] } ],
          "customEndpoints": [ { "route": "/api/v1/x", "method": "POST", "requestType": "XCommand",
                                 "targetEntity": "Order", "operationType": "Insert" } ],
          "workflows": [ { "id": "w1", "name": "W", "entity": "Order", "states": [], "transitions": [] } ],
          "connectors": [ { "name": "Pay", "type": "REST", "baseUrl": "https://x" } ]
        }
        """);

        Assert.Single(model.Dtos);
        Assert.Single(model.CustomEndpoints);
        Assert.Single(model.Workflows);
        Assert.Single(model.Connectors);
    }

    [Fact]
    public void ADocumentWithNoNodesIsRefused()
    {
        var error = Assert.Throws<System.InvalidOperationException>(
            () => CanvasMigrator.Migrate("""{ "namespace": "X", "entities": [] }"""));

        Assert.Contains("not a Studio canvas document", error.Message);
    }

    // ── The emitted document ────────────────────────────────────────────────

    [Fact]
    public void TheOutputValidates()
    {
        // The whole point. A migration whose result the compiler still rejects has migrated nothing.
        var irJson = CanvasMigrator.MigrateToJson(LegacyCanvas);

        var bag = new DiagnosticBag();
        SchemaValidator.ValidateRawDocument(irJson, bag);
        bag.AddRange(SchemaValidator.Validate(Parse(irJson)).Items);

        Assert.False(bag.HasErrors, bag.Render());
    }

    [Fact]
    public void TheOutputUsesTheNormativeFieldNames()
    {
        // camelCase, with [JsonPropertyName] winning where one is declared -- the same rule
        // IrSchemaGenerator applies when it publishes the JSON Schema. PascalCase would deserialise
        // fine and fail any third-party check against the published schema.
        var irJson = CanvasMigrator.MigrateToJson(CurrentCanvas);

        Assert.Contains("\"namespace\"", irJson);
        Assert.Contains("\"apiEnabledMethods\"", irJson);
        Assert.Contains("\"enableGraphQL\"", irJson);
        Assert.DoesNotContain("\"Namespace\"", irJson);
        Assert.DoesNotContain("\"ApiEnabledMethods\"", irJson);
    }

    [Fact]
    public void DefaultsAreTrimmedFromTheOutput()
    {
        // The migrated file is what its author edits next. Serialised in full, a two-entity domain
        // is two hundred lines of false and [] with the domain buried in it.
        var irJson = CanvasMigrator.MigrateToJson(LegacyCanvas);

        Assert.DoesNotContain("\"auditable\"", irJson);
        Assert.DoesNotContain("\"partitioned\"", irJson);
        Assert.DoesNotContain("\"archiveThresholdYears\"", irJson);
        Assert.DoesNotContain("\"baseClass\"", irJson);
        Assert.DoesNotContain("[]", irJson);
    }

    [Fact]
    public void WhatWasDeclaredSurvivesTheTrim()
    {
        // The control for the test above: trimming defaults must not trim the domain.
        var irJson = CanvasMigrator.MigrateToJson(LegacyCanvas);

        Assert.Contains("\"softDelete\": true", irJson);
        Assert.Contains("\"isKey\": true", irJson);
        Assert.Contains("\"Unique\"", irJson);

        var model = Parse(irJson);
        Assert.Equal("User", Assert.Single(model.Entities).Name);
    }

    [Fact]
    public void AnArchiveThresholdSurvivesOnAPartitionedEntity()
    {
        // The one non-zero default that gets dropped, and only where nothing reads it. On an entity
        // that is partitioned it is load-bearing, and dropping it would silently change the
        // threshold to the model's default.
        var irJson = CanvasMigrator.MigrateToJson("""
        {
          "namespace": "X",
          "nodes": [ { "type": "classNode", "data": { "entity": {
            "name": "Ledger", "partitioned": true, "archiveThresholdYears": 7,
            "properties": [ { "name": "Id", "type": "ObjectId", "isKey": true } ] } } } ]
        }
        """);

        Assert.Contains("\"archiveThresholdYears\": 7", irJson);
    }
}
