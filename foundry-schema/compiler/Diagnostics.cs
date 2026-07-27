using System;
using System.Collections.Generic;
using System.Linq;

namespace Foundry.Schema.Compiler
{
    /// <summary>
    /// Severity of a Foundry schema diagnostic.
    /// </summary>
    public enum DiagnosticSeverity
    {
        /// <summary>Advisory. Compilation proceeds.</summary>
        Info,

        /// <summary>Suspicious but compilable. Compilation proceeds.</summary>
        Warning,

        /// <summary>Compilation must not proceed; generated code would be wrong or unbuildable.</summary>
        Error
    }

    /// <summary>
    /// A single schema diagnostic.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Diagnostics are the shared feedback channel between the compiler, the CLI
    /// (<c>foundry validate</c>), the LSP server, and the AI repair loop. Codes are
    /// stable and must never be reused for a different meaning, because the AI skill
    /// spec documents them and models are prompted with them verbatim.
    /// </para>
    /// <para>
    /// <see cref="Hint"/> exists specifically for the AI repair loop: it states the
    /// corrective action in terms of the IR document, not in terms of C#.
    /// </para>
    /// </remarks>
    public sealed record Diagnostic
    {
        /// <summary>Stable diagnostic code, e.g. <c>FDY1001</c>.</summary>
        public string Code { get; init; } = string.Empty;

        /// <summary>Severity of the diagnostic.</summary>
        public DiagnosticSeverity Severity { get; init; } = DiagnosticSeverity.Error;

        /// <summary>Human-readable description of what is wrong.</summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>
        /// JSON-pointer-style location in the IR document, e.g.
        /// <c>/entities/2/properties/0/name</c>. Empty when document-scoped.
        /// </summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>
        /// Corrective action phrased as an edit to the IR document. Consumed by the
        /// AI repair loop and shown as an LSP code-action title.
        /// </summary>
        public string Hint { get; init; } = string.Empty;

        /// <inheritdoc />
        public override string ToString()
        {
            var location = string.IsNullOrEmpty(Path) ? "" : $" at {Path}";
            var severity = Severity switch
            {
                DiagnosticSeverity.Error => "error",
                DiagnosticSeverity.Warning => "warning",
                _ => "info"
            };
            return $"{severity} {Code}{location}: {Message}";
        }
    }

    /// <summary>
    /// Accumulates diagnostics during validation and code generation.
    /// </summary>
    public sealed class DiagnosticBag
    {
        private readonly List<Diagnostic> _items = new();

        /// <summary>All diagnostics, in the order they were reported.</summary>
        public IReadOnlyList<Diagnostic> Items => _items;

        /// <summary>True when at least one <see cref="DiagnosticSeverity.Error"/> was reported.</summary>
        public bool HasErrors => _items.Any(d => d.Severity == DiagnosticSeverity.Error);

        /// <summary>Number of errors reported.</summary>
        public int ErrorCount => _items.Count(d => d.Severity == DiagnosticSeverity.Error);

        /// <summary>Number of warnings reported.</summary>
        public int WarningCount => _items.Count(d => d.Severity == DiagnosticSeverity.Warning);

        /// <summary>Reports a diagnostic.</summary>
        public void Report(string code, DiagnosticSeverity severity, string message, string path = "", string hint = "")
        {
            _items.Add(new Diagnostic
            {
                Code = code,
                Severity = severity,
                Message = message,
                Path = path,
                Hint = hint
            });
        }

        /// <summary>Reports an error-severity diagnostic.</summary>
        public void Error(string code, string message, string path = "", string hint = "")
            => Report(code, DiagnosticSeverity.Error, message, path, hint);

        /// <summary>Reports a warning-severity diagnostic.</summary>
        public void Warning(string code, string message, string path = "", string hint = "")
            => Report(code, DiagnosticSeverity.Warning, message, path, hint);

        /// <summary>Reports an info-severity diagnostic.</summary>
        public void Info(string code, string message, string path = "", string hint = "")
            => Report(code, DiagnosticSeverity.Info, message, path, hint);

        /// <summary>Merges another bag's diagnostics into this one.</summary>
        public void AddRange(IEnumerable<Diagnostic> diagnostics) => _items.AddRange(diagnostics);

        /// <summary>
        /// Renders the diagnostics as compact text suitable for a terminal or for
        /// feeding back into a model during the repair loop.
        /// </summary>
        public string Render()
        {
            if (_items.Count == 0) return "No diagnostics.";
            return string.Join("\n", _items.Select(d => d.ToString() + (string.IsNullOrEmpty(d.Hint) ? "" : $"\n    hint: {d.Hint}")));
        }
    }

