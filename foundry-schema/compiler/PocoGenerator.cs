using System;
using System.Collections.Generic;
using System.Linq;

namespace Foundry.Schema.Compiler
{
    /// <summary>
    /// How the compiler treats an emitted file on subsequent runs.
    /// </summary>
    public enum EmitKind
    {
        /// <summary>
        /// Pure compiler output. Overwritten on every run. Never edit by hand.
        /// </summary>
        Generated,

        /// <summary>
        /// A starting point for hand-written logic. Written once and never overwritten,
        /// because the developer's business rules live in it.
        /// </summary>
        Scaffold
    }

    /// <summary>
    /// One file produced by the compiler, with the write policy that applies to it.
    /// </summary>
    /// <param name="Path">Output path relative to the output directory, without extension.</param>
    /// <param name="Content">Complete file contents, including the leading header.</param>
    /// <param name="Kind">Whether the file may be overwritten.</param>
    public sealed record GeneratedFile(string Path, string Content, EmitKind Kind);

    public static class PocoGenerator
    {
        /// <summary>
        /// Generates the full output set, classified by write policy.
        /// </summary>
        /// <remarks>
        /// Prefer this over <see cref="Generate"/>. Business-rule stubs contain the developer's
        /// own logic, so they are marked <see cref="EmitKind.Scaffold"/> and must be written
        /// only when absent. Overwriting them — which the compiler previously did on every run
        /// via an unconditional write — silently destroys hand-written code.
        /// </remarks>
        public static IReadOnlyList<GeneratedFile> GenerateFiles(SchemaModel schema)
        {
            var raw = Generate(schema, out var scaffoldPaths);

            return raw.Select(entry =>
            {
                var kind = scaffoldPaths.Contains(entry.Key) ? EmitKind.Scaffold : EmitKind.Generated;
                var header = kind == EmitKind.Scaffold ? CodeGen.ScaffoldHeader : CodeGen.GeneratedHeader;
                return new GeneratedFile(entry.Key, header + "\n" + entry.Value, kind);
            }).ToList();
        }

        /// <summary>
        /// Generates the full output set as a path-to-content map.
        /// </summary>
        /// <remarks>
        /// Retained for callers that do not need write-policy information. Callers that write
        /// to disk should use <see cref="GenerateFiles"/> instead so scaffolds are preserved.
        /// </remarks>
        public static Dictionary<string, string> Generate(SchemaModel schema)
            => GenerateFiles(schema).ToDictionary(f => f.Path, f => f.Content);

