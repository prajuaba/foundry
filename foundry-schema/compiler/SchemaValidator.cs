using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace Foundry.Schema.Compiler
{
    /// <summary>
    /// Validates a Foundry IR document and reports coded diagnostics.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the gate in front of code generation. Nothing should be emitted from a document
    /// that produces errors: the emitter composes C# by string concatenation, so an
    /// unvalidated document yields either unbuildable output or, worse, output that builds and
    /// is silently wrong (a dropped tenant filter, an index that was never created).
    /// </para>
    /// <para>
    /// The same diagnostics drive four consumers, which is why they carry stable codes and
    /// IR-relative paths rather than prose: the compiler, <c>foundry validate</c>, the LSP
    /// server's squiggles, and the AI repair loop.
    /// </para>
    /// </remarks>
    public static class SchemaValidator
    {
        /// <summary>
        /// Inspects the raw document text before deserialisation.
        /// </summary>
        /// <remarks>
        /// Deserialising into <see cref="SchemaModel"/> silently discards unknown properties.
        /// A Studio canvas file — which stores <c>nodes</c> and <c>edges</c> rather than
        /// <c>entities</c> — therefore produces an empty but structurally valid model, and the
        /// compiler used to report success while emitting nothing at all. This check exists to
        /// turn that silent no-op into an actionable error.
        /// </remarks>
        public static void ValidateRawDocument(string json, DiagnosticBag bag)
        {
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(json);
            }
            catch (JsonException)
            {
                // Malformed JSON is reported by the caller's deserialisation attempt.
                return;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return;

                var hasNodes = root.TryGetProperty("nodes", out _);
                var hasEntities = HasNonEmptyArray(root, "entities");

                if (hasNodes && !hasEntities)
                {
                    bag.Error(
                        DiagnosticCatalog.CanvasFormatNotIr,
                        "Document is in Studio canvas format ('nodes'), which the compiler does not consume. No code would be generated.",
                        "/nodes",
                        "Convert the document to normative IR form with 'foundry migrate <file>', or export from Studio using 'Save IR' rather than the raw canvas file.");
                }
            }
        }

        private static bool HasNonEmptyArray(JsonElement root, string name)
            => root.TryGetProperty(name, out var value)
               && value.ValueKind == JsonValueKind.Array
               && value.GetArrayLength() > 0;

        /// <summary>
        /// Validates a deserialised IR document.
        /// </summary>
        /// <param name="schema">The document to validate.</param>
        /// <returns>A bag containing every diagnostic found.</returns>
        public static DiagnosticBag Validate(SchemaModel? schema)
        {
            var bag = new DiagnosticBag();
            if (schema is null)
            {
                bag.Error(DiagnosticCatalog.MissingNamespace, "The IR document could not be read.");
                return bag;
            }

            ValidateNamespace(schema, bag);

            var entities = schema.Entities ?? new List<Entity>();
            var enums = schema.Enums ?? new List<Enum>();

            if (entities.Count == 0)
            {
                bag.Error(
                    DiagnosticCatalog.NoEntities,
                    "The document declares no entities.",
                    "/entities",
                    "Add at least one entity with a name, one 'isKey' property, and any further properties.");
            }

            var entityNames = new HashSet<string>(
                entities.Where(e => !string.IsNullOrWhiteSpace(e.Name)).Select(e => e.Name),
                StringComparer.OrdinalIgnoreCase);

            var enumNames = new HashSet<string>(
                enums.Where(e => !string.IsNullOrWhiteSpace(e.Name)).Select(e => e.Name),
                StringComparer.OrdinalIgnoreCase);

            ValidateDuplicateEntityNames(entities, bag);
            ValidateTypeNameCollisions(schema, bag);

            for (var i = 0; i < entities.Count; i++)
                ValidateEntity(entities[i], i, enumNames, bag);

            for (var i = 0; i < enums.Count; i++)
                ValidateEnum(enums[i], i, bag);

            ValidateEnumUsage(schema, enums, bag);
            ValidateDtos(schema, entities, bag);
            ValidateCustomEndpoints(schema, entityNames, bag);
            ValidateWorkflows(schema, entityNames, bag);
            ValidateConnectors(schema, bag);

            return bag;
        }

        private static void ValidateNamespace(SchemaModel schema, DiagnosticBag bag)
        {
            if (string.IsNullOrWhiteSpace(schema.Namespace))
            {
                bag.Error(
                    DiagnosticCatalog.MissingNamespace,
                    "The document has no namespace.",
                    "/namespace",
                    "Set 'namespace' to a dotted C# namespace such as 'Acme.Billing'.");
                return;
            }

            if (!CodeGen.IsValidNamespace(schema.Namespace))
            {
                bag.Error(
                    DiagnosticCatalog.InvalidNamespace,
                    $"'{schema.Namespace}' is not a valid C# namespace.",
                    "/namespace",
                    "Use dot-separated identifiers made of letters, digits and underscores, each starting with a letter or underscore.");
            }
        }

        private static void ValidateDuplicateEntityNames(List<Entity> entities, DiagnosticBag bag)
        {
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < entities.Count; i++)
            {
                var name = entities[i].Name;
                if (string.IsNullOrWhiteSpace(name)) continue;

                if (seen.TryGetValue(name, out var firstIndex))
                {
                    bag.Error(
                        DiagnosticCatalog.DuplicateEntityName,
                        $"Entity '{name}' is declared more than once (first at /entities/{firstIndex}).",
                        $"/entities/{i}/name",
                        "Rename one of the entities. Duplicate names generate colliding C# types.");
                }
                else
                {
                    seen[name] = i;
                }
            }
        }

        /// <summary>
        /// Detects a name shared by any two of entities, enums and DTOs.
        /// </summary>
        /// <remarks>
        /// All three emit a C# type into the same namespace, and the generator keys its output map
        /// by name — so a collision does not produce a compiler error, it silently discards one
        /// declaration and writes the survivor to the shared file name. That is worse than a build
        /// break: a document declaring an enum and an entity both called 'OrderStatus' compiles
        /// "successfully" with the enum gone and every reference to it now pointing at a record.
        /// </remarks>
        private static void ValidateTypeNameCollisions(SchemaModel schema, DiagnosticBag bag)
        {
            var declarations = new List<(string Name, string Kind, string Path)>();

            var entities = schema.Entities ?? new List<Entity>();
            for (var i = 0; i < entities.Count; i++)
                if (!string.IsNullOrWhiteSpace(entities[i].Name))
                    declarations.Add((entities[i].Name, "entity", $"/entities/{i}/name"));

            var enums = schema.Enums ?? new List<Enum>();
            for (var i = 0; i < enums.Count; i++)
                if (!string.IsNullOrWhiteSpace(enums[i].Name))
                    declarations.Add((enums[i].Name, "enum", $"/enums/{i}/name"));

            var dtos = schema.Dtos ?? new List<DtoModel>();
            for (var i = 0; i < dtos.Count; i++)
                if (!string.IsNullOrWhiteSpace(dtos[i].Name))
                    declarations.Add((dtos[i].Name, "DTO", $"/dtos/{i}/name"));

            foreach (var group in declarations.GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                var members = group.ToList();
                if (members.Count < 2) continue;

                // Entity-vs-entity duplicates are already reported by FDY2001; only report here
                // when the collision spans different kinds or involves enums/DTOs.
                if (members.All(m => m.Kind == "entity")) continue;

                var kinds = string.Join(" and ", members.Select(m => m.Kind).Distinct());

                foreach (var member in members.Skip(1))
                {
                    bag.Error(
                        DiagnosticCatalog.DuplicateTypeName,
                        $"'{group.Key}' is declared as {kinds}. They emit into one namespace and one file, "
                        + "so one would silently overwrite the other.",
                        member.Path,
                        $"Rename one of them, e.g. '{group.Key}' for the enum and '{group.Key}Record' for the entity. "
                        + "If you meant only the enum, remove the entity entirely.");
                }
            }
        }

        private static void ValidateEntity(Entity entity, int index, HashSet<string> enumNames, DiagnosticBag bag)
        {
            var path = $"/entities/{index}";

            if (string.IsNullOrWhiteSpace(entity.Name))
            {
                bag.Error(
                    DiagnosticCatalog.EntityMissingName,
                    "Entity has no name.",
                    $"{path}/name",
                    "Give the entity a PascalCase singular name such as 'Order'.");
            }
            else
            {
                ValidateIdentifier(entity.Name, $"{path}/name", "Entity name", bag);
            }

            var properties = entity.Properties ?? new List<Property>();

            if (properties.Count == 0)
            {
                bag.Warning(
                    DiagnosticCatalog.EntityNoProperties,
                    $"Entity '{entity.Name}' declares no properties.",
                    $"{path}/properties",
                    "Add at least an 'isKey' property.");
            }

            ValidateEntityKey(entity, properties, path, bag);
            ValidateProperties(entity, properties, path, enumNames, bag);
            ValidateEntityIndexes(entity, properties, path, bag);
            ValidateEntityFeatureCoherence(entity, properties, path, bag);
        }

        private static void ValidateEntityKey(Entity entity, List<Property> properties, string path, DiagnosticBag bag)
        {
            var keys = properties.Where(p => p.IsKey).ToList();

            if (keys.Count == 0)
            {
                bag.Error(
                    DiagnosticCatalog.EntityNoKey,
                    $"Entity '{entity.Name}' has no property marked 'isKey'.",
                    $"{path}/properties",
                    "Add a property such as { \"name\": \"Id\", \"type\": \"ObjectId\", \"isKey\": true }.");
            }
            else if (keys.Count > 1)
            {
                bag.Error(
                    DiagnosticCatalog.EntityMultipleKeys,
                    $"Entity '{entity.Name}' marks {keys.Count} properties as 'isKey'.",
                    $"{path}/properties",
                    "Exactly one property may set 'isKey': true.");
            }
            else if (!Vocabulary.KeyTypes.Contains(keys[0].Type ?? string.Empty))
            {
                // An error, not a warning. This was previously a warning saying the type was "not
                // recommended", which understated it: the MongoDB data layer constrains
                // IRepository<T> to IEntity<ObjectId>, so a differently-keyed entity generates code
                // that compiles and then has no repository to resolve at runtime. The scaffolded
                // project shipped with a string key and could not serve a single request.
                bag.Error(
                    DiagnosticCatalog.EntityUnsupportedKeyType,
                    $"Entity '{entity.Name}' has key property '{keys[0].Name}' of type '{keys[0].Type}', "
                        + "which the MongoDB data layer cannot serve.",
                    $"{path}/properties/{properties.IndexOf(keys[0])}/type",
                    $"Change the key property's \"type\" to \"ObjectId\".");
            }
        }

        private static void ValidateProperties(
            Entity entity,
            List<Property> properties,
            string path,
            HashSet<string> enumNames,
            DiagnosticBag bag)
        {
            var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < properties.Count; i++)
            {
                var prop = properties[i];
                var propPath = $"{path}/properties/{i}";

                if (string.IsNullOrWhiteSpace(prop.Name))
                {
                    bag.Error(
                        DiagnosticCatalog.PropertyMissingName,
                        $"Property {i} on '{entity.Name}' has no name.",
                        $"{propPath}/name",
                        "Give the property a PascalCase name.");
                }
                else
                {
                    ValidateIdentifier(prop.Name, $"{propPath}/name", "Property name", bag);

                    if (seen.TryGetValue(prop.Name, out var firstIndex))
                    {
                        bag.Error(
                            DiagnosticCatalog.DuplicatePropertyName,
                            $"Property '{prop.Name}' is declared more than once on '{entity.Name}' (first at index {firstIndex}).",
                            $"{propPath}/name",
                            "Rename one of the properties.");
                    }
                    else
                    {
                        seen[prop.Name] = i;
                    }
                }

                ValidatePropertyType(entity, prop, propPath, enumNames, bag);
                ValidatePropertyAttributes(entity, prop, propPath, bag);
            }
        }

        private static void ValidatePropertyType(
            Entity entity,
            Property prop,
            string propPath,
            HashSet<string> enumNames,
            DiagnosticBag bag)
        {
            if (string.IsNullOrWhiteSpace(prop.Type))
            {
                bag.Error(
                    DiagnosticCatalog.PropertyMissingType,
                    $"Property '{prop.Name}' on '{entity.Name}' has no type.",
                    $"{propPath}/type",
                    $"Set 'type' to one of: {string.Join(", ", Vocabulary.ScalarTypes.Keys)}, or the name of a declared enum.");
                return;
            }

            if (prop.IsEnum)
            {
                if (enumNames.Contains(prop.Type)) return;

                // Distinguish "marked as an enum but typed as a scalar" from "names an enum that
                // does not exist". Both used to produce the same message, which for a scalar type
                // read as "declare an enum named 'string'" — advice that cannot be followed, so an
                // AI repair loop burns every attempt without converging.
                if (Vocabulary.IsKnownScalar(prop.Type))
                {
                    var hint = enumNames.Count > 0
                        ? $"Either set 'type' to one of the declared enums ({string.Join(", ", enumNames)}), "
                          + "or set \"isEnum\": false to keep it a plain scalar."
                        : "Either declare an enum in 'enums' and set 'type' to its name, "
                          + "or set \"isEnum\": false to keep it a plain scalar.";

                    bag.Error(
                        DiagnosticCatalog.UnknownEnumType,
                        $"Property '{prop.Name}' is marked \"isEnum\": true but its type is the scalar '{prop.Type}'.",
                        $"{propPath}/type",
                        hint);
                    return;
                }

                bag.Error(
                    DiagnosticCatalog.UnknownEnumType,
                    $"Property '{prop.Name}' is marked 'isEnum' but no enum named '{prop.Type}' is declared.",
                    $"{propPath}/type",
                    enumNames.Count > 0
                        ? $"Declare an enum named '{prop.Type}' in 'enums', set 'type' to one of the declared enums "
                          + $"({string.Join(", ", enumNames)}), or set \"isEnum\": false."
                        : $"Declare an enum named '{prop.Type}' in 'enums', or set \"isEnum\": false.");
                return;
            }

            if (!Vocabulary.IsKnownScalar(prop.Type) && !enumNames.Contains(prop.Type))
            {
                // An error, not a warning: the emitter passes the type through verbatim, so an
                // unrecognised name becomes an unresolvable C# type and the generated project does
                // not build. A local model reaches for invented types readily — 'EncryptedString',
                // 'MaskedString' — when the intent belongs in 'attributes' instead, and a warning
                // let that through to a broken build.
                bag.Error(
                    DiagnosticCatalog.UnknownType,
                    $"Property '{prop.Name}' has unrecognised type '{prop.Type}', which would emit an unresolvable C# type.",
                    $"{propPath}/type",
                    $"Use one of: {string.Join(", ", Vocabulary.ScalarTypes.Keys)}. "
                    + "For encryption or masking use \"type\": \"string\" plus attributes such as "
                    + "[\"Encrypt\"] or [\"Mask\"]. To use an enum, declare it in 'enums' and set \"isEnum\": true.");
            }
        }

        private static void ValidatePropertyAttributes(Entity entity, Property prop, string propPath, DiagnosticBag bag)
        {
            var attributes = prop.Attributes ?? new List<string>();

            for (var i = 0; i < attributes.Count; i++)
            {
                var attr = attributes[i];
                var attrPath = $"{propPath}/attributes/{i}";

                if (!Vocabulary.TryResolveAttribute(attr, out var spec))
                {
                    bag.Warning(
                        DiagnosticCatalog.UnknownAttribute,
                        $"Attribute '{attr}' on '{entity.Name}.{prop.Name}' is not supported and will be ignored.",
                        attrPath,
                        $"Use one of: {string.Join(", ", Vocabulary.AttributeNames)}.");
                    continue;
                }

                // Guard the emitter: a parameterised attribute's argument list is spliced into
                // generated C#, so anything that is not a plain number, boolean or simple
                // quoted string must be rejected rather than passed through.
                if (!CodeGen.TryParseAttribute(attr, out _, out var args))
                {
                    bag.Error(
                        DiagnosticCatalog.UnsafeAttributeArgument,
                        $"Attribute '{attr}' on '{entity.Name}.{prop.Name}' cannot be safely emitted as C#.",
                        attrPath,
                        $"Write it as {spec!.Example}. Arguments may only be numbers, booleans, or double-quoted strings without quotes, backslashes or braces.");
                    continue;
                }

                if (spec!.Arity == AttributeArity.Parameterised && string.IsNullOrEmpty(args))
                {
                    bag.Error(
                        DiagnosticCatalog.UnknownAttribute,
                        $"Attribute '{spec.Name}' requires arguments.",
                        attrPath,
                        $"Write it as {spec.Example}.");
                }
                else if (spec.Arity == AttributeArity.Bare && !string.IsNullOrEmpty(args))
                {
                    bag.Error(
                        DiagnosticCatalog.UnknownAttribute,
                        $"Attribute '{spec.Name}' does not take arguments.",
                        attrPath,
                        $"Write it as {spec.Example}.");
                }
            }
        }

        private static void ValidateEntityIndexes(Entity entity, List<Property> properties, string path, DiagnosticBag bag)
        {
            var indexes = entity.Indexes ?? new List<Index>();
            var propertyNames = new HashSet<string>(
                properties.Where(p => !string.IsNullOrWhiteSpace(p.Name)).Select(p => p.Name),
                StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < indexes.Count; i++)
            {
                var fields = indexes[i].Fields ?? new List<string>();
                for (var f = 0; f < fields.Count; f++)
                {
                    if (!propertyNames.Contains(fields[f]))
                    {
                        bag.Error(
                            DiagnosticCatalog.IndexUnknownProperty,
                            $"Index on '{entity.Name}' references property '{fields[f]}', which is not declared on the entity.",
                            $"{path}/indexes/{i}/fields/{f}",
                            $"Use one of: {string.Join(", ", propertyNames)}.");
                    }
                }
            }
        }

        /// <summary>
        /// Checks that row-level ownership is fully configured, or not configured at all.
        /// </summary>
        /// <remarks>
        /// The dangerous state is the half-configured one, which reads as protected and is not. Every
        /// rule here exists to make that state impossible to express.
        /// </remarks>
        private static void ValidateOwnershipCoherence(
            Entity entity, List<Property> properties, string path, DiagnosticBag bag)
        {
            var ownerKey = properties.FirstOrDefault(p =>
                p.IsOwnerKey || (p.Attributes ?? new List<string>()).Contains("OwnerKey"));

            if (entity.OwnerScoped && ownerKey is null)
            {
                bag.Error(
                    DiagnosticCatalog.OwnerScopedWithoutOwnerKey,
                    $"Entity '{entity.Name}' sets 'ownerScoped' but declares no owner key, so there is "
                    + "nothing to scope rows by.",
                    $"{path}/properties",
                    "Add a string property named 'OwnerId' with \"isOwnerKey\": true.");
            }

            // The mirror of the tenancy case, and dangerous for the same reason: it reads as
            // owner-scoped to anyone skimming the document, while every query returns every row.
            if (!entity.OwnerScoped && ownerKey is not null)
            {
                bag.Warning(
                    DiagnosticCatalog.OwnerKeyWithoutOwnerScoped,
                    $"Entity '{entity.Name}' declares owner key '{ownerKey.Name}' but does not set "
                    + "'ownerScoped'. No owner filter will be applied and every caller sees every row.",
                    $"{path}/ownerScoped",
                    "Set \"ownerScoped\": true, or remove the owner key.");
            }

            if (ownerKey is not null && !string.Equals(ownerKey.Name, "OwnerId", StringComparison.Ordinal))
            {
                bag.Error(
                    DiagnosticCatalog.OwnerKeyMustBeNamedOwnerId,
                    $"Entity '{entity.Name}' marks '{ownerKey.Name}' as the owner key, but it must be named 'OwnerId'.",
                    $"{path}/properties",
                    "Rename the property to 'OwnerId'.");
            }

            if (ownerKey is not null
                && !string.Equals(ownerKey.Type, "string", StringComparison.OrdinalIgnoreCase))
            {
                bag.Error(
                    DiagnosticCatalog.OwnerKeyMustBeNamedOwnerId,
                    $"Entity '{entity.Name}' declares owner key 'OwnerId' as '{ownerKey.Type}'; it must be a string.",
                    $"{path}/properties",
                    "The owner key holds a claim value, which is always a string.");
            }

            if (!entity.OwnerScoped && entity.OwnerExemptRoles.Count > 0)
            {
                bag.Warning(
                    DiagnosticCatalog.OwnerExemptRolesWithoutOwnerScoped,
                    $"Entity '{entity.Name}' lists 'ownerExemptRoles' but does not set 'ownerScoped', "
                    + "so there is no owner filter for those roles to be exempt from.",
                    $"{path}/ownerScoped",
                    "Set \"ownerScoped\": true, or remove 'ownerExemptRoles'.");
            }
        }

        private static void ValidateEntityFeatureCoherence(Entity entity, List<Property> properties, string path, DiagnosticBag bag)
        {
            var declaresTenantKey = properties.Any(p =>
                p.IsTenantKey || (p.Attributes ?? new List<string>()).Contains("TenantKey"));

            var namesTenantProperty = !string.IsNullOrWhiteSpace(entity.TenantProperty);

            if (entity.MultiTenant && !declaresTenantKey && !namesTenantProperty)
            {
                bag.Error(
                    DiagnosticCatalog.MultiTenantWithoutTenantKey,
                    $"Entity '{entity.Name}' sets 'multiTenant' but declares no tenant key.",
                    $"{path}/properties",
                    "Mark one property with \"isTenantKey\": true, or set the entity's 'tenantProperty'.");
            }

            if (!entity.MultiTenant && declaresTenantKey)
            {
                bag.Warning(
                    DiagnosticCatalog.TenantKeyWithoutMultiTenant,
                    $"Entity '{entity.Name}' declares a tenant key but does not set 'multiTenant'. Tenant filters will not be applied.",
                    $"{path}/multiTenant",
                    "Set \"multiTenant\": true, or remove the tenant key.");
            }

            if (namesTenantProperty
                && !properties.Any(p => string.Equals(p.Name, entity.TenantProperty, StringComparison.OrdinalIgnoreCase)))
            {
                bag.Error(
                    DiagnosticCatalog.MultiTenantWithoutTenantKey,
                    $"Entity '{entity.Name}' names tenant property '{entity.TenantProperty}', which is not declared.",
                    $"{path}/tenantProperty",
                    "Name a property that exists on the entity.");
            }

            // Naming a tenant property while leaving multiTenant off is the most dangerous
            // half-configuration in the IR: it reads as multi-tenant to someone skimming the
            // document, but no tenant filter is injected and every query crosses tenants.
            if (namesTenantProperty && !entity.MultiTenant)
            {
                bag.Error(
                    DiagnosticCatalog.TenantKeyWithoutMultiTenant,
                    $"Entity '{entity.Name}' names tenant property '{entity.TenantProperty}' but does not set 'multiTenant'. "
                    + "No tenant isolation would be applied.",
                    $"{path}/multiTenant",
                    "Set \"multiTenant\": true and mark the tenant property with \"isTenantKey\": true, "
                    + "or remove 'tenantProperty'.");
            }

            // The data layer builds its tenant filter against the stored field named "TenantId". A
            // differently-named key does not merely look untidy: the emitted entity fails to satisfy
            // IMultiTenant (CS0535), and were it to compile the filter would match no document at
            // all. Rejected here, where the message can name the property, rather than surfacing as
            // a compile error in generated code or an empty result set at runtime.
            var tenantKey = properties.FirstOrDefault(p =>
                p.IsTenantKey || (p.Attributes ?? new List<string>()).Contains("TenantKey"));

            if (tenantKey is not null
                && !string.Equals(tenantKey.Name, "TenantId", StringComparison.Ordinal))
            {
                bag.Error(
                    DiagnosticCatalog.TenantKeyMustBeNamedTenantId,
                    $"Entity '{entity.Name}' marks '{tenantKey.Name}' as the tenant key, but it must be named 'TenantId'.",
                    $"{path}/properties",
                    "Rename the property to 'TenantId'.");
            }

            ValidateOwnershipCoherence(entity, properties, path, bag);

            if (!entity.KafkaOutboxEnabled && !string.IsNullOrWhiteSpace(entity.KafkaTopic))
            {
                bag.Warning(
                    DiagnosticCatalog.KafkaTopicWithoutOutbox,
                    $"Entity '{entity.Name}' sets 'kafkaTopic' without 'enableKafkaOutbox'.",
                    $"{path}/enableKafkaOutbox",
                    "Set \"enableKafkaOutbox\": true, or remove 'kafkaTopic'.");
            }

            if (!entity.FileIoEnabled && (entity.FileIoAllowedExtensions?.Count ?? 0) > 0)
            {
                bag.Warning(
                    DiagnosticCatalog.FileIoExtensionsWithoutFileIo,
                    $"Entity '{entity.Name}' sets 'fileIOAllowedExtensions' without 'enableFileIO'.",
                    $"{path}/enableFileIO",
                    "Set \"enableFileIO\": true, or remove 'fileIOAllowedExtensions'.");
            }

            if (entity.Partitioned && entity.ArchiveThresholdYears <= 0)
            {
                bag.Error(
                    DiagnosticCatalog.InvalidArchiveThreshold,
                    $"Entity '{entity.Name}' is partitioned but 'archiveThresholdYears' is {entity.ArchiveThresholdYears}.",
                    $"{path}/archiveThresholdYears",
                    "Set a positive number of years, e.g. 2.");
            }

            if (!string.IsNullOrWhiteSpace(entity.BaseClass))
                ValidateIdentifier(entity.BaseClass!, $"{path}/baseClass", "Base class", bag);
        }

        private static void ValidateEnum(Enum enumDef, int index, DiagnosticBag bag)
        {
            var path = $"/enums/{index}";

            if (string.IsNullOrWhiteSpace(enumDef.Name))
            {
                bag.Error(
                    DiagnosticCatalog.EntityMissingName,
                    "Enum has no name.",
                    $"{path}/name",
                    "Give the enum a PascalCase name such as 'OrderStatus'.");
            }
            else
            {
                ValidateIdentifier(enumDef.Name, $"{path}/name", "Enum name", bag);
            }

            var values = enumDef.Values ?? new List<string>();
            if (values.Count == 0)
            {
                bag.Error(
                    DiagnosticCatalog.EnumNoValues,
                    $"Enum '{enumDef.Name}' declares no values.",
                    $"{path}/values",
                    "Add at least one PascalCase value.");
                return;
            }

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < values.Count; i++)
            {
                ValidateIdentifier(values[i], $"{path}/values/{i}", "Enum value", bag);

                if (!seen.Add(values[i]))
                {
                    bag.Error(
                        DiagnosticCatalog.DuplicatePropertyName,
                        $"Enum '{enumDef.Name}' declares value '{values[i]}' more than once.",
                        $"{path}/values/{i}",
                        "Remove the duplicate value.");
                }
            }
        }

        /// <summary>
        /// Flags enums that nothing is typed with.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A warning rather than an error: an unused enum still emits a perfectly valid C# type,
        /// and one may legitimately be referenced only from hand-written <c>*.Custom.cs</c> code
        /// or declared ahead of the properties that will use it. Failing the build would punish
        /// both.
        /// </para>
        /// <para>
        /// It is worth reporting because of what it usually means. The common shape is an author
        /// — often a model — declaring <c>TicketPriority</c> with the right values and then typing
        /// the property as a plain <c>string</c>. Both halves are individually valid, so nothing
        /// else catches it, and the result is an entity with no type safety and an enum no code
        /// ever mentions.
        /// </para>
        /// </remarks>
        private static void ValidateEnumUsage(SchemaModel schema, List<Enum> enums, DiagnosticBag bag)
        {
            if (enums.Count == 0) return;

            var referencedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var property in (schema.Entities ?? new List<Entity>()).SelectMany(e => e.Properties ?? new List<Property>()))
                if (!string.IsNullOrWhiteSpace(property.Type)) referencedTypes.Add(property.Type);

            foreach (var property in (schema.Dtos ?? new List<DtoModel>()).SelectMany(d => d.Properties ?? new List<DtoProperty>()))
                if (!string.IsNullOrWhiteSpace(property.Type)) referencedTypes.Add(property.Type);

            for (var i = 0; i < enums.Count; i++)
            {
                var declared = enums[i];
                if (string.IsNullOrWhiteSpace(declared.Name)) continue;
                if (referencedTypes.Contains(declared.Name)) continue;

                bag.Warning(
                    DiagnosticCatalog.UnusedEnum,
                    $"Enum '{declared.Name}' is declared but no property uses it.",
                    $"/enums/{i}/name",
                    BuildUnusedEnumHint(schema, declared));
            }
        }

        /// <summary>
        /// Builds a hint that names the property most likely intended to use the enum.
        /// </summary>
        /// <remarks>
        /// Matching on name overlap (enum <c>TicketPriority</c> against property <c>Priority</c>)
        /// turns "something is unused" into "you probably meant this field", which is the
        /// difference between a diagnostic a reader acts on and one they scroll past.
        /// </remarks>
        private static string BuildUnusedEnumHint(SchemaModel schema, Enum declared)
        {
            var candidate = (schema.Entities ?? new List<Entity>())
                .SelectMany(e => (e.Properties ?? new List<Property>()).Select(p => (Entity: e, Property: p)))
                .FirstOrDefault(x =>
                    !x.Property.IsEnum
                    && Vocabulary.IsKnownScalar(x.Property.Type)
                    && (declared.Name.Contains(x.Property.Name, StringComparison.OrdinalIgnoreCase)
                        || x.Property.Name.Contains(declared.Name, StringComparison.OrdinalIgnoreCase)));

            if (candidate.Property is not null)
            {
                return $"'{candidate.Entity.Name}.{candidate.Property.Name}' is typed '{candidate.Property.Type}' "
                       + $"and looks like the intended user. Set its \"type\" to \"{declared.Name}\" and "
                       + "\"isEnum\": true, or remove the enum.";
            }

            return $"Set a property's \"type\" to \"{declared.Name}\" with \"isEnum\": true, or remove the enum.";
        }

        private static void ValidateDtos(SchemaModel schema, List<Entity> entities, DiagnosticBag bag)
        {
            var dtos = schema.Dtos ?? new List<DtoModel>();
            var entityLookup = entities
                .Where(e => !string.IsNullOrWhiteSpace(e.Name))
                .GroupBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < dtos.Count; i++)
            {
                var dto = dtos[i];
                var path = $"/dtos/{i}";

                if (string.IsNullOrWhiteSpace(dto.Name))
                {
                    bag.Error(
                        DiagnosticCatalog.EntityMissingName,
                        "DTO has no name.",
                        $"{path}/name",
                        "Give the DTO a PascalCase name such as 'OrderSummaryDto'.");
                }
                else
                {
                    ValidateIdentifier(dto.Name, $"{path}/name", "DTO name", bag);
                }

                var properties = dto.Properties ?? new List<DtoProperty>();
                for (var p = 0; p < properties.Count; p++)
                {
                    var prop = properties[p];
                    var propPath = $"{path}/properties/{p}";

                    if (string.IsNullOrWhiteSpace(prop.Name))
                    {
                        bag.Error(
                            DiagnosticCatalog.PropertyMissingName,
                            $"DTO '{dto.Name}' property {p} has no name.",
                            $"{propPath}/name",
                            "Give the property a PascalCase name.");
                    }
                    else
                    {
                        ValidateIdentifier(prop.Name, $"{propPath}/name", "DTO property name", bag);
                    }

                    if (string.IsNullOrWhiteSpace(prop.Type))
                    {
                        bag.Error(
                            DiagnosticCatalog.PropertyMissingType,
                            $"DTO '{dto.Name}' property '{prop.Name}' has no type.",
                            $"{propPath}/type",
                            $"Set 'type' to one of: {string.Join(", ", Vocabulary.ScalarTypes.Keys)}.");
                    }

                    ValidateDtoProjection(dto, prop, propPath, entityLookup, bag);
                }
            }
        }

        private static void ValidateDtoProjection(
            DtoModel dto,
            DtoProperty prop,
            string propPath,
            IReadOnlyDictionary<string, Entity> entityLookup,
            DiagnosticBag bag)
        {
            if (string.IsNullOrWhiteSpace(prop.SourceEntity)) return;

            if (!entityLookup.TryGetValue(prop.SourceEntity!, out var sourceEntity))
            {
                bag.Error(
                    DiagnosticCatalog.DtoUnknownSourceEntity,
                    $"DTO '{dto.Name}' property '{prop.Name}' projects from entity '{prop.SourceEntity}', which is not declared.",
                    $"{propPath}/sourceEntity",
                    $"Name one of: {string.Join(", ", entityLookup.Keys)}.");
                return;
            }

            if (string.IsNullOrWhiteSpace(prop.SourceProperty)) return;

            var sourceProperties = sourceEntity.Properties ?? new List<Property>();
            if (!sourceProperties.Any(sp => string.Equals(sp.Name, prop.SourceProperty, StringComparison.OrdinalIgnoreCase)))
            {
                bag.Error(
                    DiagnosticCatalog.DtoUnknownSourceProperty,
                    $"DTO '{dto.Name}' property '{prop.Name}' projects from '{prop.SourceEntity}.{prop.SourceProperty}', which is not declared on that entity.",
                    $"{propPath}/sourceProperty",
                    $"Name one of: {string.Join(", ", sourceProperties.Select(sp => sp.Name))}.");
            }
        }

        private static void ValidateCustomEndpoints(SchemaModel schema, HashSet<string> entityNames, DiagnosticBag bag)
        {
            var endpoints = schema.CustomEndpoints ?? new List<CustomEndpoint>();

            for (var i = 0; i < endpoints.Count; i++)
            {
                var ep = endpoints[i];
                var path = $"/customEndpoints/{i}";

                if (!string.IsNullOrWhiteSpace(ep.RequestType))
                {
                    ValidateIdentifier(ep.RequestType, $"{path}/requestType", "Request type", bag);
                }
                else
                {
                    // PocoGenerator skips an endpoint with no request type outright, so this
                    // otherwise produces a document that compiles "successfully" while generating
                    // nothing for the endpoint the author asked for.
                    bag.Error(
                        DiagnosticCatalog.EndpointUnknownEntity,
                        $"Endpoint '{ep.Route}' has no 'requestType', so no command, query or handler would be generated for it.",
                        $"{path}/requestType",
                        "Set 'requestType' to a PascalCase type name such as 'CancelOrderCommand'.");
                }

                // A target entity is what binds the generated handler to a repository. It is
                // optional only for hand-written Custom operations.
                if (string.IsNullOrWhiteSpace(ep.TargetEntity)
                    && !string.Equals(ep.OperationType, "Custom", StringComparison.OrdinalIgnoreCase))
                {
                    bag.Error(
                        DiagnosticCatalog.EndpointUnknownEntity,
                        $"Endpoint '{ep.Route}' has operationType '{ep.OperationType}' but names no 'targetEntity'.",
                        $"{path}/targetEntity",
                        $"Set 'targetEntity' to the entity this endpoint acts on ({string.Join(", ", entityNames)}), "
                        + "or use \"operationType\": \"Custom\" for a hand-written handler.");
                }

                if (!Vocabulary.HttpMethods.Contains(ep.Method ?? string.Empty))
                {
                    bag.Error(
                        DiagnosticCatalog.InvalidHttpMethod,
                        $"Endpoint '{ep.Route}' declares method '{ep.Method}'.",
                        $"{path}/method",
                        $"Use one of: {string.Join(", ", Vocabulary.HttpMethods)}.");
                }

                if (string.IsNullOrWhiteSpace(ep.Route) || !ep.Route.StartsWith("/", StringComparison.Ordinal))
                {
                    bag.Error(
                        DiagnosticCatalog.InvalidRoute,
                        $"Endpoint route '{ep.Route}' must begin with '/'.",
                        $"{path}/route",
                        "Write the route as an absolute path, e.g. '/api/v1/orders/submit'.");
                }

                if (!Vocabulary.OperationTypes.Contains(ep.OperationType ?? string.Empty))
                {
                    bag.Warning(
                        DiagnosticCatalog.UnknownAttribute,
                        $"Endpoint '{ep.Route}' declares operationType '{ep.OperationType}'.",
                        $"{path}/operationType",
                        $"Use one of: {string.Join(", ", Vocabulary.OperationTypes)}.");
                }

                if (!string.IsNullOrWhiteSpace(ep.TargetEntity) && !entityNames.Contains(ep.TargetEntity))
                {
                    bag.Error(
                        DiagnosticCatalog.EndpointUnknownEntity,
                        $"Endpoint '{ep.Route}' targets entity '{ep.TargetEntity}', which is not declared.",
                        $"{path}/targetEntity",
                        $"Name one of: {string.Join(", ", entityNames)}.");
                }

                foreach (var rule in ep.BusinessRules ?? new List<string>())
                    ValidateIdentifier(rule, $"{path}/businessRules", "Business rule name", bag);
            }
        }

        private static void ValidateWorkflows(SchemaModel schema, HashSet<string> entityNames, DiagnosticBag bag)
        {
            var workflows = schema.Workflows ?? new List<WorkflowModel>();
            var triggers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            for (var i = 0; i < workflows.Count; i++)
            {
                var wf = workflows[i];
                var path = $"/workflows/{i}";

                if (!string.IsNullOrWhiteSpace(wf.Entity) && !entityNames.Contains(wf.Entity))
                {
                    bag.Error(
                        DiagnosticCatalog.WorkflowUnknownEntity,
                        $"Workflow '{wf.Name}' is bound to entity '{wf.Entity}', which is not declared.",
                        $"{path}/entity",
                        $"Name one of: {string.Join(", ", entityNames)}.");
                }

                var states = wf.States ?? new List<WorkflowStateModel>();
                var choiceNodes = wf.ChoiceNodes ?? new List<WorkflowChoiceNodeModel>();

                // A transition may target either a state or a choice node.
                var targets = new HashSet<string>(
                    states.Select(s => s.Name).Concat(choiceNodes.Select(c => c.Name)).Where(n => !string.IsNullOrWhiteSpace(n)),
                    StringComparer.OrdinalIgnoreCase);

                var stateNames = new HashSet<string>(
                    states.Select(s => s.Name).Where(n => !string.IsNullOrWhiteSpace(n)),
                    StringComparer.OrdinalIgnoreCase);

                if (states.Count > 0 && !states.Any(s => s.IsInitial))
                {
                    bag.Error(
                        DiagnosticCatalog.WorkflowNoInitialState,
                        $"Workflow '{wf.Name}' has no initial state.",
                        $"{path}/states",
                        "Mark exactly one state with \"isInitial\": true.");
                }

                if (states.Count > 0 && !states.Any(s => s.IsFinal))
                {
                    bag.Warning(
                        DiagnosticCatalog.WorkflowNoFinalState,
                        $"Workflow '{wf.Name}' has no final state, so instances can never complete.",
                        $"{path}/states",
                        "Mark at least one state with \"isFinal\": true.");
                }

                ValidateTransitions(wf, path, stateNames, targets, triggers, bag);
                ValidateChoiceNodes(wf, path, targets, bag);
            }
        }

        private static void ValidateTransitions(
            WorkflowModel wf,
            string path,
            HashSet<string> stateNames,
            HashSet<string> targets,
            Dictionary<string, string> triggers,
            DiagnosticBag bag)
        {
            var transitions = wf.Transitions ?? new List<WorkflowTransitionModel>();

            for (var t = 0; t < transitions.Count; t++)
            {
                var trans = transitions[t];
                var transPath = $"{path}/transitions/{t}";

                if (!string.IsNullOrWhiteSpace(trans.FromState) && !stateNames.Contains(trans.FromState))
                {
                    bag.Error(
                        DiagnosticCatalog.WorkflowUnknownState,
                        $"Transition '{trans.Name}' starts from state '{trans.FromState}', which is not declared in workflow '{wf.Name}'.",
                        $"{transPath}/fromState",
                        $"Name one of: {string.Join(", ", stateNames)}.");
                }

                if (!string.IsNullOrWhiteSpace(trans.ToState) && !targets.Contains(trans.ToState))
                {
                    bag.Error(
                        DiagnosticCatalog.WorkflowUnknownState,
                        $"Transition '{trans.Name}' targets '{trans.ToState}', which is neither a declared state nor a choice node in workflow '{wf.Name}'.",
                        $"{transPath}/toState",
                        $"Name one of: {string.Join(", ", targets)}.");
                }

                if (string.IsNullOrWhiteSpace(trans.Trigger)) continue;

                ValidateIdentifier(trans.Trigger, $"{transPath}/trigger", "Transition trigger", bag);

                if (triggers.TryGetValue(trans.Trigger, out var owner))
                {
                    bag.Error(
                        DiagnosticCatalog.DuplicateTransitionTrigger,
                        $"Trigger '{trans.Trigger}' is used by more than one transition (also in workflow '{owner}').",
                        $"{transPath}/trigger",
                        "Triggers generate command types, so each must be unique across all workflows.");
                }
                else
                {
                    triggers[trans.Trigger] = wf.Name;
                }
            }
        }

        private static void ValidateChoiceNodes(WorkflowModel wf, string path, HashSet<string> targets, DiagnosticBag bag)
        {
            var choiceNodes = wf.ChoiceNodes ?? new List<WorkflowChoiceNodeModel>();

            for (var c = 0; c < choiceNodes.Count; c++)
            {
                var branches = choiceNodes[c].Branches ?? new List<WorkflowBranchModel>();
                for (var b = 0; b < branches.Count; b++)
                {
                    var target = branches[b].TargetState;
                    if (!string.IsNullOrWhiteSpace(target) && !targets.Contains(target))
                    {
                        bag.Error(
                            DiagnosticCatalog.WorkflowUnknownChoiceNode,
                            $"Choice node '{choiceNodes[c].Name}' branch {b} targets '{target}', which is not declared in workflow '{wf.Name}'.",
                            $"{path}/choiceNodes/{c}/branches/{b}/targetState",
                            $"Name one of: {string.Join(", ", targets)}.");
                    }
                }
            }
        }

        private static void ValidateConnectors(SchemaModel schema, DiagnosticBag bag)
        {
            var connectors = schema.Connectors ?? new List<ConnectorModel>();

            for (var i = 0; i < connectors.Count; i++)
            {
                var connector = connectors[i];
                var path = $"/connectors/{i}";

                if (!string.IsNullOrWhiteSpace(connector.Name))
                    ValidateIdentifier(connector.Name, $"{path}/name", "Connector name", bag);

                if (!Vocabulary.ConnectorTypes.Contains(connector.Type ?? string.Empty))
                {
                    bag.Error(
                        DiagnosticCatalog.UnknownAttribute,
                        $"Connector '{connector.Name}' declares type '{connector.Type}'.",
                        $"{path}/type",
                        $"Use one of: {string.Join(", ", Vocabulary.ConnectorTypes)}.");
                }

                if (!Vocabulary.AuthTypes.Contains(connector.AuthType ?? string.Empty))
                {
                    bag.Error(
                        DiagnosticCatalog.UnknownAttribute,
                        $"Connector '{connector.Name}' declares authType '{connector.AuthType}'.",
                        $"{path}/authType",
                        $"Use one of: {string.Join(", ", Vocabulary.AuthTypes)}.");
                }

                if (!string.IsNullOrWhiteSpace(connector.BaseUrl)
                    && !Uri.TryCreate(connector.BaseUrl, UriKind.Absolute, out _))
                {
                    bag.Warning(
                        DiagnosticCatalog.InvalidRoute,
                        $"Connector '{connector.Name}' has a baseUrl that is not an absolute URI.",
                        $"{path}/baseUrl",
                        "Write an absolute URL, e.g. 'https://api.example.com'.");
                }
            }
        }

        private static void ValidateIdentifier(string? name, string path, string what, DiagnosticBag bag)
        {
            if (string.IsNullOrWhiteSpace(name)) return;

            if (CodeGen.ReservedKeywords.Contains(name!))
            {
                bag.Error(
                    DiagnosticCatalog.ReservedKeyword,
                    $"{what} '{name}' is a reserved C# keyword.",
                    path,
                    "Choose a different name.");
                return;
            }

            if (!CodeGen.IsValidIdentifier(name))
            {
                bag.Error(
                    DiagnosticCatalog.InvalidIdentifier,
                    $"{what} '{name}' is not a valid C# identifier and cannot be emitted as code.",
                    path,
                    "Use only letters, digits and underscores, starting with a letter or underscore. No spaces or punctuation.");
            }
        }
    }
}
