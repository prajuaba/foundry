using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Foundry.Schema.Compiler
{
    /// <summary>
    /// One assertion about a generated IR document.
    /// </summary>
    /// <param name="Description">
    /// What the model was supposed to do, phrased so a failure reads as an actionable gap.
    /// </param>
    /// <param name="Check">Predicate over the generated document.</param>
    public sealed record EvalAssertion(string Description, Func<SchemaModel, bool> Check);

    /// <summary>
    /// A single evaluation case: a natural-language instruction plus what the resulting IR must contain.
    /// </summary>
    /// <summary>
    /// How demanding a case is.
    /// </summary>
    public enum EvalDifficulty
    {
        /// <summary>
        /// One construct, named in the prompt in roughly the IR's own vocabulary.
        /// </summary>
        Core,

        /// <summary>
        /// Business phrasing rather than IR vocabulary, several constructs interacting, or a
        /// requirement the model has to notice rather than be handed.
        /// </summary>
        Hard
    }

    public sealed record EvalCase
    {
        /// <summary>Stable identifier, so a regression can be tracked across runs.</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>
        /// Difficulty band. Reported separately, because a suite where everything passes has
        /// stopped measuring anything — the Core band is a regression guard, the Hard band is
        /// where headroom shows up.
        /// </summary>
        public EvalDifficulty Difficulty { get; init; } = EvalDifficulty.Core;

        /// <summary>
        /// The IR construct under test. This is the reporting axis that makes the harness useful:
        /// a pass rate per construct says which part of the IR models misunderstand, and therefore
        /// which part of the schema, vocabulary or system prompt to fix.
        /// </summary>
        public string Construct { get; init; } = string.Empty;

        /// <summary>The instruction handed to the model, as a user would phrase it.</summary>
        public string Prompt { get; init; } = string.Empty;

        /// <summary>What must be true of the generated document.</summary>
        public IReadOnlyList<EvalAssertion> Assertions { get; init; } = Array.Empty<EvalAssertion>();
    }

    /// <summary>Outcome of one assertion.</summary>
    public sealed record EvalAssertionResult(string Description, bool Passed);

    /// <summary>Outcome of running one case once.</summary>
    public sealed record EvalCaseResult
    {
        /// <summary>The case that was run.</summary>
        public string CaseId { get; init; } = string.Empty;

        /// <summary>The construct under test.</summary>
        public string Construct { get; init; } = string.Empty;

        /// <summary>Difficulty band of the case that produced this result.</summary>
        public EvalDifficulty Difficulty { get; init; } = EvalDifficulty.Core;

        /// <summary>Which repetition this was, 1-based.</summary>
        public int Run { get; init; } = 1;

        /// <summary>True when the model produced a document that passed validation.</summary>
        public bool ProducedValidIr { get; init; }

        /// <summary>How many generation attempts the repair loop needed.</summary>
        public int Attempts { get; init; }

        /// <summary>Wall-clock duration.</summary>
        public long ElapsedMs { get; init; }

        /// <summary>Per-assertion outcomes.</summary>
        public IReadOnlyList<EvalAssertionResult> Assertions { get; init; } = Array.Empty<EvalAssertionResult>();

        /// <summary>Populated when generation failed outright.</summary>
        public string? Error { get; init; }

        /// <summary>True only when valid IR was produced and every assertion held.</summary>
        public bool Passed => ProducedValidIr && Assertions.All(a => a.Passed);
    }

    /// <summary>Aggregate outcome of an evaluation run.</summary>
    public sealed record EvalRunResult
    {
        /// <summary>Model evaluated.</summary>
        public string Model { get; init; } = string.Empty;

        /// <summary>Host the model ran on.</summary>
        public string Host { get; init; } = string.Empty;

        /// <summary>When the run started (UTC).</summary>
        public DateTime StartedUtc { get; init; } = DateTime.UtcNow;

        /// <summary>Every case result, including repeats.</summary>
        public IReadOnlyList<EvalCaseResult> Results { get; init; } = Array.Empty<EvalCaseResult>();

        /// <summary>Fraction of case runs that fully passed.</summary>
        public double PassRate => Results.Count == 0 ? 0 : (double)Results.Count(r => r.Passed) / Results.Count;

        /// <summary>Fraction of case runs that at least produced schema-valid IR.</summary>
        public double ValidIrRate => Results.Count == 0 ? 0 : (double)Results.Count(r => r.ProducedValidIr) / Results.Count;
    }

    /// <summary>
    /// Measures how reliably a local model authors Foundry IR.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The premise of Foundry's AI story is that a small local model can be trusted because it
    /// authors IR rather than C#, so correctness of the generated <em>code</em> is guaranteed by
    /// the compiler and the only remaining risk is whether the model modelled the domain right.
    /// That risk is measurable, and this is what measures it.
    /// </para>
    /// <para>
    /// Results are grouped by IR construct rather than by case, because the actionable output is
    /// not "the model scored 78%" but "the model gets Kafka wrong 100% of the time" — which points
    /// at a specific fix in the schema, the vocabulary or the system prompt.
    /// </para>
    /// </remarks>
    public static class EvalHarness
    {
        // ---- assertion helpers -------------------------------------------------------------

        private static Entity? Ent(SchemaModel s, string name)
            => (s.Entities ?? new List<Entity>())
                .FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

        /// <summary>Finds an entity by any of several acceptable names, since naming is the model's choice.</summary>
        private static Entity? AnyEnt(SchemaModel s, params string[] names)
            => names.Select(n => Ent(s, n)).FirstOrDefault(e => e is not null);

        private static Property? Prop(Entity? e, string name)
            => (e?.Properties ?? new List<Property>())
                .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

        private static Property? AnyProp(Entity? e, params string[] names)
            => names.Select(n => Prop(e, n)).FirstOrDefault(p => p is not null);

        private static bool HasAttr(Property? p, string attribute)
            => (p?.Attributes ?? new List<string>())
                .Any(a => a.StartsWith(attribute, StringComparison.OrdinalIgnoreCase));

        private static bool TypeIs(Property? p, params string[] types)
            => p is not null && types.Any(t => string.Equals(p.Type, t, StringComparison.OrdinalIgnoreCase));

        private static Enum? EnumDef(SchemaModel s, string name)
            => (s.Enums ?? new List<Enum>())
                .FirstOrDefault(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));

        private static bool AnyEntity(SchemaModel s, Func<Entity, bool> predicate)
            => (s.Entities ?? new List<Entity>()).Any(predicate);

        private static EvalAssertion A(string description, Func<SchemaModel, bool> check)
            => new(description, s =>
            {
                // A model can omit anything, so every helper must tolerate nulls rather than
                // turning a wrong answer into a harness crash.
                try { return check(s); }
                catch { return false; }
            });

        // ---- the cases ---------------------------------------------------------------------

        /// <summary>The evaluation suite.</summary>
        public static readonly IReadOnlyList<EvalCase> Cases = new List<EvalCase>
        {
            new()
            {
                Id = "entity-basic",
                Construct = "entity",
                Prompt = "Create a Foundry domain in namespace Shop.Catalog with a single Product entity that has a name and a price.",
                Assertions = new[]
                {
                    A("namespace is Shop.Catalog", s => s.Namespace == "Shop.Catalog"),
                    A("declares a Product entity", s => Ent(s, "Product") is not null),
                    A("Product has exactly one key property", s => Ent(s, "Product")?.Properties.Count(p => p.IsKey) == 1),
                    A("Product has a name property", s => AnyProp(Ent(s, "Product"), "Name", "ProductName") is not null),
                    A("price is a numeric type", s => TypeIs(AnyProp(Ent(s, "Product"), "Price", "UnitPrice"), "decimal", "double", "float"))
                }
            },
            new()
            {
                Id = "key-objectid",
                Construct = "entity",
                Prompt = "In namespace Shop.Core, create an Item entity. Use a MongoDB ObjectId as its identifier.",
                Assertions = new[]
                {
                    A("key property is typed ObjectId", s => TypeIs(Ent(s, "Item")?.Properties.FirstOrDefault(p => p.IsKey), "ObjectId"))
                }
            },
            new()
            {
                Id = "types-scalar",
                Construct = "types",
                Prompt = "In namespace Data.Mixed, create a Reading entity with: a text label, a whole-number count, a decimal amount, a true/false flag, and a timestamp.",
                Assertions = new[]
                {
                    A("uses only supported scalar types or declared enums", s =>
                        (s.Entities ?? new()).SelectMany(e => e.Properties ?? new())
                            .All(p => Vocabulary.IsKnownScalar(p.Type)
                                      || (s.Enums ?? new()).Any(en => string.Equals(en.Name, p.Type, StringComparison.OrdinalIgnoreCase)))),
                    A("whole number uses int (not Int32/Integer)", s =>
                        AnyEntity(s, e => (e.Properties ?? new()).Any(p => string.Equals(p.Type, "int", StringComparison.OrdinalIgnoreCase)))),
                    A("timestamp uses DateTime", s =>
                        AnyEntity(s, e => (e.Properties ?? new()).Any(p => string.Equals(p.Type, "DateTime", StringComparison.OrdinalIgnoreCase)))),
                    A("flag uses bool", s =>
                        AnyEntity(s, e => (e.Properties ?? new()).Any(p => string.Equals(p.Type, "bool", StringComparison.OrdinalIgnoreCase))))
                }
            },
            new()
            {
                Id = "enum-declare-and-use",
                Construct = "enum",
                Prompt = "In namespace Shop.Orders, create an Order entity with a Status field. Status must be one of Draft, Placed, Shipped, or Cancelled.",
                Assertions = new[]
                {
                    A("declares an enum with the four values", s =>
                        (s.Enums ?? new()).Any(e => new[] { "Draft", "Placed", "Shipped", "Cancelled" }
                            .All(v => (e.Values ?? new()).Contains(v, StringComparer.OrdinalIgnoreCase)))),
                    A("Status property is marked isEnum", s => Prop(Ent(s, "Order"), "Status")?.IsEnum == true),
                    A("Status type matches the declared enum name", s =>
                        EnumDef(s, Prop(Ent(s, "Order"), "Status")?.Type ?? "") is not null),
                    A("does not also declare the enum as an entity (FDY2014)", s =>
                        !(s.Enums ?? new()).Any(en => Ent(s, en.Name) is not null))
                }
            },
            new()
            {
                Id = "attr-required",
                Construct = "attributes",
                Prompt = "In namespace Shop.Users, create a Customer entity whose Email is mandatory.",
                Assertions = new[]
                {
                    A("Email carries the Required attribute", s => HasAttr(Prop(Ent(s, "Customer"), "Email"), "Required"))
                }
            },
            new()
            {
                Id = "attr-unique",
                Construct = "attributes",
                Prompt = "In namespace Shop.Users, create an Account entity where the Username must be unique across the collection.",
                Assertions = new[]
                {
                    A("Username is marked Unique", s => HasAttr(AnyProp(Ent(s, "Account"), "Username", "UserName"), "Unique"))
                }
            },
            new()
            {
                Id = "attr-maxlength",
                Construct = "attributes",
                Prompt = "In namespace Shop.Content, create an Article entity whose Title is at most 200 characters.",
                Assertions = new[]
                {
                    A("Title carries MaxLength", s => HasAttr(Prop(Ent(s, "Article"), "Title"), "MaxLength")),
                    A("MaxLength uses 200", s =>
                        (Prop(Ent(s, "Article"), "Title")?.Attributes ?? new()).Any(a => a.Replace(" ", "").Contains("MaxLength(200)")))
                }
            },
            new()
            {
                Id = "attr-email",
                Construct = "attributes",
                Prompt = "In namespace Shop.Users, create a Subscriber entity with an email address field that should be validated as an email.",
                Assertions = new[]
                {
                    A("email field carries the Email attribute", s => HasAttr(AnyProp(Ent(s, "Subscriber"), "Email", "EmailAddress"), "Email"))
                }
            },
            new()
            {
                Id = "attr-encrypt",
                Construct = "security",
                Prompt = "In namespace Health.Records, create a Patient entity whose SocialSecurityNumber must be encrypted at rest.",
                Assertions = new[]
                {
                    A("SSN uses type string (not an invented EncryptedString)", s =>
                        TypeIs(AnyProp(Ent(s, "Patient"), "SocialSecurityNumber", "Ssn", "SSN"), "string")),
                    A("SSN carries the Encrypt attribute", s =>
                        HasAttr(AnyProp(Ent(s, "Patient"), "SocialSecurityNumber", "Ssn", "SSN"), "Encrypt"))
                }
            },
            new()
            {
                Id = "attr-mask-email",
                Construct = "security",
                Prompt = "In namespace Health.Records, create a Contact entity whose Email should be masked in logs and API responses.",
                Assertions = new[]
                {
                    A("Email uses type string", s => TypeIs(Prop(Ent(s, "Contact"), "Email"), "string")),
                    A("Email carries Mask or MaskEmail", s => HasAttr(Prop(Ent(s, "Contact"), "Email"), "Mask"))
                }
            },
            new()
            {
                Id = "multi-tenant",
                Construct = "multi-tenancy",
                Prompt = "In namespace Saas.Billing, create an Invoice entity that is isolated per tenant, with a TenantId discriminator.",
                Assertions = new[]
                {
                    A("entity sets multiTenant", s => Ent(s, "Invoice")?.MultiTenant == true),
                    A("a property is marked isTenantKey", s =>
                        (Ent(s, "Invoice")?.Properties ?? new()).Any(p => p.IsTenantKey || HasAttr(p, "TenantKey")))
                }
            },
            new()
            {
                Id = "soft-delete-audit",
                Construct = "entity-flags",
                Prompt = "In namespace Shop.Orders, create a Refund entity that supports soft deletion and keeps an audit trail.",
                Assertions = new[]
                {
                    A("softDelete is enabled", s => Ent(s, "Refund")?.SoftDelete == true),
                    A("auditable is enabled", s => Ent(s, "Refund")?.Auditable == true)
                }
            },
            new()
            {
                Id = "kafka-outbox",
                Construct = "kafka",
                Prompt = "In namespace Shop.Orders, create a Shipment entity. Publish shipment domain events to a Kafka topic called shipment-events.",
                Assertions = new[]
                {
                    A("entity sets enableKafkaOutbox", s => Ent(s, "Shipment")?.KafkaOutboxEnabled == true),
                    A("kafkaTopic is shipment-events", s =>
                        string.Equals(Ent(s, "Shipment")?.KafkaTopic, "shipment-events", StringComparison.OrdinalIgnoreCase)),
                    A("does not model Kafka as an outbound connector", s => (s.Connectors ?? new()).Count == 0)
                }
            },
            new()
            {
                Id = "realtime",
                Construct = "entity-flags",
                Prompt = "In namespace Ops.Monitoring, create an Alert entity that pushes live updates to connected clients over websockets.",
                Assertions = new[]
                {
                    A("entity sets enableRealTime", s => Ent(s, "Alert")?.RealTime == true)
                }
            },
            new()
            {
                Id = "fileio",
                Construct = "entity-flags",
                Prompt = "In namespace Shop.Import, create a PriceRow entity that supports bulk import from CSV and Excel files.",
                Assertions = new[]
                {
                    A("entity sets enableFileIO", s => Ent(s, "PriceRow")?.FileIoEnabled == true),
                    A("allowed extensions include csv", s =>
                        (Ent(s, "PriceRow")?.FileIoAllowedExtensions ?? new())
                            .Any(e => e.Contains("csv", StringComparison.OrdinalIgnoreCase)))
                }
            },
            new()
            {
                Id = "partitioned",
                Construct = "entity-flags",
                Prompt = "In namespace Ops.Telemetry, create an Event entity that is partitioned into hot and cold storage, archiving records older than 3 years.",
                Assertions = new[]
                {
                    A("entity sets partitioned", s => Ent(s, "Event")?.Partitioned == true),
                    A("archiveThresholdYears is 3", s => Ent(s, "Event")?.ArchiveThresholdYears == 3)
                }
            },
            new()
            {
                Id = "index-single",
                Construct = "indexes",
                Prompt = "In namespace Shop.Orders, create a Payment entity with a ProcessedAt timestamp. Add a database index on ProcessedAt.",
                Assertions = new[]
                {
                    A("ProcessedAt is indexed, by attribute or entity index", s =>
                        HasAttr(Prop(Ent(s, "Payment"), "ProcessedAt"), "Index")
                        || (Ent(s, "Payment")?.Indexes ?? new()).Any(i => (i.Fields ?? new()).Contains("ProcessedAt", StringComparer.OrdinalIgnoreCase)))
                }
            },
            new()
            {
                Id = "index-composite",
                Construct = "indexes",
                Prompt = "In namespace Shop.Orders, create a LineItem entity with OrderId and Sku. Add a single compound database index over OrderId then Sku.",
                Assertions = new[]
                {
                    A("declares a composite index over both fields in order", s =>
                        (Ent(s, "LineItem")?.Indexes ?? new()).Any(i =>
                            (i.Fields ?? new()).Count >= 2
                            && string.Equals(i.Fields[0], "OrderId", StringComparison.OrdinalIgnoreCase)
                            && string.Equals(i.Fields[1], "Sku", StringComparison.OrdinalIgnoreCase)))
                }
            },
            new()
            {
                Id = "index-unique-composite",
                Construct = "indexes",
                Prompt = "In namespace Saas.Config, create a Setting entity with TenantId and Key. The combination of TenantId and Key must be unique.",
                Assertions = new[]
                {
                    A("declares a unique composite index", s =>
                        (Ent(s, "Setting")?.Indexes ?? new()).Any(i => i.Unique && (i.Fields ?? new()).Count >= 2))
                }
            },
            new()
            {
                Id = "dto-projection",
                Construct = "dto",
                Prompt = "In namespace Shop.Orders, create an Order entity with OrderNumber and Total. Also create an OrderSummaryDto that projects OrderNumber and Total from Order.",
                Assertions = new[]
                {
                    A("declares a DTO", s => (s.Dtos ?? new()).Count > 0),
                    A("DTO properties name Order as sourceEntity", s =>
                        (s.Dtos ?? new()).SelectMany(d => d.Properties ?? new())
                            .Any(p => string.Equals(p.SourceEntity, "Order", StringComparison.OrdinalIgnoreCase))),
                    // Same vacuous-truth guard as the workflow case: All() over the empty set is
                    // true, so this passed for a DTO that set no sourceProperty at all — which is
                    // precisely the failure mode being measured.
                    A("DTO sourceProperty values exist on Order", s =>
                    {
                        var projected = (s.Dtos ?? new()).SelectMany(d => d.Properties ?? new())
                            .Where(p => !string.IsNullOrEmpty(p.SourceProperty))
                            .ToList();

                        return projected.Count > 0
                               && projected.All(p => Prop(Ent(s, "Order"), p.SourceProperty!) is not null);
                    })
                }
            },
            new()
            {
                Id = "custom-endpoint",
                Construct = "endpoint",
                Prompt = "In namespace Shop.Orders, create an Order entity and a custom POST endpoint at /api/v1/orders/cancel that cancels an order. Restrict it to the Admin role.",
                Assertions = new[]
                {
                    A("declares a custom endpoint", s => (s.CustomEndpoints ?? new()).Count > 0),
                    A("method is POST", s => (s.CustomEndpoints ?? new()).Any(e => string.Equals(e.Method, "POST", StringComparison.OrdinalIgnoreCase))),
                    A("route is the requested path", s => (s.CustomEndpoints ?? new()).Any(e => e.Route == "/api/v1/orders/cancel")),
                    A("targets the Order entity", s => (s.CustomEndpoints ?? new()).Any(e => string.Equals(e.TargetEntity, "Order", StringComparison.OrdinalIgnoreCase))),
                    A("restricted to Admin", s => (s.CustomEndpoints ?? new()).Any(e => (e.Roles ?? new()).Contains("Admin", StringComparer.OrdinalIgnoreCase)))
                }
            },
            new()
            {
                Id = "business-rule",
                Construct = "rules",
                Prompt = "In namespace Shop.Orders, create an Order entity. Creating an order must be checked against a credit limit rule, which is custom business logic.",
                Assertions = new[]
                {
                    A("names a business rule rather than inventing a field", s =>
                        (Ent(s, "Order")?.ApiBusinessRules ?? new()).Any(kv => (kv.Value ?? new()).Count > 0)
                        || (s.CustomEndpoints ?? new()).Any(e => (e.BusinessRules ?? new()).Count > 0))
                }
            },
            new()
            {
                Id = "workflow-basic",
                Construct = "workflow",
                Prompt = "In namespace Shop.Returns, create a ReturnRequest entity with a workflow: it starts in Submitted, can move to Approved or Rejected, and both of those are final states.",
                Assertions = new[]
                {
                    A("declares a workflow", s => (s.Workflows ?? new()).Count > 0),
                    A("workflow is bound to ReturnRequest", s =>
                        (s.Workflows ?? new()).Any(w => string.Equals(w.Entity, "ReturnRequest", StringComparison.OrdinalIgnoreCase))),
                    A("exactly one initial state", s =>
                        (s.Workflows ?? new()).Any(w => (w.States ?? new()).Count(st => st.IsInitial) == 1)),
                    A("has at least one final state", s =>
                        (s.Workflows ?? new()).Any(w => (w.States ?? new()).Any(st => st.IsFinal))),
                    // Guarded against vacuous truth: All() over an empty workflow list returns
                    // true, so without the Count check this assertion passed for a document
                    // containing no workflow at all — flattering the score for the exact failure
                    // the case exists to detect.
                    A("transitions reference declared states", s =>
                        (s.Workflows ?? new()).Count > 0
                        && (s.Workflows ?? new()).All(w =>
                        {
                            var transitions = w.Transitions ?? new();
                            if (transitions.Count == 0) return false;

                            var names = new HashSet<string>((w.States ?? new()).Select(st => st.Name), StringComparer.OrdinalIgnoreCase);
                            return transitions.All(t =>
                                names.Contains(t.FromState ?? "") && names.Contains(t.ToState ?? ""));
                        }))
                }
            },
            new()
            {
                Id = "workflow-roles",
                Construct = "workflow",
                Prompt = "In namespace Shop.Expenses, create an ExpenseClaim entity with an approval workflow where only the Manager role may approve.",
                Assertions = new[]
                {
                    A("a transition requires the Manager role", s =>
                        (s.Workflows ?? new()).SelectMany(w => w.Transitions ?? new())
                            .Any(t => (t.RequiredRoles ?? new()).Contains("Manager", StringComparer.OrdinalIgnoreCase)))
                }
            },
            new()
            {
                Id = "connector-rest",
                Construct = "connector",
                Prompt = "In namespace Shop.Shipping, create a Parcel entity and configure an outbound REST connector to https://api.carrier.example.com using bearer token authentication.",
                Assertions = new[]
                {
                    A("declares a connector", s => (s.Connectors ?? new()).Count > 0),
                    A("connector type is REST", s => (s.Connectors ?? new()).Any(c => string.Equals(c.Type, "REST", StringComparison.OrdinalIgnoreCase))),
                    A("auth type is Bearer", s => (s.Connectors ?? new()).Any(c => string.Equals(c.AuthType, "Bearer", StringComparison.OrdinalIgnoreCase))),
                    A("base URL is the carrier API", s => (s.Connectors ?? new()).Any(c => (c.BaseUrl ?? "").Contains("carrier.example.com")))
                }
            },
            new()
            {
                Id = "graphql",
                Construct = "entity-flags",
                Prompt = "In namespace Shop.Catalog, create a Category entity and expose it over GraphQL.",
                Assertions = new[]
                {
                    A("entity sets enableGraphQL", s => Ent(s, "Category")?.GraphQlEnabled == true)
                }
            },
            new()
            {
                Id = "multi-entity-relationship",
                Construct = "entity",
                Prompt = "In namespace Blog.Content, model Authors and Posts. Each post belongs to one author.",
                Assertions = new[]
                {
                    A("declares both entities", s => Ent(s, "Author") is not null && Ent(s, "Post") is not null),
                    A("Post references the author by id", s =>
                        AnyProp(Ent(s, "Post"), "AuthorId", "Author_Id") is not null),
                    A("both entities have a key", s =>
                        (s.Entities ?? new()).All(e => (e.Properties ?? new()).Count(p => p.IsKey) == 1))
                }
            },
            new()
            {
                Id = "naming-pascal",
                Construct = "naming",
                Prompt = "In namespace Shop.Inventory, create an entity for stock keeping units with a field for the quantity on hand and one for the reorder threshold.",
                Assertions = new[]
                {
                    A("entity and property names are valid C# identifiers", s =>
                        (s.Entities ?? new()).All(e =>
                            CodeGen.IsValidIdentifier(e.Name)
                            && (e.Properties ?? new()).All(p => CodeGen.IsValidIdentifier(p.Name)))),
                    A("names are PascalCase", s =>
                        (s.Entities ?? new()).All(e => e.Name.Length > 0 && char.IsUpper(e.Name[0])
                            && (e.Properties ?? new()).All(p => p.Name.Length > 0 && char.IsUpper(p.Name[0]))))
                }
            },
            new()
            {
                Id = "no-invented-types",
                Construct = "types",
                Prompt = "In namespace Secure.Vault, create a Credential entity with an encrypted secret, a masked hint, and an expiry date.",
                Assertions = new[]
                {
                    A("every type is a supported scalar or a declared enum", s =>
                        (s.Entities ?? new()).SelectMany(e => e.Properties ?? new())
                            .All(p => Vocabulary.IsKnownScalar(p.Type)
                                      || (s.Enums ?? new()).Any(en => string.Equals(en.Name, p.Type, StringComparison.OrdinalIgnoreCase)))),
                    A("encryption expressed as an attribute, not a type", s =>
                        (s.Entities ?? new()).SelectMany(e => e.Properties ?? new()).Any(p => HasAttr(p, "Encrypt")))
                }
            },
            // ---- Hard band -----------------------------------------------------------------
            //
            // These are phrased the way a stakeholder actually talks, so the model has to infer
            // which IR construct expresses the requirement rather than being told. Several also
            // bury a requirement mid-paragraph, which is where a 30B model previously dropped
            // one silently.

            new()
            {
                Id = "hard-business-phrasing",
                Construct = "inference",
                Difficulty = EvalDifficulty.Hard,
                Prompt = "In namespace Legal.Matters, we track Matters for law firms. Each firm must only "
                       + "ever see its own matters. We need a full paper trail of who changed what, and "
                       + "nothing should ever be truly erased from the system. Client reference numbers "
                       + "must never collide.",
                Assertions = new[]
                {
                    A("isolates firms via multiTenant + a tenant key", s =>
                        AnyEntity(s, e => e.MultiTenant
                                          && (e.Properties ?? new()).Any(p => p.IsTenantKey || HasAttr(p, "TenantKey")))),
                    A("'paper trail' becomes auditable, not a hand-rolled log entity", s =>
                        AnyEntity(s, e => e.Auditable)),
                    A("'never truly erased' becomes softDelete", s =>
                        AnyEntity(s, e => e.SoftDelete)),
                    A("'must never collide' becomes a Unique attribute or unique index", s =>
                        AnyEntity(s, e =>
                            (e.Properties ?? new()).Any(p => HasAttr(p, "Unique"))
                            || (e.Indexes ?? new()).Any(i => i.Unique)))
                }
            },
            new()
            {
                Id = "hard-buried-requirement",
                Construct = "inference",
                Difficulty = EvalDifficulty.Hard,
                Prompt = "In namespace Retail.Fulfilment, create a Shipment entity with a tracking number, "
                       + "a carrier name, a dispatch timestamp and a weight in kilograms. The tracking "
                       + "number is how customers look shipments up, so that lookup has to be fast even "
                       + "with millions of rows. Other teams subscribe to shipment updates through our "
                       + "event bus on the shipment-events topic. Weight should never be negative.",
                Assertions = new[]
                {
                    A("tracking number is indexed for the stated lookup", s =>
                        HasAttr(AnyProp(Ent(s, "Shipment"), "TrackingNumber", "Tracking"), "Index")
                        || HasAttr(AnyProp(Ent(s, "Shipment"), "TrackingNumber", "Tracking"), "Unique")
                        || (Ent(s, "Shipment")?.Indexes ?? new()).Any(i =>
                            (i.Fields ?? new()).Any(f => f.Contains("Track", StringComparison.OrdinalIgnoreCase)))),
                    A("the buried event-bus requirement becomes Kafka outbox", s =>
                        Ent(s, "Shipment")?.KafkaOutboxEnabled == true),
                    A("uses the topic named in the prompt", s =>
                        string.Equals(Ent(s, "Shipment")?.KafkaTopic, "shipment-events", StringComparison.OrdinalIgnoreCase)),
                    A("weight constraint expressed as Range, not prose", s =>
                        HasAttr(AnyProp(Ent(s, "Shipment"), "Weight", "WeightKg", "WeightInKg"), "Range"))
                }
            },
            new()
            {
                Id = "hard-multi-entity",
                Construct = "multi-entity",
                Difficulty = EvalDifficulty.Hard,
                Prompt = "In namespace Academy.Enrolment, model a small course platform: Students, Courses, "
                       + "Instructors, and Enrolments linking a student to a course. Each course has one "
                       + "instructor. A student cannot enrol on the same course twice.",
                Assertions = new[]
                {
                    A("declares all four entities", s =>
                        new[] { "Student", "Course", "Instructor", "Enrolment" }
                            .All(n => AnyEnt(s, n, n + "s", n.Replace("Enrolment", "Enrollment")) is not null)),
                    A("every entity has exactly one key", s =>
                        (s.Entities ?? new()).All(e => (e.Properties ?? new()).Count(p => p.IsKey) == 1)),
                    A("Enrolment references both student and course", s =>
                    {
                        var e = AnyEnt(s, "Enrolment", "Enrollment");
                        return AnyProp(e, "StudentId") is not null && AnyProp(e, "CourseId") is not null;
                    }),
                    A("Course references its instructor", s =>
                        AnyProp(AnyEnt(s, "Course"), "InstructorId") is not null),
                    A("'cannot enrol twice' becomes a unique composite index", s =>
                    {
                        var e = AnyEnt(s, "Enrolment", "Enrollment");
                        return (e?.Indexes ?? new()).Any(i => i.Unique && (i.Fields ?? new()).Count >= 2);
                    })
                }
            },
            new()
            {
                Id = "hard-enum-not-entity",
                Construct = "traps",
                Difficulty = EvalDifficulty.Hard,
                Prompt = "In namespace Support.Desk, create a Ticket entity. A ticket has a priority, which "
                       + "is one of Low, Normal, High or Urgent, and a status of Open, Pending, or Closed. "
                       + "Also keep the reporter's email address, encrypted.",
                Assertions = new[]
                {
                    A("declares two enums", s => (s.Enums ?? new()).Count >= 2),
                    A("does NOT also declare those enums as entities (FDY2014)", s =>
                        !(s.Enums ?? new()).Any(en => Ent(s, en.Name) is not null)),
                    A("priority and status are enum-typed properties", s =>
                    {
                        var t = Ent(s, "Ticket");
                        return Prop(t, "Priority")?.IsEnum == true && Prop(t, "Status")?.IsEnum == true;
                    }),
                    A("email uses string + Encrypt, not an invented type", s =>
                    {
                        var e = AnyProp(Ent(s, "Ticket"), "ReporterEmail", "Email");
                        return TypeIs(e, "string") && HasAttr(e, "Encrypt");
                    })
                }
            },
            new()
            {
                Id = "hard-rule-not-field",
                Construct = "traps",
                Difficulty = EvalDifficulty.Hard,
                Prompt = "In namespace Lending.Applications, create a LoanApplication with an amount and an "
                       + "applicant id. Applications over 50000 need a second approver, and we reject any "
                       + "applicant whose debt-to-income ratio exceeds our policy threshold. The threshold "
                       + "changes quarterly.",
                Assertions = new[]
                {
                    A("policy logic is named as a business rule, not modelled as a field", s =>
                        (s.Entities ?? new()).Any(e => (e.ApiBusinessRules ?? new()).Any(kv => (kv.Value ?? new()).Count > 0))
                        || (s.CustomEndpoints ?? new()).Any(e => (e.BusinessRules ?? new()).Count > 0)),
                    A("does not invent a 'threshold' storage field on the application", s =>
                        !(Ent(s, "LoanApplication")?.Properties ?? new())
                            .Any(p => p.Name.Contains("Threshold", StringComparison.OrdinalIgnoreCase))),
                    A("amount is numeric", s =>
                        TypeIs(AnyProp(Ent(s, "LoanApplication"), "Amount", "LoanAmount"), "decimal", "double"))
                }
            },
            new()
            {
                Id = "hard-workflow-with-enum",
                Construct = "cross-construct",
                Difficulty = EvalDifficulty.Hard,
                Prompt = "In namespace Publishing.Editorial, an Article moves through Draft, InReview, "
                       + "Approved and Published. Only an Editor may approve. Once published it cannot "
                       + "change again. Keep the current stage queryable on the article itself.",
                Assertions = new[]
                {
                    A("declares a workflow bound to Article", s =>
                        (s.Workflows ?? new()).Any(w => string.Equals(w.Entity, "Article", StringComparison.OrdinalIgnoreCase))),
                    A("exactly one initial state", s =>
                        (s.Workflows ?? new()).Any(w => (w.States ?? new()).Count(st => st.IsInitial) == 1)),
                    A("Published is final", s =>
                        (s.Workflows ?? new()).SelectMany(w => w.States ?? new())
                            .Any(st => st.IsFinal && st.Name.Contains("Publish", StringComparison.OrdinalIgnoreCase))),
                    A("an Editor role gates a transition", s =>
                        (s.Workflows ?? new()).SelectMany(w => w.Transitions ?? new())
                            .Any(t => (t.RequiredRoles ?? new()).Any(r => r.Contains("Editor", StringComparison.OrdinalIgnoreCase)))),
                    A("all transitions reference declared states", s =>
                        (s.Workflows ?? new()).Count > 0
                        && (s.Workflows ?? new()).All(w =>
                        {
                            var names = new HashSet<string>((w.States ?? new()).Select(st => st.Name), StringComparer.OrdinalIgnoreCase);
                            var ts = w.Transitions ?? new();
                            return ts.Count > 0 && ts.All(t => names.Contains(t.FromState ?? "") && names.Contains(t.ToState ?? ""));
                        }))
                }
            },
            new()
            {
                Id = "hard-dto-and-endpoint",
                Construct = "cross-construct",
                Difficulty = EvalDifficulty.Hard,
                Prompt = "In namespace Billing.Statements, we have Invoices with a number, an amount and a "
                       + "customer id. The mobile app needs a lightweight view showing only the number and "
                       + "amount, and finance staff need a way to void an invoice at /api/v1/invoices/void. "
                       + "Only the Finance role may void.",
                Assertions = new[]
                {
                    A("declares a DTO projecting from Invoice", s =>
                        (s.Dtos ?? new()).SelectMany(d => d.Properties ?? new())
                            .Any(pr => string.Equals(pr.SourceEntity, "Invoice", StringComparison.OrdinalIgnoreCase))),
                    A("the DTO is lightweight: it does not project the customer id", s =>
                        !(s.Dtos ?? new()).SelectMany(d => d.Properties ?? new())
                            .Any(pr => (pr.SourceProperty ?? "").Contains("Customer", StringComparison.OrdinalIgnoreCase))),
                    A("declares the void endpoint at the stated route", s =>
                        (s.CustomEndpoints ?? new()).Any(e => e.Route == "/api/v1/invoices/void")),
                    A("void endpoint targets Invoice", s =>
                        (s.CustomEndpoints ?? new()).Any(e => string.Equals(e.TargetEntity, "Invoice", StringComparison.OrdinalIgnoreCase))),
                    A("restricted to Finance", s =>
                        (s.CustomEndpoints ?? new()).Any(e => (e.Roles ?? new()).Any(r => r.Contains("Finance", StringComparison.OrdinalIgnoreCase))))
                }
            },
            new()
            {
                Id = "hard-negative-constraint",
                Construct = "traps",
                Difficulty = EvalDifficulty.Hard,
                Prompt = "In namespace Analytics.Events, create a PageView entity with a url, a session id "
                       + "and a viewed-at timestamp. This is high-volume append-only telemetry: it is not "
                       + "tenant-scoped, must not be soft-deleted, and does not need real-time push. "
                       + "Archive anything older than one year into cold storage.",
                Assertions = new[]
                {
                    A("does not enable multiTenant", s => Ent(s, "PageView")?.MultiTenant != true),
                    A("does not enable softDelete", s => Ent(s, "PageView")?.SoftDelete != true),
                    A("does not enable realTime", s => Ent(s, "PageView")?.RealTime != true),
                    A("partitioned for cold storage", s => Ent(s, "PageView")?.Partitioned == true),
                    A("archive threshold is 1 year", s => Ent(s, "PageView")?.ArchiveThresholdYears == 1)
                }
            },
            new()
            {
                Id = "hard-modification-preserve",
                Construct = "modification",
                Difficulty = EvalDifficulty.Hard,
                Prompt = "The finance team now wants to track a currency code on products, and they want "
                       + "product lookups by SKU to stay fast. Leave everything else exactly as it is.",
                Assertions = new[]
                {
                    A("Product survives", s => Ent(s, "Product") is not null),
                    A("existing Sku property preserved", s => Prop(Ent(s, "Product"), "Sku") is not null),
                    A("existing Name property preserved", s => Prop(Ent(s, "Product"), "Name") is not null),
                    A("existing softDelete flag preserved", s => Ent(s, "Product")?.SoftDelete == true),
                    A("currency code added", s =>
                        AnyProp(Ent(s, "Product"), "CurrencyCode", "Currency") is not null),
                    A("Sku remains indexed or unique", s =>
                        HasAttr(Prop(Ent(s, "Product"), "Sku"), "Unique")
                        || HasAttr(Prop(Ent(s, "Product"), "Sku"), "Index"))
                }
            },
            new()
            {
                Id = "hard-pii-classification",
                Construct = "security",
                Difficulty = EvalDifficulty.Hard,
                Prompt = "In namespace Health.Intake, create a PatientRecord holding a full name, a national "
                       + "insurance number, a contact email, a card number used for billing, and a free-text "
                       + "notes field. Apply whatever protection our compliance team would expect for each.",
                Assertions = new[]
                {
                    A("national insurance number is encrypted", s =>
                        HasAttr(AnyProp(Ent(s, "PatientRecord"), "NationalInsuranceNumber", "NationalInsuranceNo", "NiNumber", "Nin"), "Encrypt")),
                    A("card number is encrypted or tagged as card PII", s =>
                    {
                        var card = AnyProp(Ent(s, "PatientRecord"), "CardNumber", "PaymentCardNumber");
                        return HasAttr(card, "Encrypt") || HasAttr(card, "PiiCreditCard");
                    }),
                    A("email is masked or tagged as email PII", s =>
                    {
                        var email = AnyProp(Ent(s, "PatientRecord"), "ContactEmail", "Email");
                        return HasAttr(email, "Mask") || HasAttr(email, "PiiEmail");
                    }),
                    A("every protected field still uses a supported scalar type", s =>
                        (s.Entities ?? new()).SelectMany(e => e.Properties ?? new())
                            .All(pr => Vocabulary.IsKnownScalar(pr.Type)
                                       || (s.Enums ?? new()).Any(en => string.Equals(en.Name, pr.Type, StringComparison.OrdinalIgnoreCase))))
                }
            },

            new()
            {
                Id = "modify-existing",
                Construct = "modification",
                Prompt = "Add a DiscountPercent decimal property to the Product entity, and index it.",
                Assertions = new[]
                {
                    A("Product still exists", s => Ent(s, "Product") is not null),
                    A("existing Sku property is preserved", s => Prop(Ent(s, "Product"), "Sku") is not null),
                    A("DiscountPercent was added as a decimal", s => TypeIs(Prop(Ent(s, "Product"), "DiscountPercent"), "decimal")),
                    A("DiscountPercent is indexed", s =>
                        HasAttr(Prop(Ent(s, "Product"), "DiscountPercent"), "Index")
                        || (Ent(s, "Product")?.Indexes ?? new()).Any(i => (i.Fields ?? new()).Contains("DiscountPercent", StringComparer.OrdinalIgnoreCase)))
                }
            }
        };

        /// <summary>
        /// The document handed to the <c>modify-existing</c> case as its starting point.
        /// </summary>
        public static SchemaModel ModificationBaseline => new()
        {
            Namespace = "Shop.Catalog",
            Entities = new List<Entity>
            {
                new()
                {
                    Name = "Product",
                    SoftDelete = true,
                    Properties = new List<Property>
                    {
                        new() { Name = "Id", Type = "ObjectId", IsKey = true },
                        new() { Name = "Sku", Type = "string", Attributes = new List<string> { "Required", "Unique" } },
                        new() { Name = "Name", Type = "string", Attributes = new List<string> { "Required" } }
                    }
                }
            }
        };

        /// <summary>
        /// Runs the suite.
        /// </summary>
        /// <param name="generator">Configured generator pointing at the model under test.</param>
        /// <param name="cases">Cases to run.</param>
        /// <param name="model">Model name, for the report.</param>
        /// <param name="host">Host, for the report.</param>
        /// <param name="runs">
        /// Repetitions per case. Greater than one surfaces variance, which matters because
        /// sampling is stochastic and a single pass can flatter or libel a model.
        /// </param>
        /// <param name="progress">Receives a line per completed case run.</param>
        /// <param name="ct">Cancellation token.</param>
        public static async Task<EvalRunResult> RunAsync(
            AiSchemaGenerator generator,
            IEnumerable<EvalCase> cases,
            string model,
            string host,
            int runs = 1,
            IProgress<string>? progress = null,
            CancellationToken ct = default)
        {
            var results = new List<EvalCaseResult>();
            var caseList = cases.ToList();

            for (var run = 1; run <= Math.Max(1, runs); run++)
            {
                foreach (var testCase in caseList)
                {
                    ct.ThrowIfCancellationRequested();

                    var stopwatch = Stopwatch.StartNew();

                    var baseline = testCase.Construct == "modification" ? ModificationBaseline : null;
                    var generation = await generator.GenerateAsync(testCase.Prompt, baseline, ct);

                    stopwatch.Stop();

                    var assertions = generation.Schema is null
                        ? testCase.Assertions.Select(a => new EvalAssertionResult(a.Description, false)).ToList()
                        : testCase.Assertions.Select(a => new EvalAssertionResult(a.Description, a.Check(generation.Schema))).ToList();

                    var result = new EvalCaseResult
                    {
                        CaseId = testCase.Id,
                        Construct = testCase.Construct,
                        Difficulty = testCase.Difficulty,
                        Run = run,
                        ProducedValidIr = generation.Success,
                        Attempts = generation.Attempts.Count,
                        ElapsedMs = stopwatch.ElapsedMilliseconds,
                        Assertions = assertions,
                        Error = generation.Error
                    };

                    results.Add(result);

                    var passed = assertions.Count(a => a.Passed);

                    // Distinguish "the model modelled the domain wrong" from "the model never
                    // produced valid IR at all". They point at completely different fixes: the
                    // first at the prompt or vocabulary, the second at the validator or the
                    // repair loop's ability to converge.
                    var detail = result.ProducedValidIr
                        ? $"{passed}/{assertions.Count} assertions"
                        : $"INVALID IR after {result.Attempts} attempt(s)";

                    progress?.Report(
                        $"{(result.Passed ? "PASS" : "FAIL")}  {testCase.Id,-26} {testCase.Construct,-14} "
                        + $"{detail,-28} {result.Attempts} attempt(s)  {result.ElapsedMs}ms");
                }
            }

            return new EvalRunResult
            {
                Model = model,
                Host = host,
                Results = results
            };
        }

        /// <summary>
        /// Renders a Markdown report, grouped by construct.
        /// </summary>
        public static string RenderMarkdown(EvalRunResult run)
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Foundry IR authoring eval");
            sb.AppendLine();
            sb.AppendLine($"- Model: `{run.Model}`");
            sb.AppendLine($"- Host: `{run.Host}`");
            sb.AppendLine($"- Started: {run.StartedUtc:u}");
            sb.AppendLine($"- Cases run: {run.Results.Count}");
            sb.AppendLine($"- **Pass rate: {run.PassRate:P0}**");
            sb.AppendLine($"- Valid IR rate: {run.ValidIrRate:P0}");
            sb.AppendLine();

            sb.AppendLine("## By difficulty");
            sb.AppendLine();
            sb.AppendLine("| Band | Pass | Runs | Rate |");
            sb.AppendLine("| :--- | ---: | ---: | ---: |");
            foreach (var band in run.Results.GroupBy(r => r.Difficulty).OrderBy(g => g.Key))
            {
                var passed = band.Count(r => r.Passed);
                sb.AppendLine($"| {band.Key} | {passed} | {band.Count()} | {(double)passed / band.Count():P0} |");
            }
            sb.AppendLine();

            sb.AppendLine("## By construct");
            sb.AppendLine();
            sb.AppendLine("| Construct | Pass | Runs | Rate |");
            sb.AppendLine("| :--- | ---: | ---: | ---: |");

            foreach (var group in run.Results.GroupBy(r => r.Construct).OrderBy(g => g.Count(r => r.Passed) / (double)g.Count()))
            {
                var passed = group.Count(r => r.Passed);
                sb.AppendLine($"| {group.Key} | {passed} | {group.Count()} | {(double)passed / group.Count():P0} |");
            }

            sb.AppendLine();
            sb.AppendLine("## Failing assertions");
            sb.AppendLine();

            var failures = run.Results
                .Where(r => !r.Passed)
                .SelectMany(r => r.Assertions.Where(a => !a.Passed).Select(a => (r.CaseId, r.Construct, a.Description)))
                .GroupBy(f => (f.CaseId, f.Construct, f.Description))
                .OrderByDescending(g => g.Count())
                .ToList();

            if (failures.Count == 0)
            {
                sb.AppendLine("None.");
            }
            else
            {
                sb.AppendLine("| Count | Case | Construct | Expectation not met |");
                sb.AppendLine("| ---: | :--- | :--- | :--- |");
                foreach (var failure in failures)
                    sb.AppendLine($"| {failure.Count()} | {failure.Key.CaseId} | {failure.Key.Construct} | {failure.Key.Description} |");
            }

            sb.AppendLine();
            sb.AppendLine("## Cases");
            sb.AppendLine();
            sb.AppendLine("| Case | Construct | Result | Assertions | Attempts | ms |");
            sb.AppendLine("| :--- | :--- | :--- | ---: | ---: | ---: |");
            foreach (var result in run.Results.OrderBy(r => r.Construct).ThenBy(r => r.CaseId))
            {
                var passed = result.Assertions.Count(a => a.Passed);
                sb.AppendLine(
                    $"| {result.CaseId} | {result.Construct} | {(result.Passed ? "pass" : "**fail**")} "
                    + $"| {passed}/{result.Assertions.Count} | {result.Attempts} | {result.ElapsedMs} |");
            }

            return sb.ToString();
        }

        /// <summary>Serialises the run for machine comparison across releases.</summary>
        public static string RenderJson(EvalRunResult run)
            => JsonSerializer.Serialize(run, new JsonSerializerOptions { WriteIndented = true });
    }
}