        private static Dictionary<string, string> Generate(SchemaModel schema, out HashSet<string> scaffoldPaths)
        {
            var result = new Dictionary<string, string>();

            // Paths whose contents are the developer's, not the compiler's.
            scaffoldPaths = new HashSet<string>(StringComparer.Ordinal);

            // (rule class name, the request type it validates), collected as the stubs are emitted
            // so the registrations below cannot disagree with them.
            var ruleRegistrations = new List<(string Rule, string Request)>();

            // Enums, entities and DTOs are foldered by kind, as every other artefact already was.
            // They used to be written to the output root while Commands/, Handlers/, Rules/,
            // Services/, Kafka/, Workflow/, RealTime/, Serialization/ and Diagnostics/ all had a
            // home, so a domain of any size buried nine directories under a flat list of types --
            // 48 of them here. The folder is a path, not a namespace: every one of these still
            // declares the schema's own namespace, exactly as Commands/ does, so this moves files
            // and changes no type's full name.

            // Generate enums
            if (schema.Enums != null)
            {
                foreach (var enumDef in schema.Enums)
                {
                    var enumCode = GenerateEnum(enumDef, schema.Namespace);
                    result[$"Enums/{enumDef.Name}"] = enumCode;
                }
            }

            // Generate entities
            if (schema.Entities != null)
            {
                foreach (var entity in schema.Entities)
                {
                    var entityCode = GenerateEntity(entity, schema.Namespace, schema.Workflows);
                    result[$"Entities/{entity.Name}"] = entityCode;
                }
            }

            // Generate DTOs
            if (schema.Dtos != null)
            {
                foreach (var dto in schema.Dtos)
                {
                    var dtoCode = GenerateDto(dto, schema.Namespace);
                    result[$"Dtos/{dto.Name}"] = dtoCode;
                }
            }

            // Generate Custom Endpoint Handlers
            if (schema.CustomEndpoints != null)
            {
                foreach (var ep in schema.CustomEndpoints)
                {
                    if (string.IsNullOrEmpty(ep.RequestType) || ep.RequestType.Equals("Void", StringComparison.OrdinalIgnoreCase))
                        continue;

                    // The request type the handler and its rules are typed against. Without this
                    // the emitted handler referenced a type nothing declared, so a generated
                    // project failed to build with CS0246 on every custom endpoint.
                    result[$"Commands/{ep.RequestType}"] = GenerateCustomEndpointRequest(ep, schema.Namespace, schema);

                    // The custom-endpoint handler body is a starting point the developer
                    // completes, so it is a scaffold rather than compiler output.
                    var handlerPath = $"Handlers/{ep.RequestType}Handler";
                    result[handlerPath] = GenerateHandler(ep, schema.Namespace, schema);
                    scaffoldPaths.Add(handlerPath);

                    if (ep.BusinessRules != null)
                    {
                        foreach (var rule in ep.BusinessRules)
                        {
                            if (string.IsNullOrWhiteSpace(rule)) continue;
                            var rulePath = $"Rules/{rule}";
                            result[rulePath] = GenerateCustomEndpointRuleStub(rule, ep.RequestType, schema.Namespace);
                            scaffoldPaths.Add(rulePath);
                            ruleRegistrations.Add((rule, $"{schema.Namespace}.{ep.RequestType}"));
                        }
                    }
                }
            }

            // Generate Entity-level CRUD Rules stubs
            if (schema.Entities != null)
            {
                foreach (var entity in schema.Entities)
                {
                    if (entity.ApiBusinessRules == null) continue;
                    foreach (var pair in entity.ApiBusinessRules)
                    {
                        var method = pair.Key;
                        var rules = pair.Value;
                        if (rules == null) continue;
                        foreach (var rule in rules)
                        {
                            if (string.IsNullOrWhiteSpace(rule)) continue;
                            var rulePath = $"Rules/{rule}";
                            result[rulePath] = GenerateEntityRuleStub(rule, method, entity.Name, schema.Namespace);
                            scaffoldPaths.Add(rulePath);
                            ruleRegistrations.Add((rule, EntityRuleRequestType(method, entity.Name, schema.Namespace)));
                        }
                    }
                }
            }

            // Registrations for every rule the schema named.
            //
            // The stubs above are classes and nothing bound them to the container, so a rule a
            // schema declared was compiled into the application and never ran: AddFoundryRules
            // registers the engine, not the rules, and BusinessRuleBehavior resolves
            // IBusinessRule<TRequest> from DI. Both apiBusinessRules and a custom endpoint's
            // businessRules were therefore inert in every generated application -- declared,
            // emitted, enforced by nothing.
            if (ruleRegistrations.Count > 0)
            {
                var lines = string.Join("\n", ruleRegistrations
                    .OrderBy(r => r.Rule, StringComparer.Ordinal)
                    .Select(r => $"        services.AddTransient<IBusinessRule<{r.Request}>, {schema.Namespace}.Rules.{r.Rule}>();"));

                result["Rules/RuleRegistrations"] = $@"using Microsoft.Extensions.DependencyInjection;
using Foundry.Rules;
using Foundry.Api.MediatR;
using {schema.Namespace};

namespace {schema.Namespace}.Rules;

/// <summary>
/// Binds every business rule this schema declared to the request it validates.
/// </summary>
public static class RuleRegistrations
{{
    public static IServiceCollection AddGeneratedBusinessRules(this IServiceCollection services)
    {{
{lines}
        return services;
    }}
}}
";
            }

            // Generate Workflow Transition Trigger Commands & Handlers
            if (schema.Workflows != null)
            {
                foreach (var wf in schema.Workflows)
                {
                    if (string.IsNullOrEmpty(wf.Entity)) continue;
                    var boundEntity = schema.Entities?.FirstOrDefault(e => e.Name.Equals(wf.Entity, StringComparison.OrdinalIgnoreCase));
                    if (boundEntity == null) continue;

                    foreach (var trans in wf.Transitions)
                    {
                        if (string.IsNullOrEmpty(trans.Trigger)) continue;

                        // `useCustomCommand` means the application supplies this transition's
                        // command and handler itself, so the compiler emits neither.
                        //
                        // The flag was read by nothing: it was written into the generated workflow
                        // definition, the engine never consulted it, and the command and handler
                        // were emitted regardless -- so a schema asking to write its own got the
                        // generated pair anyway, and the only way to notice was to look. The
                        // workflow definition still names the trigger, so the engine matches the
                        // hand-written command by name exactly as it matches a generated one; if
                        // nobody writes it, the build fails on the missing type, which is the
                        // loudest and earliest place that can be reported.
                        if (trans.UseCustomCommand) continue;

                        var cmdCode = GenerateTransitionCommand(trans, boundEntity, schema.Namespace);
                        result[$"Commands/{trans.Trigger}"] = cmdCode;

                        var handlerCode = GenerateTransitionHandler(trans, schema.Namespace);
                        result[$"Handlers/{trans.Trigger}Handler"] = handlerCode;
                    }
                }
            }

            // --- Work Item 1.1: Kafka Consumers & Registration ---
            var kafkaEntities = (schema.Entities ?? new List<Entity>())
                .Where(e => e.KafkaOutboxEnabled || !string.IsNullOrEmpty(e.KafkaTopic))
                .Select(e => new { e.Name, Topic = KafkaTopicNaming.TopicFor(e.Name, e.KafkaTopic) })
                .Concat(
                    (schema.Dtos ?? new List<DtoModel>())
                        .Where(d => d.KafkaOutboxEnabled || !string.IsNullOrEmpty(d.KafkaTopic))
                        .Select(d => new { d.Name, Topic = KafkaTopicNaming.TopicFor(d.Name, d.KafkaTopic) })
                ).ToList();

            if (kafkaEntities.Any())
            {
                foreach (var kTarget in kafkaEntities)
                {
                    result[$"Kafka/{kTarget.Name}KafkaConsumer"] = $@"using System;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Foundry.Kafka.Consumer;
using {schema.Namespace};

namespace {schema.Namespace}.Kafka;

/// <summary>
/// Auto-generated Kafka consumer for {kTarget.Name} events.
/// </summary>
public partial class {CodeGen.Ident(kTarget.Name, "Entity name")}KafkaConsumer : IKafkaMessageHandler
{{
    public string Topic => ""{CodeGen.Lit(kTarget.Topic)}"";

    public Task HandleAsync(string topic, string key, string value, System.Collections.Generic.IDictionary<string, string> headers, CancellationToken ct)
    {{
        // Auto-generated consumer handler for {kTarget.Name} Kafka events
        return Task.CompletedTask;
    }}
}}
";
                }

                var handlerRegistrations = string.Join("\n        ", kafkaEntities.Select(e => $"services.AddSingleton<IKafkaMessageHandler, {e.Name}KafkaConsumer>();"));
                result["Kafka/KafkaRegistrations"] = $@"using Microsoft.Extensions.DependencyInjection;
using Foundry.Kafka.Consumer;

namespace {schema.Namespace}.Kafka;

public static class KafkaRegistrations
{{
    public static IServiceCollection AddGeneratedKafkaHandlers(this IServiceCollection services)
    {{
        {handlerRegistrations}
        return services;
    }}
}}
";
            }

            // GraphQL is deliberately not emitted here.
            //
            // This used to write a GraphQL/GraphQLRegistration.cs holding [ExtendObjectType] query
            // and mutation classes -- a second, independent implementation of a surface
            // Foundry.Api already builds from the same api-manifest.json this compiler writes. It
            // was never compiled by anything, and it did not compile: it called repo.AsQueryable()
            // and repo.AddAsync(), neither of which exists on IRepository<T>. So every schema that
            // set enableGraphQL produced a project that could not build.
            //
            // It also enforced no roles, while the manifest-driven surface runs every field through
            // GraphQLAccessGuard. Repairing it would have left two implementations of one rule --
            // the mistake this repository has paid for more than any other -- so the rival is gone
            // and enableGraphQL now travels in the manifest instead, where the code that runs reads
            // it. See ApiManifestGenerator and GraphQLConfiguration.AddDynamicGraphQL.

            // --- Work Item 1.3: FileIO Services (Entities & Composite DTOs) ---
            var fileTargets = (schema.Entities ?? new List<Entity>())
                .Where(e => e.FileIoEnabled)
                .Select(e => new { e.Name, AllowedExtensions = e.FileIoAllowedExtensions })
                .Concat(
                    (schema.Dtos ?? new List<DtoModel>())
                        .Where(d => d.FileIoEnabled)
                        .Select(d => new { d.Name, AllowedExtensions = d.FileIoAllowedExtensions })
                ).ToList();

            foreach (var target in fileTargets)
            {
                // Use schema-defined allowed extensions or fall back to defaults
                var allowedExts = target.AllowedExtensions != null && target.AllowedExtensions.Count > 0
                    ? target.AllowedExtensions.Select(e => e.StartsWith(".") ? e.ToLowerInvariant() : $".{e.ToLowerInvariant()}").ToList()
                    : new List<string> { ".csv", ".xlsx", ".xls" };

                // Build switch arms from allowed extensions.
                //
                // One quote, not two. These fragments are *interpolated into* the verbatim template
                // below, and interpolation inserts a value as-is -- it does not re-process verbatim
                // escapes. Written as `""` they reached the generated file as `""`, so every
                // extension literal emitted as an empty string followed by a bare `.csv`, and the
                // file did not parse. Any schema setting enableFileIO produced a project that could
                // not build.
                var switchArms = new List<string>();
                if (allowedExts.Contains(".csv"))
                    switchArms.Add("            \".csv\" => _csvParser.ParseAsync(fileStream, ct),");
                if (allowedExts.Contains(".xlsx") || allowedExts.Contains(".xls"))
                {
                    var excelExts = new List<string>();
                    if (allowedExts.Contains(".xlsx")) excelExts.Add("\".xlsx\"");
                    if (allowedExts.Contains(".xls")) excelExts.Add("\".xls\"");
                    switchArms.Add($"            {string.Join(" or ", excelExts)} => _excelParser.ParseAsync(fileStream, ct),");
                }
                if (allowedExts.Contains(".json"))
                    switchArms.Add("            \".json\" => throw new NotSupportedException(\"JSON import requires JsonDataParser (see Foundry.FileIO)\"),");
                if (allowedExts.Contains(".xml"))
                    switchArms.Add("            \".xml\" => throw new NotSupportedException(\"XML import requires XmlDataParser (see Foundry.FileIO)\"),");

                var allowedExtsList = string.Join(", ", allowedExts.Select(e => $"\"{e}\""));
                var switchBody = string.Join("\n", switchArms);

                result[$"Services/{target.Name}FileService"] = $@"using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Foundry.FileIO;
using {schema.Namespace};

namespace {schema.Namespace}.Services;

/// <summary>
/// Auto-generated FileIO streaming import and export service for {target.Name}.
/// Allowed extensions: {string.Join(", ", allowedExts)}
/// </summary>
public class {target.Name}FileService
{{
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {{
        {allowedExtsList}
    }};

    private readonly CsvDataParser<{target.Name}> _csvParser = new();
    private readonly ExcelDataParser<{target.Name}> _excelParser = new();
    private readonly CsvDataExporter<{target.Name}> _csvExporter = new();

    /// <summary>Streams rows out of a file, without holding the whole file in memory.</summary>
    public IAsyncEnumerable<{target.Name}> ImportAsync(
        Stream fileStream, string fileName, CancellationToken ct = default)
    {{
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        if (!AllowedExtensions.Contains(ext))
            throw new NotSupportedException($""File extension '{{ext}}' is not allowed for {target.Name}. Allowed: {string.Join(", ", allowedExts)}"");

        return ext switch
        {{
{switchBody}
            _ => throw new NotSupportedException($""File extension '{{ext}}' has no registered parser for {target.Name}."")
        }};
    }}

    /// <summary>Reads the whole file into a list, for callers that want it in one piece.</summary>
    public async Task<IReadOnlyList<{target.Name}>> ImportAllAsync(
        Stream fileStream, string fileName, CancellationToken ct = default)
    {{
        var items = new List<{target.Name}>();
        await foreach (var item in ImportAsync(fileStream, fileName, ct).WithCancellation(ct))
        {{
            items.Add(item);
        }}
        return items;
    }}

    public Task ExportToCsvAsync(
        IAsyncEnumerable<{target.Name}> items, Stream outputStream, CancellationToken ct = default)
        => _csvExporter.ExportAsync(items, outputStream, ct);
}}
";
            }

            // --- Work Item 1.4: Workflow Configurations ---
            if (schema.Workflows != null && schema.Workflows.Count > 0)
            {
                var workflowEntries = new List<string>();
                foreach (var wf in schema.Workflows)
                {
                    // Generate state configs
                    var stateConfigs = string.Join(",\n                    ", wf.States.Select(s =>
                        $@"new WorkflowStateConfig {{ Name = ""{s.Name}"", IsInitial = {s.IsInitial.ToString().ToLower()}, IsFinal = {s.IsFinal.ToString().ToLower()}, AllowedRoles = new List<string> {{ {string.Join(", ", s.AllowedRoles.Select(r => $@"""{r}"""))} }} }}"));

                    // Generate transition configs
                    var transitionConfigs = string.Join(",\n                    ", wf.Transitions.Select(t =>
                    {
                        var conditionConfigs = t.Conditions.Count > 0
                            ? string.Join(", ", t.Conditions.Select(c =>
                                $@"new WorkflowConditionConfig {{ Type = ""{c.Type}"", Property = ""{c.Property}"", Operator = ""{c.Operator}"", Value = ""{c.Value}"" }}"))
                            : "";
                        var actionConfigs = t.Actions.Count > 0
                            ? string.Join(", ", t.Actions.Select(a => BuildActionConfigInitializer(a)))
                            : "";
                        var rolesList = t.RequiredRoles.Count > 0
                            ? string.Join(", ", t.RequiredRoles.Select(r => $@"""{r}"""))
                            : "";

                        return $@"new WorkflowTransitionConfig
                    {{
                        Id = ""{t.Id}"", Name = ""{t.Name}"", FromState = ""{t.FromState}"", ToState = ""{t.ToState}"",
                        Trigger = ""{t.Trigger}"", UseCustomCommand = {t.UseCustomCommand.ToString().ToLower()},
                        RequiredRoles = new List<string> {{ {rolesList} }},
                        Conditions = new List<WorkflowConditionConfig> {{ {conditionConfigs} }},
                        Actions = new List<WorkflowActionConfig> {{ {actionConfigs} }}
                    }}";
                    }));

                    // Generate choice node configs
                    var choiceConfigs = string.Join(",\n                    ", wf.ChoiceNodes.Select(cn =>
                    {
                        var branches = string.Join(", ", cn.Branches.Select(b =>
                        {
                            var branchConditions = b.Condition != null
                                ? $@"new WorkflowConditionConfig {{ Type = ""{b.Condition.Type}"", Property = ""{b.Condition.Property}"", Operator = ""{b.Condition.Operator}"", Value = ""{b.Condition.Value}"" }}"
                                : "";
                            return $@"new WorkflowChoiceBranchConfig {{ ToState = ""{b.TargetState}"", Conditions = new List<WorkflowConditionConfig> {{ {branchConditions} }} }}";
                        }));
                        return $@"new WorkflowChoiceNodeConfig {{ Id = ""{cn.Id}"", Name = ""{cn.Name}"", Branches = new List<WorkflowChoiceBranchConfig> {{ {branches} }} }}";
                    }));

                    var entry = $@"new WorkflowConfig
                {{
                    Id = ""{wf.Id}"", Name = ""{wf.Name}"", Entity = ""{wf.Entity}"",
                    Version = ""{wf.Version}"", EffectiveDate = ""{wf.EffectiveDate}"",
                    ExpirationDate = ""{wf.ExpirationDate}"", IsActive = {wf.IsActive.ToString().ToLower()},
                    States = new List<WorkflowStateConfig>
                    {{
                        {stateConfigs}
                    }},
                    Transitions = new List<WorkflowTransitionConfig>
                    {{
                        {transitionConfigs}
                    }},
                    ChoiceNodes = new List<WorkflowChoiceNodeConfig>
                    {{
                        {choiceConfigs}
                    }}
                }}";
                    workflowEntries.Add(entry);
                }

                var allWorkflows = string.Join(",\n            ", workflowEntries);
                result["Workflow/WorkflowConfigurations"] = $@"using System.Collections.Generic;
using Foundry.Rules;

namespace {schema.Namespace}.Workflow;

/// <summary>
/// Auto-generated workflow configurations parsed from visual state machine schema.
/// </summary>
public static class WorkflowConfigurations
{{
    public static List<WorkflowConfig> GetConfigurations()
    {{
        return new List<WorkflowConfig>
        {{
            {allWorkflows}
        }};
    }}
}}
";
            }

            // --- System.Text.Json source-generated serialization context ---
            //
            // Reflection-based STJ is the default and costs a metadata walk per type on first use,
            // plus it is trim/AOT-hostile. A generated JsonSerializerContext moves that to compile
            // time. Every entity, enum and DTO the schema declares is registered, so applications
            // can opt in with JsonSerializerOptions.TypeInfoResolver = FoundryJsonContext.Default.
            {
                var serializableTypes = new List<string>();

                foreach (var e in schema.Entities ?? new List<Entity>())
                    serializableTypes.Add(CodeGen.Ident(e.Name, "Entity name"));

                foreach (var d in schema.Dtos ?? new List<DtoModel>())
                    serializableTypes.Add(CodeGen.Ident(d.Name, "DTO name"));

                if (serializableTypes.Count > 0)
                {
                    var attributes = string.Join("\n", serializableTypes
                        .SelectMany(t => new[]
                        {
                            $"[JsonSerializable(typeof({t}))]",
                            $"[JsonSerializable(typeof(System.Collections.Generic.List<{t}>))]"
                        }));

                    result["Serialization/FoundryJsonContext"] = $@"using System.Text.Json.Serialization;

namespace {CodeGen.Ns(schema.Namespace)}.Serialization;

/// <summary>
/// Source-generated System.Text.Json contracts for this domain.
/// </summary>
/// <remarks>
/// Assign <c>FoundryJsonContext.Default</c> to
/// <c>JsonSerializerOptions.TypeInfoResolver</c> to serialize without reflection.
/// </remarks>
{attributes}
public partial class FoundryJsonContext : JsonSerializerContext
{{
}}
";
                }
            }

            // --- Startup index verification ---
            //
            // Declared indexes are created by EntityIndexManager at startup, but nothing proved
            // they exist afterwards. A missing index does not fail anything — it silently turns
            // an indexed lookup into a collection scan, which is the most expensive kind of
            // quiet failure in a MongoDB application.
            if ((schema.Entities?.Count ?? 0) > 0)
            {
                var entityNames = (schema.Entities ?? new List<Entity>())
                    .Select(e => CodeGen.Ident(e.Name, "Entity name"))
                    .ToList();

                var registrations = string.Join("\n", entityNames.Select(n =>
                    $"        await EnsureAsync<{n}>(provider, ct);"));

                result["Diagnostics/IndexVerification"] = $@"using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Foundry.Mongo.Repositories;
using {CodeGen.Ns(schema.Namespace)};

namespace {CodeGen.Ns(schema.Namespace)}.Diagnostics;

/// <summary>
/// Creates every declared index at startup and logs the outcome per entity.
/// </summary>
public static partial class IndexVerification
{{
    /// <summary>
    /// Ensures declared indexes exist for every entity in this domain.
    /// </summary>
    public static async Task EnsureIndexesAsync(IServiceProvider provider, CancellationToken ct = default)
    {{
{registrations}
    }}

    private static async Task EnsureAsync<T>(IServiceProvider provider, CancellationToken ct)
        where T : class, Foundry.Core.Entities.IEntity<MongoDB.Bson.ObjectId>
    {{
        var logger = provider.GetService<ILoggerFactory>()?.CreateLogger(""Foundry.IndexVerification"");

        try
        {{
            var repository = provider.GetService<IRepository<T>>();
            if (repository is null)
            {{
                logger?.LogWarning(""No IRepository<{{Entity}}> registered; indexes not verified."", typeof(T).Name);
                return;
            }}

            await repository.CreateIndexesAsync(ct);
            logger?.LogInformation(""Indexes ensured for {{Entity}}."", typeof(T).Name);
        }}
        catch (Exception ex)
        {{
            // Surfaced rather than swallowed: a failed index build is a performance cliff that
            // otherwise only shows up as unexplained latency in production.
            logger?.LogError(ex, ""Failed to ensure indexes for {{Entity}}."", typeof(T).Name);
            throw;
        }}
    }}
}}
";
            }

            // --- Work Item 1.5: RealTime Endpoint Mapping ---
            if (schema.Entities != null && schema.Entities.Any(e => e.RealTime))
            {
                // MapFoundryRealTime lives in Microsoft.Extensions.DependencyInjection, and that
                // namespace is an implicit using only in a Web SDK project. Relying on it meant the
                // file compiled inside a scaffolded application and not inside a plain library --
                // so the same generated code was valid or invalid according to the consumer's SDK.
                result["RealTime/RealTimeConfiguration"] = $@"using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Foundry.RealTime;

namespace {schema.Namespace}.RealTime;

/// <summary>
/// Auto-generated real-time SignalR, WebSockets, and SSE route configuration.
/// </summary>
public static class RealTimeConfiguration
{{
    public static IEndpointRouteBuilder MapGeneratedRealTimeEndpoints(this IEndpointRouteBuilder endpoints)
    {{
        endpoints.MapFoundryRealTime();
        return endpoints;
    }}
}}
";
            }

            return result;
        }

        private static string GenerateEnum(Enum enumDef, string @namespace)
        {
            var values = string.Join(",\n    ", enumDef.Values.Select(v => CodeGen.Ident(v, "Enum value")));
            return $@"namespace {CodeGen.Ns(@namespace)};

public enum {CodeGen.Ident(enumDef.Name, "Enum name")}
{{
    {values}
}}";
        }

        private static string GenerateEntity(Entity entity, string @namespace, List<WorkflowModel> workflows)
        {
            var keyProperty = entity.Properties.FirstOrDefault(p => p.IsKey);
            var keyType = keyProperty?.Type ?? "ObjectId";

            // Map ObjectId to C# type name
            if (keyType.Equals("ObjectId", StringComparison.OrdinalIgnoreCase))
                keyType = "ObjectId";

            // BaseEntity<TKey> always comes first, and baseClass is listed after it rather than
            // instead of it.
            //
            // Substituting it -- which is what "leave unset to derive from BaseEntity<TKey>" used to
            // mean literally -- produced an entity with no Id, no CreatedAtUtc, and no
            // IEntity<ObjectId>. Every generic in the framework is constrained on that interface, so
            // an entity naming a baseClass took the repository, the endpoint generator, the index
            // verifier and the workflow engine out with it, all reported as CS0311 on generated code
            // the developer never wrote. The field could not be used correctly by anyone.
            //
            // What it is actually good for is naming an application interface the entity should
            // carry, so application code can talk about a set of entities the schema does not
            // otherwise group. That composes; replacing the base class does not.
            var interfaces = new List<string> { "BaseEntity<" + keyType + ">" };
            if (!string.IsNullOrEmpty(entity.BaseClass))
                interfaces.Add(entity.BaseClass!);

            interfaces.Add("IVersionable");
            if (entity.SoftDelete)
                interfaces.Add("ISoftDelete");

            var hasWorkflow = workflows != null && workflows.Any(w => w.Entity.Equals(entity.Name, StringComparison.OrdinalIgnoreCase));
            if (hasWorkflow)
                interfaces.Add("IWorkflowStateful");

            var isMultiTenant = entity.MultiTenant 
                || !string.IsNullOrEmpty(entity.TenantProperty)
                || entity.Properties.Any(p => p.IsTenantKey || p.Attributes.Contains("TenantKey"));

            if (isMultiTenant)
                interfaces.Add("IMultiTenant");

            // Row-level ownership. Composes with tenancy rather than replacing it: the data layer
            // applies both filters, so an owner-scoped multi-tenant entity is narrowed twice.
            var isOwnerScoped = entity.OwnerScoped
                || entity.Properties.Any(p => p.IsOwnerKey || p.Attributes.Contains("OwnerKey"));

            // Grants widen ownership rather than replacing it, so ISharedResource extends
            // IOwnedResource and only one of the two is listed.
            var isShareable = entity.Properties.Any(p => p.IsSharedWithKey || p.Attributes.Contains("SharedWithKey"));

            if (isShareable && isOwnerScoped)
                interfaces.Add("ISharedResource");
            else if (isOwnerScoped)
                interfaces.Add("IOwnedResource");

            var interfaceList = string.Join(", ", interfaces);

            var properties = new List<string>();
            foreach (var prop in entity.Properties)
            {
                if (prop.IsKey)
                    continue;

                var type = MapType(prop.Type);
                var requiredKeyword = prop.Attributes.Contains("Required") ? "required " : "";
                // init, not set. Generated entities are records, and BaseEntity<TId> already
                // sets the house rule: `init` for caller-supplied data (Id), `set` only for the
                // fields the DAL itself stamps (CreatedAtUtc, UpdatedAtUtc, Version). Domain
                // properties are caller-supplied.
                //
                // The DAL never assigns to these directly -- soft delete goes through
                // Builders<T>.Update.Set server-side, and encryption write-back uses reflection,
                // which ignores the init modreq -- so immutability costs nothing at runtime and
                // makes it impossible to mutate an entity between an optimistic-concurrency read
                // and its write.
                var isTenantKey = prop.IsTenantKey
                    || prop.Attributes.Contains("TenantKey")
                    || prop.Name.Equals(entity.TenantProperty, StringComparison.OrdinalIgnoreCase);

                // The tenant key is the one exception, and it is not a style choice: IMultiTenant
                // declares `string TenantId { get; set; }`, and C# will not accept an `init`
                // accessor as an implementation of `set` (CS8854). Every multi-tenant entity the
                // compiler emitted therefore failed to build -- so the framework's headline claim
                // had never compiled, let alone run, and nothing caught it because no test ever
                // built a multi-tenant schema.
                //
                // `set` is also what the DAL needs here: the repository stamps the tenant from the
                // ambient context on insert rather than trusting the request body, which it cannot
                // do through an init-only accessor.
                //
                // The owner key needs `set` for exactly the same two reasons: IOwnedResource
                // declares one, and the repository stamps the owner from the authenticated caller
                // instead of trusting the request body.
                var isOwnerKey = prop.IsOwnerKey || prop.Attributes.Contains("OwnerKey");

                // The grant set needs `set` because ISharedResource declares one -- the same CS8854
                // that made every multi-tenant entity fail to build. Unlike the owner key it is also
                // genuinely caller-settable: granting access to a row you own is an ordinary write.
                var isGrantKey = prop.IsSharedWithKey || prop.Attributes.Contains("SharedWithKey");

                var initKeyword = (isTenantKey && isMultiTenant)
                    || (isOwnerKey && isOwnerScoped)
                    || (isGrantKey && isShareable && isOwnerScoped)
                    ? "get; set"
                    : "get; init";

                var attributes = new List<string>();
                if (isTenantKey)
                {
                    attributes.Add("[TenantKey]");
                }

                if (isOwnerKey)
                {
                    attributes.Add("[OwnerKey]");
                }

                if (isGrantKey)
                {
                    attributes.Add("[SharedWithKey]");
                }

                foreach (var attr in prop.Attributes)
                {
                    if (attr == "UniqueIndex" || attr == "Unique")
                        attributes.Add("[Indexed(Unique = true)]");
                    // "Indexed" is the canonical spelling; "Index" is accepted as an alias.
                    // Only "Index" used to be handled, so schemas written with "Indexed" —
                    // including the shipped showcase sample — silently lost their indexes.
                    else if (attr == "Indexed" || attr == "Index")
                        attributes.Add("[Indexed]");
                    else if (attr == "TextIndex")
                        attributes.Add("[TextIndexed]");
                    else if (attr == "Required")
                        attributes.Add("[Required]");
                    // Encrypt/Mask/MaskEmail all map to [SensitiveData], which is AllowMultiple=false
                    // and carries a single ProtectionType. Emitting one per attribute produced
                    // "Duplicate 'SensitiveData' attribute" (CS0579) whenever a property combined
                    // them — as the showcase's Customer.Email did. They are resolved together
                    // below instead.
                    else if (attr is "Encrypt" or "Mask" or "MaskEmail")
                    {
                        // handled after the loop
                    }
                    else if (attr == "PiiEmail")
                        attributes.Add("[PiiData(PiiType.Email)]");
                    else if (attr == "PiiCreditCard")
                        attributes.Add("[PiiData(PiiType.CreditCard)]");
                    else if (attr.Equals("Email", StringComparison.OrdinalIgnoreCase))
                        attributes.Add("[EmailAddress]");
                    else if (attr.Equals("Url", StringComparison.OrdinalIgnoreCase))
                        attributes.Add("[Url]");
                    else if (attr.Equals("Phone", StringComparison.OrdinalIgnoreCase))
                        attributes.Add("[Phone]");
                    else
                        TryEmitValidationAttribute(attr, attributes);
                }

                // Exactly one [SensitiveData]. Encryption wins over masking when both are asked
                // for: it is the stronger at-rest guarantee, and ProtectionType is not a flags
                // enum so the two cannot be combined.
                var wantsEncrypt = prop.Attributes.Contains("Encrypt");
                var wantsMaskEmail = prop.Attributes.Contains("MaskEmail");
                var wantsMask = prop.Attributes.Contains("Mask");

                // Named categories let one caller be entitled to some masked properties and not
                // others. Omitted, the attribute's own default applies -- so a declaration that names
                // no category still answers to view:pii exactly as before.
                var category = string.IsNullOrWhiteSpace(prop.SensitiveCategory)
                    ? string.Empty
                    : $", Category = \"{CodeGen.Lit(prop.SensitiveCategory!)}\"";

                if (wantsEncrypt)
                    attributes.Add("[SensitiveData(Protection = ProtectionType.Encrypt)]");
                else if (wantsMaskEmail)
                    attributes.Add($"[SensitiveData(Protection = ProtectionType.Mask, MaskingType = MaskingType.Email{category})]");
                else if (wantsMask)
                    attributes.Add($"[SensitiveData(Protection = ProtectionType.Mask{category})]");

                var attributeLines = string.Join("\n    ", attributes);
                var attributeLine = string.IsNullOrEmpty(attributeLines) ? "" : $"    {attributeLines}\n";

                var defaultValue = "";
                if (type == "string")
                    defaultValue = " = string.Empty;";
                else if (type == "bool")
                    defaultValue = " = false;";
                else if (type == "int" || type == "decimal" || type == "double" || type == "float")
                    defaultValue = " = 0;";
                else if (prop.IsEnum)
                    defaultValue = $" = default({type});";
                else if (isGrantKey)
                    // Never null. The read filter enumerates it, and a null grant set on a row would
                    // throw inside the query rather than simply matching nothing.
                    defaultValue = " = new();";

                properties.Add($"{attributeLine}    public {requiredKeyword}{type} {CodeGen.Ident(prop.Name, "Property name")} {{ {initKeyword}; }}{defaultValue}");
            }

            if (entity.SoftDelete)
            {
                // [JsonIgnore] keeps soft-delete bookkeeping off the wire. It is a storage
                // concern: a caller never needs to see it, since the repository already filters
                // deleted rows out of every read, and a caller must not be able to *set* it —
                // otherwise a PUT carrying "isDeleted": true deletes a record through the update
                // route, skipping whatever roles the schema put on DELETE.
                //
                // This only affects System.Text.Json. The MongoDB driver serialises via its own
                // BSON class map and ignores these attributes, so the fields are still persisted
                // and the soft-delete filter still works.
                properties.Add("    [Indexed]\n    [JsonIgnore]\n    public bool IsDeleted { get; init; } = false;");
                properties.Add("    [JsonIgnore]\n    public DateTime? DeletedAt { get; init; }");
            }

            if (hasWorkflow)
            {
                // Deliberately `set` while domain properties are `init`: IWorkflowStateful
                // declares `string CurrentState { get; set; }`, and WorkflowTransitionBehavior
                // assigns these directly when advancing an instance. Making them init-only would
                // fail to satisfy the interface and break the workflow engine. Narrowing this
                // means reworking the engine to produce a new instance via `with`.
                properties.Add("    public string CurrentState { get; set; } = string.Empty;");
                properties.Add("    public string WorkflowId { get; set; } = string.Empty;");
                properties.Add("    public string WorkflowVersion { get; set; } = string.Empty;");
            }

            var propertyLines = string.Join("\n\n", properties);
            if (!string.IsNullOrEmpty(propertyLines))
                propertyLines = "\n" + propertyLines + "\n";

            var partitionAttribute = entity.Partitioned
                ? $"[Partitioned({entity.ArchiveThresholdYears})]\n"
                : "";

            var realTimeAttribute = "";
            if (!entity.RealTime)
            {
                realTimeAttribute = "[RealTime(false)]\n";
            }
            else if (entity.RealTimeRoles != null && entity.RealTimeRoles.Count > 0)
            {
                var rolesList = string.Join(", ", entity.RealTimeRoles.Select(r => $"\"{r}\""));
                realTimeAttribute = $"[RealTime(true, new[] {{ {rolesList} }})]\n";
            }

            // Roles that see the whole tenant rather than only their own rows. Carried on the entity
            // because it is a policy about the entity, not a value on any row.
            var ownerExemptAttribute = "";
            if (isOwnerScoped && entity.OwnerExemptRoles.Count > 0)
            {
                // CodeGen.LitList escapes, so a role name containing a quote cannot terminate the
                // literal and inject code -- the schema-to-code injection this compiler has had once.
                var exemptList = CodeGen.LitList(
                    entity.OwnerExemptRoles.Where(r => !string.IsNullOrWhiteSpace(r)));

                if (!string.IsNullOrEmpty(exemptList))
                    ownerExemptAttribute = $"[OwnerExemptRoles({exemptList})]\n";
            }

            // Roles that see the whole tenant and can change only their own rows. Separate from the
            // list above because that one is per entity, not per operation: read-only oversight -- an
            // auditor, a compliance reviewer -- could not be stated at all.
            if (isOwnerScoped && entity.OwnerReadExemptRoles.Count > 0)
            {
                var readExemptList = CodeGen.LitList(
                    entity.OwnerReadExemptRoles.Where(r => !string.IsNullOrWhiteSpace(r)));

                if (!string.IsNullOrEmpty(readExemptList))
                    ownerExemptAttribute += $"[OwnerReadExemptRoles({readExemptList})]\n";
            }

            // Entity-level indexes. Previously parsed, validated and then dropped on the floor:
            // nothing in the emitter referenced entity.Indexes, so a declared composite index was
            // never created and queries silently fell back to collection scans.
            var compoundIndexAttribute = BuildCompoundIndexAttributes(entity);

            // A declared kafkaTopic reached the generated consumer and stopped there: the publisher
            // derived its own name from the event type, so the consumer subscribed to a topic nothing
            // was ever written to. This attribute is how the declaration reaches the outbox, which
            // records it on the message and dispatches there.
            var kafkaTopicAttribute = !string.IsNullOrWhiteSpace(entity.KafkaTopic)
                ? $"[KafkaTopic(\"{CodeGen.Lit(entity.KafkaTopic!)}\")]\n"
                : "";

            var needAttributes = entity.Partitioned || !entity.RealTime || (entity.RealTimeRoles != null && entity.RealTimeRoles.Count > 0) || !string.IsNullOrEmpty(compoundIndexAttribute) || !string.IsNullOrEmpty(kafkaTopicAttribute);
            var extraImports = needAttributes
                ? "\nusing Foundry.Core.Attributes;"
                : "";
            if (hasWorkflow)
                extraImports += "\nusing Foundry.Rules;";
            if (isMultiTenant)
                extraImports += "\nusing Foundry.Core.Tenant;";
            extraImports += "\nusing Foundry.Core.Security;";

            // The grant set is a List<string>, which needs the namespace its type lives in. Emitted
            // only when there is one, so an entity without grants keeps the using list it had.
            if (isShareable)
                extraImports += "\nusing System.Collections.Generic;";

            return $@"using System;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using MongoDB.Bson;
using Foundry.Core.Entities;{extraImports}

namespace {CodeGen.Ns(@namespace)};

{partitionAttribute}{realTimeAttribute}{ownerExemptAttribute}{compoundIndexAttribute}{kafkaTopicAttribute}public partial record {CodeGen.Ident(entity.Name, "Entity name")} : {interfaceList}
{{{propertyLines}}}";
        }

        private static string MapType(string schemaType) => Vocabulary.MapType(schemaType);

        /// <summary>
        /// Emits one <c>[CompoundIndex]</c> per entity-level index declaration.
        /// </summary>
        /// <remarks>
        /// A single-field entity index whose property already carries <c>Indexed</c> or
        /// <c>Unique</c> is skipped. Emitting both would ask MongoDB to create two indexes over
        /// the same key pattern under different names, which the server rejects at startup — so
        /// the redundant declaration is dropped in favour of the property attribute.
        /// </remarks>
        private static string BuildCompoundIndexAttributes(Entity entity)
        {
            var indexes = entity.Indexes ?? new List<Index>();
            if (indexes.Count == 0) return string.Empty;

            var properties = entity.Properties ?? new List<Property>();
            var lines = new List<string>();

            foreach (var index in indexes)
            {
                var fields = (index.Fields ?? new List<string>())
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .ToList();

                if (fields.Count == 0) continue;

                if (fields.Count == 1)
                {
                    var covered = properties.Any(p =>
                        string.Equals(p.Name, fields[0], StringComparison.OrdinalIgnoreCase)
                        && (p.Attributes ?? new List<string>()).Any(a =>
                            a is "Indexed" or "Index" or "Unique" or "UniqueIndex"));

                    if (covered) continue;
                }

                var fieldArgs = string.Join(", ", fields.Select(f => $"\"{CodeGen.Lit(f)}\""));
                var options = new List<string>();

                if (index.Unique) options.Add("Unique = true");
                if (!string.IsNullOrWhiteSpace(index.Name)) options.Add($"Name = \"{CodeGen.Lit(index.Name)}\"");

                var optionText = options.Count > 0 ? $", {string.Join(", ", options)}" : "";
                lines.Add($"[CompoundIndex({fieldArgs}{optionText})]");
            }

            return lines.Count == 0 ? string.Empty : string.Join("\n", lines) + "\n";
        }

        /// <summary>
        /// Emits a validation attribute if <paramref name="attr"/> is a recognised,
        /// safely-renderable parameterised attribute.
        /// </summary>
        /// <returns>True when the attribute was handled and appended to <paramref name="attributes"/>.</returns>
        /// <remarks>
        /// This replaces the previous <c>attributes.Add($"[{attr}]")</c> pass-through. That form
        /// spliced raw schema text into generated source: an attribute of
        /// <c>X)] public class Evil {{ }} [Obsolete(</c> closed the attribute and opened a new
        /// type declaration. Because the schema can be authored by a local AI model, every
        /// argument list is now parsed and constrained before emission, and anything that does
        /// not parse is dropped rather than emitted.
        /// </remarks>
        private static bool TryEmitValidationAttribute(string attr, List<string> attributes)
        {
            if (!CodeGen.TryParseAttribute(attr, out var name, out var args))
                return false;

            if (string.IsNullOrEmpty(args))
                return false;

            switch (name.ToLowerInvariant())
            {
                case "minlength":
                    attributes.Add($"[MinLength({args})]");
                    return true;

                case "maxlength":
                    attributes.Add($"[MaxLength({args})]");
                    return true;

                case "range":
                    attributes.Add($"[Range({args})]");
                    return true;

                case "regex":
                    // The parser guarantees args is a quoted string with no embedded quote,
                    // backslash or brace, so it is safe to place inside the attribute.
                    //
                    // [GeneratedRegex] would compile the pattern at build time rather than
                    // interpreting it per validation, but it requires a partial method on a
                    // partial type and cannot be applied to a property. Emitting the compiled
                    // regex is therefore a change to the validation pipeline, not to this
                    // attribute — tracked separately rather than faked here.
                    attributes.Add($"[RegularExpression({args})]");
                    return true;

                default:
                    return false;
            }
        }

        private static string GenerateDto(DtoModel dto, string @namespace)
        {
            var properties = new List<string>();
            foreach (var prop in dto.Properties)
            {
                var type = MapType(prop.Type);
                var requiredKeyword = prop.IsRequired ? "required " : "";
                var initKeyword = "get; init";

                var attributes = new List<string>();
                foreach (var attr in prop.Attributes)
                {
                    if (attr == "Required")
                        attributes.Add("[Required]");
                    else if (attr.Equals("Email", StringComparison.OrdinalIgnoreCase))
                        attributes.Add("[EmailAddress]");
                    else if (attr.Equals("Url", StringComparison.OrdinalIgnoreCase))
                        attributes.Add("[Url]");
                    else if (attr.Equals("Phone", StringComparison.OrdinalIgnoreCase))
                        attributes.Add("[Phone]");
                    else
                        TryEmitValidationAttribute(attr, attributes);
                }

                var attributeLines = string.Join("\n    ", attributes);
                var attributeLine = string.IsNullOrEmpty(attributeLines) ? "" : $"    {attributeLines}\n";

                var defaultValue = "";
                if (type == "string")
                    defaultValue = " = string.Empty;";
                else if (type == "bool")
                    defaultValue = " = false;";
                else if (type == "int" || type == "decimal" || type == "double" || type == "float")
                    defaultValue = " = 0;";

                properties.Add($"{attributeLine}    public {requiredKeyword}{type} {CodeGen.Ident(prop.Name, "DTO property name")} {{ {initKeyword}; }}{defaultValue}");
            }

            var propertyLines = string.Join("\n\n", properties);
            if (!string.IsNullOrEmpty(propertyLines))
                propertyLines = "\n" + propertyLines + "\n";

            return $@"using System;
using System.ComponentModel.DataAnnotations;
using MongoDB.Bson;

namespace {CodeGen.Ns(@namespace)};

public partial record {CodeGen.Ident(dto.Name, "DTO name")}
{{{propertyLines}}}";
        }

        /// <summary>
        /// The request property an Update endpoint reads its row by.
        /// </summary>
        private static string IdentifierPropertyFor(CustomEndpoint ep)
            => string.IsNullOrWhiteSpace(ep.FilterSourceValue) ? "Id" : ep.FilterSourceValue!;

        /// <summary>
        /// The C# type of <paramref name="propertyName"/> on <paramref name="entityName"/>.
        /// </summary>
        /// <remarks>
        /// Falls back to <c>string</c> when the entity or property cannot be resolved, which is the
        /// shape everything used unconditionally before. The validator rejects a filter or
        /// assignment naming a property that does not exist, so the fallback is for schemas that
        /// declare no target at all rather than for ones that name the wrong thing.
        /// </remarks>
        private static string TypeOfEntityProperty(SchemaModel schema, string? entityName, string? propertyName)
        {
            if (string.IsNullOrWhiteSpace(entityName) || string.IsNullOrWhiteSpace(propertyName)) return "string";

            var entity = schema.Entities?.FirstOrDefault(
                e => e.Name.Equals(entityName, StringComparison.OrdinalIgnoreCase));

            var property = entity?.Properties?.FirstOrDefault(
                p => p.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase));

            if (property == null) return "string";

            // An enum property's type is the enum's own name, which is emitted in this namespace.
            return property.IsEnum ? property.Type : MapType(property.Type);
        }

        /// <summary>
        /// Renders one comparison for a Query endpoint's declared filter.
        /// </summary>
        /// <remarks>
        /// <c>filterOperator</c> was read by nothing: every Query endpoint emitted
        /// <c>x.Field.ToString() == request.Value</c>, so a schema declaring <c>GreaterThan</c> got
        /// an equality test and the endpoint answered with the wrong rows rather than failing. That
        /// is the quietest way a generator can be wrong, and the reason the operator is now a
        /// closed set: an unrecognised one stops the compile instead of silently becoming equality.
        /// </remarks>
        private static string ComparisonFor(CustomEndpoint ep)
        {
            var field = string.IsNullOrWhiteSpace(ep.FilterField) ? "Id" : ep.FilterField!;
            var value = $"request.{IdentifierPropertyFor(ep)}";
            var op = (ep.FilterOperator ?? "Equals").Trim();

            return op.ToUpperInvariant() switch
            {
                "" or "EQUALS" or "EQ" or "==" => $"x.{field} == {value}",
                "NOTEQUALS" or "NEQ" or "!=" => $"x.{field} != {value}",
                "GREATERTHAN" or "GT" or ">" => $"x.{field} > {value}",
                "GREATERTHANOREQUAL" or "GTE" or ">=" => $"x.{field} >= {value}",
                "LESSTHAN" or "LT" or "<" => $"x.{field} < {value}",
                "LESSTHANOREQUAL" or "LTE" or "<=" => $"x.{field} <= {value}",
                "CONTAINS" => $"x.{field}.Contains({value})",
                "STARTSWITH" => $"x.{field}.StartsWith({value})",
                _ => throw new UnsafeSchemaValueException(
                    DiagnosticCatalog.InvalidIdentifier,
                    $"filter operator '{op}' is not one of Equals, NotEquals, GreaterThan, "
                    + "GreaterThanOrEqual, LessThan, LessThanOrEqual, Contains or StartsWith.")
            };
        }

        /// <summary>
        /// Computes the MediatR response type for a custom endpoint.
        /// </summary>
        private static string ResponseTypeFor(CustomEndpoint ep)
            => ep.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)
                ? "System.Collections.Generic.IReadOnlyList<" + (string.IsNullOrEmpty(ep.TargetEntity) ? "object" : ep.TargetEntity) + ">"
                : "bool";

        /// <summary>
        /// Emits the MediatR request record a custom endpoint's handler and rules are typed against.
        /// </summary>
        /// <remarks>
        /// Properties are derived from what the generated handler actually reads — the filter
        /// source value and any assignment sources — so the scaffolded body compiles as written.
        /// </remarks>
        private static string GenerateCustomEndpointRequest(CustomEndpoint ep, string @namespace, SchemaModel schema)
        {
            var properties = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string? name, string type)
            {
                if (string.IsNullOrWhiteSpace(name) || !seen.Add(name!)) return;
                properties.Add($"    public {type} {CodeGen.Ident(name, "Request property")} {{ get; init; }}"
                               + (type == "string" ? " = string.Empty;" : ""));
            }

            // An Update reads the row before it writes it, and the identifier it reads by has to be
            // on the request. The handler emitted `request.Id` whether or not anything declared it,
            // so an Update endpoint that named no filterSourceValue produced a handler referring to
            // a property the request did not have -- CS1061, on a project the CLI called
            // ready-to-run. The identifier is now declared wherever the handler will read it.
            if (ep.OperationType.Equals("Update", StringComparison.OrdinalIgnoreCase))
                Add(IdentifierPropertyFor(ep), "string");

            // Typed from the property each one is compared or assigned to, rather than string.
            // `Status = request.NewStatus` does not compile when Status is an enum and the request
            // field is a string, and `x.StockQuantity.ToString() == request.MinimumStock` compiles
            // but compares the wrong things -- so the two shapes failed in the two different ways a
            // generator can fail, loudly and silently.
            if (!string.IsNullOrWhiteSpace(ep.FilterSourceValue))
                Add(ep.FilterSourceValue, TypeOfEntityProperty(schema, ep.TargetEntity, ep.FilterField));

            foreach (var assignment in ep.Assignments ?? new List<AssignmentRule>())
                Add(assignment.SourceValue, TypeOfEntityProperty(schema, ep.TargetEntity, assignment.EntityProperty));

            var body = properties.Count == 0
                ? "\n    // Add request properties here, or in a *.Custom.cs partial.\n"
                : "\n" + string.Join("\n\n", properties) + "\n";

            // MongoDB.Bson is here because a request property is typed from the entity property it
            // filters or assigns, and an ObjectId key is the commonest thing to reassign. The
            // handler emitter has always carried this using; the request emitter had not, so a
            // custom endpoint touching an ObjectId property emitted a record that could not compile
            // -- CS0246 on generated code, in a project the CLI had just called ready-to-run.
            return $@"using System;
using MediatR;
using MongoDB.Bson;

namespace {CodeGen.Ns(@namespace)};

/// <summary>
/// Request for the '{CodeGen.Lit(ep.Route)}' endpoint.
/// </summary>
public partial record {CodeGen.Ident(ep.RequestType, "Request type")} : IRequest<{ResponseTypeFor(ep)}>
{{{body}}}
";
        }

        private static string GenerateHandler(CustomEndpoint ep, string @namespace, SchemaModel schema)
        {
            var handlerName = ep.RequestType + "Handler";

            // Previously this derived a response type by string-replacing "Query"/"Request" with
            // "Response" — a type the compiler never emitted, so every GET endpoint produced a
            // handler that could not build. The response is now a projection of the target entity.
            var responseType = ResponseTypeFor(ep);

            var repoType = !string.IsNullOrEmpty(ep.TargetEntity) ? $"IRepository<{ep.TargetEntity}>" : null;

            var newResponseExpr = $"new {responseType}";
            var newDtoExpr = $"new {ep.TargetEntity}Dto";
            var newEntityExpr = $"new {ep.TargetEntity}";

            string body = "";
            if (ep.OperationType.Equals("Query", StringComparison.OrdinalIgnoreCase))
            {
                body = $@"        var items = await _repository.FindManyAsync(
            x => {ComparisonFor(ep)},
            ct: cancellationToken);

        // Returning the entities directly. Project them into a DTO here if the API should not
        // expose the full entity shape — declare that DTO in the schema's ""dtos"" section.
        return items;";
            }
            else if (ep.OperationType.Equals("Update", StringComparison.OrdinalIgnoreCase))
            {
                // Generated properties are init-only, so mutation goes through a `with`
                // expression rather than assignment. This is also the safer shape under
                // optimistic concurrency: the entity read from the repository is never altered
                // in place, so nothing can observe it half-updated.
                var assignments = (ep.Assignments ?? new List<AssignmentRule>())
                    .Select(a =>
                        $"            {CodeGen.Ident(a.EntityProperty, "Entity property")} = "
                        + $"request.{CodeGen.Ident(a.SourceValue, "Request property")},")
                    .ToList();

                var withBlock = assignments.Count > 0
                    ? $@"        entity = entity with
        {{
{string.Join("\n", assignments)}
        }};"
                    : "        // No assignments declared on this endpoint; set the properties to update here.";

                body = $@"        var entity = await _repository.GetByIdAsync(request.{IdentifierPropertyFor(ep)});
        if (entity == null)
        {{
            return false;
        }}

        // Apply visual assignments
{withBlock}

        await _repository.UpdateAsync(entity);
        return true;";
            }
            else if (ep.OperationType.Equals("Insert", StringComparison.OrdinalIgnoreCase))
            {
                // Deliberately not constructing the entity inline: entities with [Required]
                // properties compile to `required` members, so an empty object initializer is a
                // guaranteed CS9035. This is a scaffold the developer owns, so it states the work
                // plainly and compiles as written.
                body = $@"        // TODO: map the request onto a {ep.TargetEntity} and insert it, for example:
        //
        //     var entity = new {ep.TargetEntity} {{ /* set required properties */ }};
        //     await _repository.InsertAsync(entity, ct: cancellationToken);
        //     return true;

        throw new NotImplementedException(
            ""Map {ep.RequestType} onto {ep.TargetEntity} and insert it."");";
            }
            else
            {
                body = @"        // Write your custom MediatR query/command logic here
        throw new NotImplementedException(""Custom logic handler."");";
            }

            var fieldDeclaration = repoType != null ? $"    private readonly {repoType} _repository;\n" : "";
            
            var constructor = repoType != null 
                ? $@"    public {handlerName}({repoType} repository)
    {{
        _repository = repository;
    }}"
                : $@"    public {handlerName}()
    {{
    }}";

            return $@"using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using MongoDB.Bson;
using Foundry.Core.Entities;
using Foundry.Mongo.Repositories;
using {@namespace};

namespace {@namespace}.Handlers;

public class {handlerName} : IRequestHandler<{ep.RequestType}, {responseType}>
{{
{fieldDeclaration}
{constructor}

    public async Task<{responseType}> Handle({ep.RequestType} request, CancellationToken cancellationToken)
    {{
{body}
    }}
}}";
        }

        private static string GenerateCustomEndpointRuleStub(string ruleName, string requestType, string ns)
        {
            return $@"using System.Threading;
using System.Threading.Tasks;
using Foundry.Rules;

namespace {ns}.Rules;

/// <summary>
/// Custom business rule validator for {requestType}.
/// </summary>
public class {ruleName} : IBusinessRule<{ns}.{requestType}>
{{
    public Task<RuleResult> ValidateAsync({ns}.{requestType} request, CancellationToken ct)
    {{
        // TODO: Implement custom business policy validation logic
        return Task.FromResult(RuleResult.Success());
    }}
}}
";
        }

        /// <summary>
        /// The MediatR request an entity CRUD rule validates, for one HTTP method.
        /// </summary>
        /// <remarks>
        /// Shared by the stub and by its DI registration, so the two cannot name different types --
        /// a registration bound to the wrong request compiles and never fires.
        /// </remarks>
        private static string EntityRuleRequestType(string method, string entityName, string ns)
            => method.ToUpperInvariant() switch
            {
                "POST" => $"InsertCommand<{ns}.{entityName}>",
                "PUT" => $"UpdateCommand<{ns}.{entityName}>",
                "DELETE" => $"DeleteCommand<{ns}.{entityName}>",
                "GET_BY_ID" => $"GetByIdQuery<{ns}.{entityName}>",
                "GET" => $"FindManyQuery<{ns}.{entityName}>",
                _ => "object"
            };

        private static string GenerateEntityRuleStub(string ruleName, string method, string entityName, string ns)
        {
            var requestType = EntityRuleRequestType(method, entityName, ns);

            return $@"using System.Threading;
using System.Threading.Tasks;
using Foundry.Rules;
using Foundry.Api.MediatR;

namespace {ns}.Rules;

/// <summary>
/// Entity CRUD business rule validator for {entityName} on {method}.
/// </summary>
public class {ruleName} : IBusinessRule<{requestType}>
{{
    public Task<RuleResult> ValidateAsync({requestType} request, CancellationToken ct)
    {{
        // TODO: Implement custom business policy validation logic
        return Task.FromResult(RuleResult.Success());
    }}
}}
";
        }

        private static string GenerateTransitionCommand(WorkflowTransitionModel transition, Entity entity, string @namespace)
        {
            var keyProperty = entity.Properties.FirstOrDefault(p => p.IsKey);
            var keyType = keyProperty?.Type ?? "ObjectId";
            if (keyType.Equals("ObjectId", StringComparison.OrdinalIgnoreCase))
                keyType = "MongoDB.Bson.ObjectId";

            return $@"using System;
using MediatR;
using Foundry.Rules;
using MongoDB.Bson;

namespace {@namespace}.Commands;

/// <summary>
/// Command to trigger the workflow transition '{transition.Name}' from '{transition.FromState}' to '{transition.ToState}'.
/// </summary>
// IRequest<Unit> rather than the void IRequest. ISender.Send returns a bare Task for the void form,
// which the generated endpoint cannot assign to a result -- so a transition command could be built
// and dispatched from code, and never routed. Unit is what MediatR provides for exactly this.
public partial record {CodeGen.Ident(transition.Trigger, "Transition trigger")} : IRequest<Unit>, IWorkflowTransitionRequest
{{
    /// <summary>
    /// Gets the unique ID of the target entity document.
    /// </summary>
    public string EntityId {{ get; init; }} = string.Empty;

    /// <inheritdoc />
    string IWorkflowTransitionRequest.EntityId => EntityId;

    /// <inheritdoc />
    public string EntityType => ""{CodeGen.Lit(entity.Name)}"";

    /// <inheritdoc />
    // Falls back to the trigger exactly as ApiManifestGenerator does. The engine matches a request
    // to a definition on this value, so the two must agree: when a transition declares no id, an
    // empty string here would match the first transition in the workflow, or none at all.
    public string TransitionId => ""{CodeGen.Lit(ApiManifestGenerator.TransitionId(transition))}"";

    /// <inheritdoc />
    public string FromState => ""{CodeGen.Lit(transition.FromState)}"";

    /// <inheritdoc />
    public string ToState => ""{CodeGen.Lit(transition.ToState)}"";
}}
";
        }

        private static string GenerateTransitionHandler(WorkflowTransitionModel transition, string @namespace)
        {
            var trigger = CodeGen.Ident(transition.Trigger, "Transition trigger");

            // The command lives in '<namespace>.Commands' and this handler in '<namespace>.Handlers'.
            // Without the using directive the handler names a type it cannot see (CS0246), so every
            // workflow the compiler emitted produced a project that did not build. Nothing caught it
            // because no test and no sample had ever compiled a schema with a workflow in it.
            return $@"using System;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using {@namespace}.Commands;

namespace {@namespace}.Handlers;

/// <summary>
/// Handler for {transition.Trigger} workflow state transition command.
/// </summary>
public partial class {trigger}Handler : IRequestHandler<{trigger}, Unit>
{{
    /// <inheritdoc />
    public Task<Unit> Handle({trigger} request, CancellationToken ct)
    {{
        // State updates, roles audits, guard evaluation, and logging are automatically processed by WorkflowTransitionBehavior.
        return Task.FromResult(Unit.Value);
    }}
}}
";
        }

        /// <summary>
        /// Builds a WorkflowActionConfig object initializer string from a WorkflowActionModel.
        /// Recursively handles CompensateWith for nested actions.
        /// </summary>
        private static string BuildActionConfigInitializer(WorkflowActionModel action)
        {
            var props = new List<string> { $@"Type = ""{action.Type}""" };
            if (action.RequestType != null) props.Add($@"RequestType = ""{action.RequestType}""");
            if (action.Method != null) props.Add($@"Method = ""{action.Method}""");
            if (action.Url != null) props.Add($@"Url = ""{action.Url}""");
            if (action.PayloadTemplate != null) props.Add($@"PayloadTemplate = ""{action.PayloadTemplate.Replace("\"", "\\\"")}""");
            if (action.BodyTemplate != null) props.Add($@"BodyTemplate = ""{action.BodyTemplate.Replace("\"", "\\\"")}""");
            props.Add($@"Retryable = {action.Retryable.ToString().ToLower()}");
            if (action.CompensateWith != null) props.Add($@"CompensateWith = {BuildActionConfigInitializer(action.CompensateWith)}");
            return $"new WorkflowActionConfig {{ {string.Join(", ", props)} }}";
        }
    }
}