    /// <summary>
    /// The canonical catalog of Foundry diagnostic codes.
    /// </summary>
    /// <remarks>
    /// Ranges: <c>FDY1xxx</c> structural (shape of the document), <c>FDY2xxx</c>
    /// semantic (cross-reference integrity), <c>FDY3xxx</c> configuration coherence,
    /// <c>FDY4xxx</c> naming and identifier safety.
    /// <para>
    /// This catalog is emitted verbatim into the AI skill bundle by
    /// <c>foundry ai-spec</c>, so every code added here must have a description.
    /// </para>
    /// </remarks>
    public static class DiagnosticCatalog
    {
        // ---- FDY1xxx: structural ----

        /// <summary>The IR document has no namespace.</summary>
        public const string MissingNamespace = "FDY1001";

        /// <summary>The IR document declares no entities.</summary>
        public const string NoEntities = "FDY1002";

        /// <summary>An entity has no name.</summary>
        public const string EntityMissingName = "FDY1003";

        /// <summary>An entity declares no properties.</summary>
        public const string EntityNoProperties = "FDY1004";

        /// <summary>An entity has no property marked <c>isKey</c>.</summary>
        public const string EntityNoKey = "FDY1005";

        /// <summary>An entity has more than one property marked <c>isKey</c>.</summary>
        public const string EntityMultipleKeys = "FDY1006";

        /// <summary>A property has no name.</summary>
        public const string PropertyMissingName = "FDY1007";

        /// <summary>A property has no type.</summary>
        public const string PropertyMissingType = "FDY1008";

        /// <summary>An enum declares no values.</summary>
        public const string EnumNoValues = "FDY1009";

        /// <summary>The IR document uses the Studio canvas format instead of the normative IR format.</summary>
        public const string CanvasFormatNotIr = "FDY1010";

        /// <summary>
        /// The key property's type is not <c>objectid</c>. <c>IRepository&lt;T&gt;</c> is constrained to
        /// <c>IEntity&lt;ObjectId&gt;</c>, so an entity keyed on anything else compiles but can never be
        /// persisted, queried or served — no repository can be resolved for it.
        /// </summary>
        public const string EntityUnsupportedKeyType = "FDY1011";

        // ---- FDY2xxx: semantic / cross-reference ----

        /// <summary>Two entities share a name.</summary>
        public const string DuplicateEntityName = "FDY2001";

        /// <summary>Two properties on one entity share a name.</summary>
        public const string DuplicatePropertyName = "FDY2002";

        /// <summary>A workflow transition references a state that is not declared.</summary>
        public const string WorkflowUnknownState = "FDY2003";

        /// <summary>A workflow references an entity that is not declared.</summary>
        public const string WorkflowUnknownEntity = "FDY2004";

        /// <summary>A workflow has no initial state.</summary>
        public const string WorkflowNoInitialState = "FDY2005";

        /// <summary>A workflow has no final state.</summary>
        public const string WorkflowNoFinalState = "FDY2006";

        /// <summary>An index references a property that is not declared on the entity.</summary>
        public const string IndexUnknownProperty = "FDY2007";

        /// <summary>A property is typed as an enum that is not declared.</summary>
        public const string UnknownEnumType = "FDY2008";

        /// <summary>A custom endpoint targets an entity that is not declared.</summary>
        public const string EndpointUnknownEntity = "FDY2009";

        /// <summary>A DTO property references a source entity that is not declared.</summary>
        public const string DtoUnknownSourceEntity = "FDY2010";

        /// <summary>A DTO property references a source property not declared on its source entity.</summary>
        public const string DtoUnknownSourceProperty = "FDY2011";

        /// <summary>A workflow transition references a choice node that is not declared.</summary>
        public const string WorkflowUnknownChoiceNode = "FDY2012";

        /// <summary>Two workflow transitions share a trigger name, which would generate duplicate command types.</summary>
        public const string DuplicateTransitionTrigger = "FDY2013";

        /// <summary>
        /// An entity, enum and/or DTO share a name, so one silently overwrites the other on emit.
        /// </summary>
        public const string DuplicateTypeName = "FDY2014";

        // ---- FDY3xxx: configuration coherence ----

        /// <summary>A property is marked <c>isTenantKey</c> but the entity is not multi-tenant.</summary>
        public const string TenantKeyWithoutMultiTenant = "FDY3001";

        /// <summary>An entity is multi-tenant but declares no tenant key.</summary>
        public const string MultiTenantWithoutTenantKey = "FDY3002";

        /// <summary>A Kafka topic is set but the outbox is not enabled.</summary>
        public const string KafkaTopicWithoutOutbox = "FDY3003";

