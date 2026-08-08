using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Foundry.Schema.Compiler
{
    /// <summary>
    /// Generates the Foundry AI skill bundle: everything a local model needs to author valid IR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every file in the bundle is derived from compiler source — the IR schema from
    /// <see cref="SchemaModel"/>, the vocabulary from <see cref="Vocabulary"/>, the diagnostics
    /// from <see cref="DiagnosticCatalog"/>. Nothing here is hand-maintained prose about how the
    /// compiler behaves, because that is precisely what rots: the previous integration carried a
    /// hardcoded 7-rule prompt that had already fallen out of step with the compiler it described.
    /// </para>
    /// <para>
    /// The golden examples are asserted by test to validate cleanly, so the bundle can never ship
    /// an example the compiler would reject.
    /// </para>
    /// </remarks>
    public static class AiSpecBundle
    {
        /// <summary>
        /// Golden IR examples, keyed by file name. Each must pass <see cref="SchemaValidator"/>
        /// with zero errors.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> Examples = new Dictionary<string, string>
        {
            ["01-minimal.json"] = """
{
  "namespace": "Acme.Catalog",
  "entities": [
    {
      "name": "Product",
      "softDelete": true,
      "properties": [
        { "name": "Id", "type": "ObjectId", "isKey": true },
        { "name": "Sku", "type": "string", "attributes": ["Required", "Unique"] },
        { "name": "Name", "type": "string", "attributes": ["Required"] },
        { "name": "UnitPrice", "type": "decimal" }
      ]
    }
  ]
}
""",

            ["02-enum-and-index.json"] = """
{
  "namespace": "Acme.Sales",
  "enums": [
    { "name": "OrderStatus", "values": ["Pending", "Paid", "Shipped", "Cancelled"] }
  ],
  "entities": [
    {
      "name": "Order",
      "softDelete": true,
      "auditable": true,
      "properties": [
        { "name": "Id", "type": "ObjectId", "isKey": true },
        { "name": "OrderNumber", "type": "string", "attributes": ["Required", "Unique"] },
        { "name": "Status", "type": "OrderStatus", "isEnum": true, "attributes": ["Indexed"] },
        { "name": "PlacedAt", "type": "DateTime", "attributes": ["Indexed"] },
        { "name": "Total", "type": "decimal", "attributes": ["Range(0, 1000000)"] }
      ],
      "indexes": [
        { "fields": ["Status", "PlacedAt"], "unique": false }
      ]
    }
  ]
}
""",

            ["03-multi-tenant-and-pii.json"] = """
{
  "namespace": "Acme.Crm",
  "entities": [
    {
      "name": "Contact",
      "softDelete": true,
      "auditable": true,
      "multiTenant": true,
      "properties": [
        { "name": "Id", "type": "ObjectId", "isKey": true },
        { "name": "TenantId", "type": "string", "isTenantKey": true, "attributes": ["Required", "Indexed"] },
        { "name": "Email", "type": "string", "attributes": ["Required", "Email", "Encrypt", "MaskEmail", "PiiEmail"] },
        { "name": "DisplayName", "type": "string", "attributes": ["Required", "MaxLength(200)"] },
        { "name": "Phone", "type": "string", "attributes": ["Phone"] }
      ]
    }
  ]
}
""",

            ["04-workflow.json"] = """
{
  "namespace": "Acme.Claims",
  "entities": [
    {
      "name": "Claim",
      "auditable": true,
      "properties": [
        { "name": "Id", "type": "ObjectId", "isKey": true },
        { "name": "Reference", "type": "string", "attributes": ["Required", "Unique"] },
        { "name": "Amount", "type": "decimal" }
      ]
    }
  ],
  "workflows": [
    {
      "id": "claim-approval",
      "name": "ClaimApproval",
      "entity": "Claim",
      "version": "1.0.0",
      "isActive": true,
      "states": [
        { "name": "Draft", "isInitial": true, "isFinal": false, "allowedRoles": ["Claimant"] },
        { "name": "Review", "isInitial": false, "isFinal": false, "allowedRoles": ["Adjuster"] },
        { "name": "Settled", "isInitial": false, "isFinal": true, "allowedRoles": ["Adjuster"] },
        { "name": "Rejected", "isInitial": false, "isFinal": true, "allowedRoles": ["Adjuster"] }
      ],
      "transitions": [
        { "id": "t1", "name": "Submit", "fromState": "Draft", "toState": "Review", "trigger": "SubmitClaimCommand", "requiredRoles": ["Claimant"] },
        { "id": "t2", "name": "Settle", "fromState": "Review", "toState": "Settled", "trigger": "SettleClaimCommand", "requiredRoles": ["Adjuster"] },
        { "id": "t3", "name": "Reject", "fromState": "Review", "toState": "Rejected", "trigger": "RejectClaimCommand", "requiredRoles": ["Adjuster"] }
      ]
    }
  ]
}
""",

            ["05-endpoint-and-dto.json"] = """
{
  "namespace": "Acme.Billing",
  "entities": [
    {
      "name": "Invoice",
      "softDelete": true,
      "enableKafkaOutbox": true,
      "kafkaTopic": "invoice-events",
      "properties": [
        { "name": "Id", "type": "ObjectId", "isKey": true },
        { "name": "Number", "type": "string", "attributes": ["Required", "Unique"] },
        { "name": "AmountDue", "type": "decimal" },
        { "name": "IssuedOn", "type": "DateTime", "attributes": ["Indexed"] }
      ],
      "apiBusinessRules": {
        "POST": ["InvoiceAmountRule"]
      }
    }
  ],
  "dtos": [
    {
      "name": "InvoiceSummaryDto",
      "properties": [
        { "name": "Number", "type": "string", "sourceEntity": "Invoice", "sourceProperty": "Number", "isRequired": true },
        { "name": "AmountDue", "type": "decimal", "sourceEntity": "Invoice", "sourceProperty": "AmountDue" }
      ]
    }
  ],
  "customEndpoints": [
    {
      "route": "/api/v1/invoices/issue",
      "method": "POST",
      "operationType": "Insert",
      "targetEntity": "Invoice",
      "requestType": "IssueInvoiceCommand",
      "roles": ["Billing"],
      "businessRules": ["IssueInvoiceRule"]
    }
  ]
}
"""
        };

        /// <summary>
        /// Writes the complete bundle to <paramref name="outputDirectory"/>.
        /// </summary>
        /// <returns>The relative paths written.</returns>
        public static IReadOnlyList<string> Write(string outputDirectory)
        {
            Directory.CreateDirectory(outputDirectory);
            var written = new List<string>();

            void Emit(string relativePath, string content)
            {
                var full = Path.Combine(outputDirectory, relativePath);
                var parent = Path.GetDirectoryName(full);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                File.WriteAllText(full, content);
                written.Add(relativePath);
            }

            Emit("foundry.ir.schema.json", IrSchemaGenerator.Generate());
            Emit("SKILL.md", BuildSkillDocument());
            Emit("vocabulary.md", BuildVocabularyDocument());
            Emit("diagnostics.md", BuildDiagnosticsDocument());

            foreach (var example in Examples)
                Emit(Path.Combine("examples", example.Key), example.Value.TrimStart('\n'));

            return written;
        }

        /// <summary>
        /// Builds the system prompt handed to a local model.
        /// </summary>
        /// <remarks>
        /// Deliberately short and imperative. The heavy lifting is done by the JSON Schema passed
        /// as the sampler grammar, not by prose — prose cannot prevent a malformed field name,
        /// whereas the grammar can.
        /// </remarks>
        /// <summary>
        /// Trigger words that make a bulky construct section worth its space in the prompt.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, string[]> SectionTriggers =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["workflow"] = new[]
                {
                    "workflow", "state machine", "lifecycle", "approve", "approval", "reject",
                    "transition", "stage", "status moves", "starts in", "final state", "submitted",
                    "pending", "escalate", "review"
                },
                ["dto"] = new[]
                {
                    "dto", "summary", "projection", "project", "view model", "viewmodel",
                    "response shape", "read model", "returns only", "expose only"
                },
                ["endpoint"] = new[]
                {
                    "endpoint", "route", "/api", "post ", "get ", "put ", "patch ", "delete ",
                    "action", "operation", "restrict", "role", "cancel", "submit"
                }
            };

        private static bool SectionApplies(string section, string? instruction)
        {
            // With no instruction to inspect (for example when writing the static skill bundle),
            // include everything so the documented prompt is complete.
            if (string.IsNullOrWhiteSpace(instruction)) return true;

            return SectionTriggers.TryGetValue(section, out var triggers)
                   && triggers.Any(t => instruction!.Contains(t, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Builds the system prompt, including only the construct sections the instruction needs.
        /// </summary>
        /// <param name="currentSchema">Existing document being modified, if any.</param>
        /// <param name="instruction">
        /// The user's request. When supplied, the bulky per-construct sections (workflows, DTOs,
        /// custom endpoints) are included only when the instruction suggests them.
        /// </param>
        /// <remarks>
        /// <para>
        /// Measured behaviour, not speculation: with every section always present the prompt
        /// reached ~6.7KB and a 30B Q4 model stopped attending to all of it. Each section added to
        /// fix one construct measurably broke another — workflow/DTO guidance knocked out
        /// <c>partitioned</c>, an attribute line knocked out <c>custom-endpoint</c> and
        /// <c>graphql</c> — and the overall pass rate oscillated (93% → 97% → 94%) instead of
        /// converging. The failures were being redistributed, not removed.
        /// </para>
        /// <para>
        /// The always-on core (rules plus the intent→IR map) is small and covers the constructs
        /// that appear in almost every document. The three bulky skeletons are the ones that only
        /// matter when specifically requested, so they are gated on the instruction. This keeps
        /// any single request's prompt well under the level where attention degrades, without
        /// giving up coverage.
        /// </para>
        /// </remarks>
        public static string BuildSystemPrompt(SchemaModel? currentSchema = null, string? instruction = null)
        {
            var sb = new StringBuilder();

            sb.AppendLine("You are the Foundry domain modeller. You author Foundry IR documents.");
            sb.AppendLine();
            sb.AppendLine("RULES");
            sb.AppendLine("1. Output one JSON object: a Foundry IR document. Nothing else.");
            sb.AppendLine("2. Never write C#. The compiler generates all code — persistence, tenancy,");
            sb.AppendLine("   encryption, indexing, outbox, validation, endpoints. Express intent as IR");
            sb.AppendLine("   fields, never as prose and never as an invented type name.");
            sb.AppendLine("3. Every entity: exactly one property with \"isKey\": true, normally");
            sb.AppendLine("   {\"name\": \"Id\", \"type\": \"ObjectId\", \"isKey\": true}.");
            sb.AppendLine("4. Names are PascalCase C# identifiers: letters, digits, underscore only.");
            sb.AppendLine($"5. \"type\" must be one of: {string.Join(", ", Vocabulary.ScalarTypes.Keys)}");
            sb.AppendLine("   — or an enum you declared in \"enums\", in which case also set \"isEnum\": true.");
            sb.AppendLine();
            sb.AppendLine("INTENT -> IR   (use exactly these fields)");
            sb.AppendLine($"  attributes          only these: {string.Join(", ", Vocabulary.AttributeNames)}");
            sb.AppendLine("  mandatory field     attributes [\"Required\"]");
            sb.AppendLine("  encrypted value     \"type\":\"string\" + attributes [\"Encrypt\"]   not EncryptedString");
            sb.AppendLine("  masked value        \"type\":\"string\" + attributes [\"Mask\"] or [\"MaskEmail\"]");
            sb.AppendLine("  whole number        \"type\":\"int\"                              not Int32");
            sb.AppendLine("  one field unique    attributes [\"Unique\"]");
            sb.AppendLine("  COMBINATION unique  entity \"indexes\":[{\"fields\":[\"A\",\"B\"],\"unique\":true}]");
            sb.AppendLine("                      Two separate [\"Unique\"] attributes are NOT the same thing:");
            sb.AppendLine("                      that makes each field unique on its own. When the request");
            sb.AppendLine("                      says a COMBINATION must be unique, use one composite index.");
            sb.AppendLine("  index a field       attributes [\"Indexed\"], or entity \"indexes\" for 2+ fields");
            sb.AppendLine("  multi-tenant        entity \"multiTenant\":true AND a property \"isTenantKey\":true");
            sb.AppendLine("                      (both together, or it is an error)");
            sb.AppendLine("  publish events      entity \"enableKafkaOutbox\":true + \"kafkaTopic\":\"<name>\"");
            sb.AppendLine("                      (\"connectors\" call OUT to REST/SOAP/GraphQL — never Kafka)");
            sb.AppendLine("  real-time push      entity \"enableRealTime\":true");
            sb.AppendLine("  soft delete/audit   entity \"softDelete\":true / \"auditable\":true");
            sb.AppendLine("  hot/cold storage    entity \"partitioned\":true + \"archiveThresholdYears\":<years>");
            sb.AppendLine("  bulk file import    entity \"enableFileIO\":true +");
            sb.AppendLine("                      \"fileIOAllowedExtensions\":[\".csv\",\".xlsx\"]");
            sb.AppendLine("  GraphQL             entity \"enableGraphQL\":true");
            sb.AppendLine("  CUSTOM LOGIC        Name it, never implement it and never invent a field for it.");
            sb.AppendLine("                      Any rule, check, limit, policy or validation the IR cannot");
            sb.AppendLine("                      express becomes a named business rule:");
            sb.AppendLine("                        entity \"apiBusinessRules\":{\"POST\":[\"CreditLimitRule\"]}");
            sb.AppendLine("                      or an endpoint's \"businessRules\":[\"CancelOrderRule\"].");
            sb.AppendLine("                      Foundry scaffolds a stub file the developer completes.");
            if (SectionApplies("workflow", instruction))
            {
            sb.AppendLine();
            sb.AppendLine("STATE MACHINES GO IN \"workflows\"");
            sb.AppendLine("A lifecycle, approval or staged status (\"starts in X\", \"moves to Y\", \"only a");
            sb.AppendLine("Manager may approve\", \"is final\") is a workflow, not a plain status field.");
            sb.AppendLine("Exactly one state \"isInitial\"; terminal states \"isFinal\"; every fromState and");
            sb.AppendLine("toState names a declared state; each transition carries a PascalCase \"trigger\"");
            sb.AppendLine("that becomes a command type; role limits go on \"requiredRoles\". Shape:");
            sb.AppendLine("  \"workflows\": [{");
            sb.AppendLine("    \"id\": \"return-approval\", \"name\": \"ReturnApproval\",");
            sb.AppendLine("    \"entity\": \"<a declared entity>\", \"version\": \"1.0.0\", \"isActive\": true,");
            sb.AppendLine("    \"states\": [");
            sb.AppendLine("      {\"name\": \"Submitted\", \"isInitial\": true,  \"isFinal\": false},");
            sb.AppendLine("      {\"name\": \"Approved\",  \"isInitial\": false, \"isFinal\": true,");
            sb.AppendLine("       \"allowedRoles\": [\"Manager\"]}");
            sb.AppendLine("    ],");
            sb.AppendLine("    \"transitions\": [");
            sb.AppendLine("      {\"id\": \"t1\", \"name\": \"Approve\", \"fromState\": \"Submitted\",");
            sb.AppendLine("       \"toState\": \"Approved\", \"trigger\": \"ApproveReturnCommand\",");
            sb.AppendLine("       \"requiredRoles\": [\"Manager\"]}");
            sb.AppendLine("    ]");
            sb.AppendLine("  }]");
            sb.AppendLine("A workflow does not replace the entity: declare the entity too.");
            }

            if (SectionApplies("dto", instruction))
            {
            sb.AppendLine();
            sb.AppendLine("READ SHAPES GO IN \"dtos\", LINKED TO THEIR SOURCE");
            sb.AppendLine("A summary, view, projection or response shape is a DTO. Every property taken");
            sb.AppendLine("from an entity MUST set both \"sourceEntity\" and \"sourceProperty\"; without them");
            sb.AppendLine("the compiler cannot project and the DTO is dead weight. Shape:");
            sb.AppendLine("  \"dtos\": [{");
            sb.AppendLine("    \"name\": \"OrderSummaryDto\",");
            sb.AppendLine("    \"properties\": [");
            sb.AppendLine("      {\"name\": \"OrderNumber\", \"type\": \"string\",");
            sb.AppendLine("       \"sourceEntity\": \"Order\", \"sourceProperty\": \"OrderNumber\", \"isRequired\": true},");
            sb.AppendLine("      {\"name\": \"Total\", \"type\": \"decimal\",");
            sb.AppendLine("       \"sourceEntity\": \"Order\", \"sourceProperty\": \"Total\"}");
            sb.AppendLine("    ]");
            sb.AppendLine("  }]");
            }

            if (SectionApplies("endpoint", instruction))
            {
            sb.AppendLine();
            sb.AppendLine("NON-CRUD ROUTES GO IN \"customEndpoints\"");
            sb.AppendLine("CRUD is generated for every entity automatically — never declare endpoints for");
            sb.AppendLine("it. Declare one only for a named action (\"cancel an order\", \"approve\") or a");
            sb.AppendLine("specific path. Always set \"targetEntity\"; role limits go in \"roles\".");
            sb.AppendLine("  \"customEndpoints\": [{");
            sb.AppendLine("    \"route\": \"/api/v1/orders/cancel\",     // absolute, begins with /");
            sb.AppendLine("    \"method\": \"POST\",                     // GET, POST, PUT, PATCH, DELETE");
            sb.AppendLine("    \"operationType\": \"Update\",            // Query, Insert, Update, Custom");
            sb.AppendLine("    \"targetEntity\": \"Order\",              // REQUIRED: a declared entity");
            sb.AppendLine("    \"requestType\": \"CancelOrderCommand\",  // PascalCase command/query type");
            sb.AppendLine("    \"roles\": [\"Admin\"],                   // role restrictions go here");
            sb.AppendLine("    \"businessRules\": [\"CancelOrderRule\"]");
            sb.AppendLine("  }]");
            }

            sb.AppendLine();
            sb.AppendLine("The response is grammar-constrained to the Foundry IR JSON Schema, so any field");
            sb.AppendLine("name or value outside the schema is impossible. Model the domain correctly and");
            sb.AppendLine("the output will be valid.");

            if (currentSchema is not null && (currentSchema.Entities?.Count ?? 0) > 0)
            {
                sb.AppendLine();
                sb.AppendLine("CURRENT DOCUMENT — modify this, preserving anything the instruction does not change:");
                sb.AppendLine(System.Text.Json.JsonSerializer.Serialize(currentSchema, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                }));
            }

            return sb.ToString();
        }

        private static string BuildSkillDocument()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Foundry IR authoring skill");
            sb.AppendLine();
            sb.AppendLine("> Generated by `foundry ai-spec`. Do not edit by hand — regenerate instead.");
            sb.AppendLine();
            sb.AppendLine("## The contract");
            sb.AppendLine();
            sb.AppendLine("The model authors **IR**, never C#. The Foundry compiler turns IR into code.");
            sb.AppendLine();
            sb.AppendLine("This is what makes the output trustworthy from a small local model: a missing");
            sb.AppendLine("tenant filter, an unindexed hot query, an N+1 access pattern or a broken");
            sb.AppendLine("encryption call are not mistakes the model *can* make, because it does not");
            sb.AppendLine("author that layer. The only thing it can get wrong is the domain model itself,");
            sb.AppendLine("which is exactly what a human reviews on the Studio canvas.");
            sb.AppendLine();
            sb.AppendLine("## Files");
            sb.AppendLine();
            sb.AppendLine("| File | Purpose |");
            sb.AppendLine("| :--- | :--- |");
            sb.AppendLine("| `foundry.ir.schema.json` | JSON Schema for the IR. Pass as Ollama's `format` to constrain decoding. |");
            sb.AppendLine("| `vocabulary.md` | Supported types and property attributes. |");
            sb.AppendLine("| `diagnostics.md` | Every validation code, with the corrective action. |");
            sb.AppendLine("| `examples/` | Golden IR documents, each verified to compile. |");
            sb.AppendLine();
            sb.AppendLine("## System prompt");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.Append(BuildSystemPrompt());
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine("## Loop");
            sb.AppendLine();
            sb.AppendLine("1. Send the system prompt plus the user instruction, with `format` set to the IR schema.");
            sb.AppendLine("2. Validate the result (`foundry validate`).");
            sb.AppendLine("3. On errors, send the diagnostics back verbatim and ask for a corrected document.");
            sb.AppendLine("4. Repeat at most three times, then surface whatever diagnostics remain.");
            sb.AppendLine();
            sb.AppendLine("Diagnostics are written as edits to the IR document, so they can be fed to the");
            sb.AppendLine("model unmodified.");
            return sb.ToString();
        }

        private static string BuildVocabularyDocument()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Foundry IR vocabulary");
            sb.AppendLine();
            sb.AppendLine("> Generated from `Vocabulary.cs`. This is the list the compiler actually honours.");
            sb.AppendLine();
            sb.AppendLine("## Scalar types");
            sb.AppendLine();
            sb.AppendLine("| IR type | Emitted C# type |");
            sb.AppendLine("| :--- | :--- |");
            foreach (var pair in Vocabulary.ScalarTypes)
                sb.AppendLine($"| `{pair.Key}` | `{pair.Value}` |");
            sb.AppendLine();
            sb.AppendLine($"Recommended key types: {string.Join(", ", Vocabulary.KeyTypes.Select(k => $"`{k}`"))}.");
            sb.AppendLine();
            sb.AppendLine("A property may also be typed as the name of an enum declared in `enums`;");
            sb.AppendLine("set `\"isEnum\": true` in that case.");
            sb.AppendLine();
            sb.AppendLine("## Property attributes");
            sb.AppendLine();
            sb.AppendLine("| Attribute | Args | Entities | DTOs | Effect |");
            sb.AppendLine("| :--- | :--- | :--- | :--- | :--- |");
            foreach (var spec in Vocabulary.Attributes)
            {
                var aliases = spec.Aliases.Count > 0 ? $" (alias: {string.Join(", ", spec.Aliases)})" : "";
                var args = spec.Arity == AttributeArity.Parameterised ? "yes" : "—";
                sb.AppendLine($"| `{spec.Name}`{aliases} | {args} | yes | {(spec.ValidOnDtos ? "yes" : "no")} | {spec.Summary} |");
            }
            sb.AppendLine();
            sb.AppendLine("Argument values may only be numbers, booleans, or double-quoted strings with no");
            sb.AppendLine("embedded quote, backslash or brace. Anything else is rejected as unsafe to emit");
            sb.AppendLine($"(`{DiagnosticCatalog.UnsafeAttributeArgument}`).");
            sb.AppendLine();
            sb.AppendLine("## Enumerated fields");
            sb.AppendLine();
            sb.AppendLine($"- Endpoint `method`: {string.Join(", ", Vocabulary.HttpMethods.OrderBy(m => m))}");
            sb.AppendLine($"- Endpoint `operationType`: {string.Join(", ", Vocabulary.OperationTypes.OrderBy(m => m))}");
            sb.AppendLine($"- Connector `type`: {string.Join(", ", Vocabulary.ConnectorTypes.OrderBy(m => m))}");
            sb.AppendLine($"- Connector `authType`: {string.Join(", ", Vocabulary.AuthTypes.OrderBy(m => m))}");
            return sb.ToString();
        }

        private static string BuildDiagnosticsDocument()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Foundry diagnostics");
            sb.AppendLine();
            sb.AppendLine("> Generated from `DiagnosticCatalog`. Codes are stable and safe to match on.");
            sb.AppendLine();
            sb.AppendLine("Ranges: `FDY1xxx` document structure, `FDY2xxx` cross-reference integrity,");
            sb.AppendLine("`FDY3xxx` configuration coherence, `FDY4xxx` identifier safety.");
            sb.AppendLine();
            sb.AppendLine("| Code | Meaning |");
            sb.AppendLine("| :--- | :--- |");
            foreach (var pair in DiagnosticCatalog.Descriptions.OrderBy(p => p.Key, StringComparer.Ordinal))
                sb.AppendLine($"| `{pair.Key}` | {pair.Value} |");
            return sb.ToString();
        }
    }
}