        /// <summary>FileIO allowed extensions are set but FileIO is not enabled.</summary>
        public const string FileIoExtensionsWithoutFileIo = "FDY3004";

        /// <summary>An unrecognised property attribute was found and will be ignored.</summary>
        public const string UnknownAttribute = "FDY3005";

        /// <summary>An unrecognised property type was found and will be emitted verbatim.</summary>
        public const string UnknownType = "FDY3006";

        /// <summary>A partitioned entity has a non-positive archive threshold.</summary>
        public const string InvalidArchiveThreshold = "FDY3007";

        /// <summary>A custom endpoint declares an unsupported HTTP method.</summary>
        public const string InvalidHttpMethod = "FDY3008";

        /// <summary>A custom endpoint route does not begin with a forward slash.</summary>
        public const string InvalidRoute = "FDY3009";

        /// <summary>An enum is declared but no property is typed with it.</summary>
        public const string UnusedEnum = "FDY3010";

        /// <summary>The tenant key is not named <c>TenantId</c>.</summary>
        public const string TenantKeyMustBeNamedTenantId = "FDY3011";

        /// <summary>A property is marked <c>isOwnerKey</c> but the entity is not owner-scoped.</summary>
        public const string OwnerKeyWithoutOwnerScoped = "FDY3012";

        /// <summary>An entity is owner-scoped but declares no owner key.</summary>
        public const string OwnerScopedWithoutOwnerKey = "FDY3013";

        /// <summary>The owner key is not named <c>OwnerId</c>.</summary>
        public const string OwnerKeyMustBeNamedOwnerId = "FDY3014";

        /// <summary>Exempt roles are listed but the entity is not owner-scoped.</summary>
        public const string OwnerExemptRolesWithoutOwnerScoped = "FDY3015";

        // ---- FDY4xxx: naming and identifier safety ----

        /// <summary>A name is not a valid C# identifier and cannot be emitted as code.</summary>
        public const string InvalidIdentifier = "FDY4001";

        /// <summary>A name is a reserved C# keyword.</summary>
        public const string ReservedKeyword = "FDY4002";

        /// <summary>A namespace is not a valid dotted C# namespace.</summary>
        public const string InvalidNamespace = "FDY4003";

        /// <summary>An attribute argument contains characters that cannot be safely emitted.</summary>
        public const string UnsafeAttributeArgument = "FDY4004";

        /// <summary>
        /// Warnings worth sending back to a model for a second attempt.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The repair loop exists to reach a *valid* document, so it is driven by errors. These
        /// codes are the exception: each describes a document that compiles but almost certainly
        /// does not say what the author meant, and each has a fix the diagnostic can state
        /// precisely — "type this property with the enum you declared", "set enableKafkaOutbox
        /// since you named a topic".
        /// </para>
        /// <para>
        /// Membership is deliberately narrow. A warning that cannot be acted on mechanically
        /// would make the loop spend an attempt achieving nothing, and warnings never justify
        /// returning a worse document than one already in hand.
        /// </para>
        /// </remarks>
        public static readonly IReadOnlySet<string> RepairableWarnings = new HashSet<string>(StringComparer.Ordinal)
        {
            UnusedEnum,                      // declared an enum, then typed the property as a scalar
            KafkaTopicWithoutOutbox,         // named a topic without enabling the outbox
            FileIoExtensionsWithoutFileIo,   // listed extensions without enabling FileIO
            TenantKeyWithoutMultiTenant,     // marked a tenant key without enabling multi-tenancy
            OwnerKeyWithoutOwnerScoped,      // marked an owner key without enabling owner scoping
            OwnerExemptRolesWithoutOwnerScoped, // listed exempt roles with nothing to be exempt from
            WorkflowNoFinalState,            // a workflow instances can never leave
            UnknownAttribute                 // the hint lists the supported vocabulary
        };

        /// <summary>
        /// Human-readable descriptions for every code, keyed by code.
        /// Emitted into the AI skill bundle.
        /// </summary>
        public static readonly IReadOnlyDictionary<string, string> Descriptions = new Dictionary<string, string>
        {
            [MissingNamespace] = "The IR document must set a non-empty 'namespace'.",
            [NoEntities] = "The IR document declares no entities; nothing would be generated.",
            [EntityMissingName] = "Every entity must have a non-empty 'name'.",
            [EntityNoProperties] = "An entity with no properties generates an empty record.",
            [EntityNoKey] = "Every entity must have exactly one property with 'isKey': true.",
            [EntityMultipleKeys] = "An entity must not declare more than one 'isKey' property.",
            [PropertyMissingName] = "Every property must have a non-empty 'name'.",
            [PropertyMissingType] = "Every property must have a non-empty 'type'.",
            [EnumNoValues] = "An enum must declare at least one value.",
            [CanvasFormatNotIr] = "This document is in Studio canvas format ('nodes'/'edges'). The compiler consumes the normative IR format ('entities'/'enums'/'dtos'). Convert it with 'foundry migrate'.",
            [EntityUnsupportedKeyType] = "The key property must be of type 'objectid'. The MongoDB data layer constrains IRepository<T> to IEntity<ObjectId>, so an entity keyed on any other type generates code that compiles but has no resolvable repository -- it can never be persisted or served.",
            [DuplicateEntityName] = "Entity names must be unique; duplicates would generate colliding C# types.",
            [DuplicatePropertyName] = "Property names must be unique within an entity.",
            [WorkflowUnknownState] = "A workflow transition references a state not present in the workflow's 'states'.",
            [WorkflowUnknownEntity] = "A workflow's 'entity' must name a declared entity.",
            [WorkflowNoInitialState] = "A workflow must mark exactly one state 'isInitial'.",
            [WorkflowNoFinalState] = "A workflow should mark at least one state 'isFinal' or it can never complete.",
            [IndexUnknownProperty] = "An index field must name a property declared on the same entity.",
            [UnknownEnumType] = "A property marked 'isEnum' must have a type matching a declared enum name.",
            [EndpointUnknownEntity] = "A custom endpoint's 'targetEntity' must name a declared entity.",
            [DtoUnknownSourceEntity] = "A DTO property's 'sourceEntity' must name a declared entity.",
            [DtoUnknownSourceProperty] = "A DTO property's 'sourceProperty' must name a property on its 'sourceEntity'.",
            [WorkflowUnknownChoiceNode] = "A transition target must be a declared state or choice node.",
            [DuplicateTransitionTrigger] = "Transition 'trigger' names must be unique; duplicates generate colliding command types.",
            [DuplicateTypeName] = "An entity, enum and DTO all become C# types in one namespace and are written to one file per name, so their names must not collide. A collision silently discards one of them.",
            [TenantKeyWithoutMultiTenant] = "A property marked 'isTenantKey' requires the entity to set 'multiTenant': true.",
            [MultiTenantWithoutTenantKey] = "An entity with 'multiTenant': true must mark one property 'isTenantKey' or set 'tenantProperty'.",
            [TenantKeyMustBeNamedTenantId] = "The tenant key property must be named 'TenantId'. The data layer builds its tenant filter against the stored field by that name, so any other name compiles to an entity that does not satisfy IMultiTenant -- and, if it did, would filter on a field no document has.",
            [OwnerKeyWithoutOwnerScoped] = "A property marked 'isOwnerKey' requires the entity to set 'ownerScoped': true.",
            [OwnerScopedWithoutOwnerKey] = "An entity with 'ownerScoped': true must mark one property 'isOwnerKey'.",
            [OwnerKeyMustBeNamedOwnerId] = "The owner key property must be named 'OwnerId', for the same reason the tenant key must be named 'TenantId': the data layer filters on the stored field by name.",
            [OwnerExemptRolesWithoutOwnerScoped] = "Setting 'ownerExemptRoles' without 'ownerScoped': true has no effect; there is no owner filter for those roles to be exempt from.",
            [KafkaTopicWithoutOutbox] = "Setting 'kafkaTopic' without 'enableKafkaOutbox': true has no effect.",
            [FileIoExtensionsWithoutFileIo] = "Setting 'fileIOAllowedExtensions' without 'enableFileIO': true has no effect.",
            [UnknownAttribute] = "The attribute is not in the supported vocabulary and will be ignored.",
            [UnknownType] = "The type is not in the supported vocabulary and will be emitted verbatim as a C# type name.",
            [InvalidArchiveThreshold] = "A partitioned entity's 'archiveThresholdYears' must be greater than zero.",
            [InvalidHttpMethod] = "A custom endpoint 'method' must be one of GET, POST, PUT, PATCH, DELETE.",
            [InvalidRoute] = "A custom endpoint 'route' must begin with '/'.",
            [UnusedEnum] = "An enum is declared but no property is typed with it. Usually the property that should use it was left as a plain scalar, which loses type safety and leaves the enum dead.",
            [InvalidIdentifier] = "The name cannot be emitted as C#. Use letters, digits and underscores, starting with a letter or underscore.",
            [ReservedKeyword] = "The name is a reserved C# keyword. Choose a different name.",
            [InvalidNamespace] = "The namespace must be dot-separated valid C# identifiers, e.g. 'Acme.Billing'.",
            [UnsafeAttributeArgument] = "The attribute argument contains quote, backslash or brace characters that cannot be safely emitted."
        };
    }
}
