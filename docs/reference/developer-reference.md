# Foundry Developer Reference

Complete reference for every public function, type and command the Foundry framework exposes.

This document is derived from the source, not from an earlier document. Every signature below was
read out of the file it lives in, and every file path is a link into the tree. Where a function's
behaviour is not obvious from its signature — a rule that is easy to get wrong, a failure mode that
is easy to miss — that is stated rather than left implied.

**Version:** framework `v1.0.0`, `net10.0`, MongoDB driver 3.x, MediatR 12.x.
**Companion documents:** [`README.md`](../../README.md) (orientation),
[`docs/getting-started/quick-start-guide.md`](../getting-started/quick-start-guide.md) (first app),
[`docs/engineering-assessment.md`](../engineering-assessment.md) (what is proven, and how).

---

## Table of contents

| Part | Contents |
| :--- | :--- |
| [0. The pipeline](#0-the-pipeline) | How the pieces relate; where each function runs |
| [1. CLI](#1-cli-foundry) | Every `foundry` command, flag and exit code |
| [2. The IR](#2-the-ir-schema-language) | Schema model, vocabulary, attributes, diagnostics |
| [3. Compiler API](#3-compiler-api-foundryschemacompiler) | Generators, exporters, validator, AI toolchain |
| [4. Generated code](#4-generated-code-surface) | What the compiler emits and how a host wires it |
| [5. Foundry.Core](#5-foundrycore) | Entities, audit, paging, search, security, tenancy |
| [6. Foundry.Mongo](#6-foundrymongo) | `IRepository<T>`, unit of work, workers, encryption |
| [7. Foundry.Api](#7-foundryapi) | MediatR pipeline, security, GraphQL, workflow hosting |
| [8. Foundry.Rules](#8-foundryrules) | Business rules and the workflow engine |
| [9. Foundry.Kafka](#9-foundrykafka) | Producer, consumer, outbox dispatcher |
| [10. Foundry.RealTime](#10-foundryrealtime) | SignalR, WebSockets, SSE, access policy |
| [11. Foundry.Connectors](#11-foundryconnectors) | REST, SOAP, GraphQL outbound connectors |
| [12. Foundry.FileIO](#12-foundryfileio) | CSV/Excel streaming, file security |
| [13. Foundry.Testing](#13-foundrytesting) | Suite generation, mock data, reports |
| [14. Tooling surfaces](#14-tooling-surfaces) | Studio backend HTTP API, Studio, VS Code, LSP |
| [15. Appendices](#15-appendices) | Diagnostics, env vars, host wiring order |

---

## 0. The pipeline

Foundry has one direction of flow. Everything downstream of the IR is derived; nothing derived is
edited by hand.

```
  Studio canvas ──foundry migrate──┐
                                   ▼
  natural language ──foundry ai──► *.ir.json ──foundry validate──► gate
                                       │
                                       ├──foundry schema build──► Generated/*.cs + api-manifest.json
                                       │                              │
                                       │                              ├─► ApiRouteGenerator (Roslyn)
                                       │                              │      emits MapGeneratedEndpoints
                                       │                              └─► AddDynamicGraphQL / MapWorkflowHistory
                                       │
                                       ├──foundry sdk──► TypeScript | C# | Python client
                                       ├──foundry export──► OpenAPI | AsyncAPI | Postman | Mermaid
                                       └──foundry test──► xUnit suites over the live app
```

Three rules hold this together, and each exists because it was once violated:

1. **One producer per contract.** The route an entity serves is decided by
   `ApiManifestGenerator.RouteFor` and nowhere else. That rule was reimplemented — wrongly — in four
   places before it was made public.
2. **Scaffolds are written once.** Files classified `EmitKind.Scaffold` hold your logic and are never
   overwritten by a later compile.
3. **Silence is a bug.** A gate that cannot fail, a client that returns an error as data, or a
   compile that reports success while emitting nothing is treated as a defect, not a nicety.

---

## 1. CLI (`foundry`)

Entry point: [`foundry-cli/src/Foundry.Cli/Program.cs:16`](../../foundry-cli/src/Foundry.Cli/Program.cs#L16).

All commands return a process exit code. **`foundry` with no arguments prints help and exits `1`** —
so `foundry $UNSET_VAR` fails rather than looking like a successful no-op.

### 1.1 Command summary

| Command | Purpose | Exit |
| :--- | :--- | :--- |
| `new` \| `init` \| `create` | Scaffold a runnable API project | 0 / 1 |
| `schema build` \| `schema compile` | Compile IR → C# + manifest | compiler's code |
| `schema studio` | Alias for `studio` | 0 / 1 |
| `studio` | Serve the embedded Studio web IDE | 0 / 1 |
| `api` | Run a Foundry API project | `dotnet run`'s code |
| `validate` | Validate an IR document (CI gate) | 0 = clean, 1 = errors |
| `migrate` | Studio canvas → normative IR | 0 / 1 |
| `export` | OpenAPI / AsyncAPI / Postman / Mermaid | 0 / 1 |
| `sdk` | Generate a client SDK | 0 / 1 |
| `test` | Generate xUnit suites + report | 0 / 1 |
| `generate ci` | Write a GitHub Actions workflow | 0 |
| `doctor` | Check the local environment | 0 = usable, 1 = a prerequisite is missing |
| `lsp` | Language server over stdio | 0 |
| `ai` | Natural language → validated IR | 0 / 1 |
| `ai-spec` | Write the AI skill bundle | 0 / 1 |
| `eval` | Measure local-model IR accuracy | 0 / 1 |
| `version` | Print framework version | 0 |

Dispatch and `--help` both read one table — `Program.Commands`
([`Program.cs`](../../foundry-cli/src/Foundry.Cli/Program.cs)) — so every command the CLI accepts is
listed, and an unrecognised one is rejected by name rather than silently printing help. They were
two separate lists for a long time and disagreed: `schema`, `api`, `sdk`, `test` and `lsp` all
worked and none of them appeared in `--help`.

### 1.2 `foundry new <ProjectName> [--schema|-s <path>]`

Scaffolds a complete project: `Program.cs` host, `.csproj` with the framework references,
`api-manifest.json`, and — when `--schema` is given — the compiled `Generated/` tree.

The scaffolder is schema-driven, not directory-driven:

- GraphQL wiring (`AddDynamicGraphQL(manifest)` + `app.MapGraphQL()`) is emitted only when at least
  one entity sets `enableGraphQL`.
- `using {Project}.Domain.Rules;` and `AddGeneratedBusinessRules()` are emitted only when the schema
  declares rules. Emitting them unconditionally produced a project that did not compile.
- The compiler's exit code is propagated. A failed compile no longer yields a "ready to run" banner.

### 1.3 `foundry schema build -i <schema.json> -o <dir> [-m <manifest.json>]`

Runs [`Foundry.Schema.Compiler.Program.Main`](../../foundry-schema/compiler/Program.cs#L20)
in-process and returns its exit code.

| Flag | Alias | Required | Default |
| :--- | :--- | :--- | :--- |
| `--input` | `-i` | yes | — |
| `--output` | `-o` | yes | — |
| `--manifest` | `-m` | no | `<output>/api-manifest.json` |

Behaviour:

1. Raw-document validation runs **before** deserialisation, so a Studio canvas file (no `entities`
   property) is rejected with `FDY1010` instead of deserialising into an empty-but-valid model.
2. Full `SchemaValidator.Validate` runs next. **Any error means no files are written at all.**
3. Files are written; `EmitKind.Scaffold` targets that already exist are preserved and reported.
4. `api-manifest.json` is always written. Without it, the Roslyn analyser emits empty registrations
   and every entity route answers 404.

### 1.4 `foundry validate <schema.json>`

Prints coded diagnostics via `DiagnosticBag.Render()`. Exit `0` when clean (warnings alone still
exit `0`), `1` on any error or malformed JSON. This is the CI gate form.

### 1.5 `foundry migrate <canvas.json> [--out|-o <path>] [--in-place]`

Converts a Studio canvas document (`nodes`/`edges`) into normative IR (`entities`). Validates before
writing. Default output path is derived by `DefaultIrPathFor` (`x.foundry.json` → `x.ir.json`).

This command exists because `FDY1010` told the reader to run it, and for a long time it did not
exist — the VS Code "New Schema" command emitted exactly the document that trips that diagnostic.

### 1.6 `foundry export -f <format> [-i <in>] [-o <out>]`

| `-f` value | Producer | Default output |
| :--- | :--- | :--- |
| `openapi`, `swagger` | `OpenApiExporter.ExportJson` | `openapi_spec.json` |
| `asyncapi`, `kafka` | `AsyncApiExporter.ExportJson` | `asyncapi_spec.json` |
| `postman` | `PostmanExporter.ExportJson` | `postman_collection.json` |
| `mermaid` | `MermaidExporter.ExportMermaid` | `schema-diagram.mmd` |

Default input is `domain.foundry.json`. An unsupported format throws `NotSupportedException`.

### 1.7 `foundry sdk -l <lang> [-i <in>] [-o <out>]`

| `-l` value | Generator | Default output |
| :--- | :--- | :--- |
| `ts`, `typescript` | `TypeScriptSdkGenerator` | `foundryClient.ts` |
| `cs`, `csharp` | `CsharpSdkGenerator` | `FoundryClient.cs` |
| `py`, `python` | `PythonSdkGenerator` | `foundry_client.py` |

### 1.8 `foundry test [-i <in>] [-o <dir>] [-r <report>]`

Generates xUnit suites into `-o` (default `./tests`) and writes an HTML + Markdown report to `-r`
(default `test-report.html`).

**This command generates suites; it does not run them.** The report is therefore written with
`totalTests: 0, passedTests: 0, failedTests: 0` so it reads "no tests were executed" — which is what
happened. It previously invented a full green run.

### 1.9 `foundry api [--project|-p <csproj>] [-- <args>]`

Resolves a single `*.csproj` in the current directory (or takes `--project`), warns if it does not
reference `Foundry.Api`, then shells `dotnet run --project <csproj> [-- args]`.

### 1.10 `foundry ai "<instruction>" [flags]`

Natural language → validated IR via a local Ollama model, with schema-constrained decoding and a
generate–validate–repair loop.

| Flag | Meaning | Default |
| :--- | :--- | :--- |
| `--host` | Ollama host | `FOUNDRY_OLLAMA_HOST`, else `http://localhost:11434` |
| `--model` | Model tag | `FOUNDRY_OLLAMA_MODEL`, else `qwen3-coder:30b` |
| `--out`, `-o` | Write the IR here | stdout |
| `--base` | Existing IR to modify rather than create | — |
| `--check` | Probe reachability + model presence, then exit | — |

Each repair attempt is printed with its error count.

### 1.11 `foundry ai-spec [--out <dir>]`

Writes the AI skill bundle — IR JSON Schema, the attribute vocabulary, worked examples, and the
system prompt — for driving a local model. See [`AiSpecBundle`](#312-aispecbundle).

### 1.12 `foundry eval [flags]`

Runs the local-model accuracy harness per IR construct.

| Flag | Meaning |
| :--- | :--- |
| `--runs N` | Repetitions per case |
| `--construct <name>` | Filter to one construct |
| `--case <id>` | Filter to one case |
| `--difficulty core\|hard` | Filter by difficulty |
| `--out`, `-o` | Markdown report path |
| `--json` | JSON report path |
| `--min-pass <0..1>` | Exit non-zero below this pass rate (CI gate) |
| `--list` | List cases and exit |

### 1.13 `foundry doctor`, `generate ci`, `studio`, `lsp`, `version`

- **`doctor`** — probes the local environment and reports what it found. Exits **1** if a required
  item is missing, **0** otherwise, so it can gate a setup script.

  | Check | Severity if absent | What it does |
  | :--- | :--- | :--- |
  | Platform | — | OS, architecture, CLI and runtime versions |
  | Studio bundle | warning | Whether *this build* embeds the Studio UI |
  | dotnet SDK | **failure** | Resolves `dotnet` on PATH, then runs `dotnet --version` |
  | Node.js | warning | `node --version`; needed only to rebuild Studio or the extension |
  | Docker | warning | `docker info`; needed for the compose stack |
  | MongoDB | warning | TCP connect, honouring `MONGODB_CONNECTION` (default `localhost:27017`) |
  | Kafka | warning | TCP connect to `localhost:9092` |
  | Ollama | warning | `GET {FOUNDRY_OLLAMA_HOST}/api/tags`, and whether the configured model is present |

  Only the SDK is fatal: a database and a broker are needed to *run* an application, not to model,
  validate or compile one. The SDK is resolved against PATH explicitly rather than by handing
  `"dotnet"` to `Process.Start` — when the CLI runs as `dotnet foundry.dll`, that resolves the host
  which launched it whatever PATH says, so the probe would have reported a healthy SDK on a machine
  where `dotnet build` in a shell fails. PATH is also the question that matters, because PATH is
  what the user's own shell will search.

  This command previously printed three version strings and then declared the environment "fully
  healthy" unconditionally, having probed nothing at all.
- **`generate ci [--provider github]`** — writes `.github/workflows/ci.yml`.
- **`studio [--port 5000]`** — serves the embedded Studio SPA.
- **`lsp`** — LSP server over stdio; implements `initialize` and `textDocument/completion`
  ([`LspServer.cs`](../../foundry-cli/src/Foundry.Cli/Lsp/LspServer.cs)). Framed with a
  byte-counted `Content-Length`.
- **`version`** — prints `Foundry Framework Unified Executable v1.0.0 (.NET 10)`.

---

## 2. The IR (schema language)

Model types: [`foundry-schema/compiler/SchemaModels.cs`](../../foundry-schema/compiler/SchemaModels.cs).
The machine-readable JSON Schema is produced by [`IrSchemaGenerator.Generate()`](#39-irschemagenerator)
and gated against drift in CI.

### 2.1 `SchemaModel` (root)

| Property | Type | Default |
| :--- | :--- | :--- |
| `namespace` | `string` | `""` (required in practice — `FDY1001`) |
| `version` | `string` | `"1.0.0"` |
| `entities` | `List<Entity>` | `[]` |
| `enums` | `List<Enum>` | `[]` |
| `dtos` | `List<DtoModel>` | `[]` |
| `customEndpoints` | `List<CustomEndpoint>` | `[]` |
| `workflows` | `List<WorkflowModel>` | `[]` |
| `connectors` | `List<ConnectorModel>` | `[]` |

### 2.2 `Entity`

| Property | Type | Notes |
| :--- | :--- | :--- |
| `name` | `string` | Must be a valid C# identifier (`FDY4001`/`FDY4002`) |
| `baseClass` | `string?` | |
| `softDelete` | `bool` | Emits `ISoftDelete` |
| `auditable` | `bool` | Routes mutations through the audit sink |
| `partitioned` | `bool` | Hot/cold split; see `PartitionedRepository<T>` |
| `archiveThresholdYears` | `int` = 2 | Age at which a year is archived |
| `realTime` | `bool` | Publishes mutations to the real-time channels |
| `realTimeRoles` | `List<string>` | Roles permitted to observe |
| `kafkaOutboxEnabled` | `bool` | Enqueues mutation events to the outbox |
| `kafkaTopic` | `string?` | Defaults via `KafkaTopicNaming` |
| `fileIoEnabled` | `bool` | Emits a `{Name}FileService` |
| `fileIoAllowedExtensions` | `List<string>` | |
| `graphQlEnabled` | `bool` | Includes the entity in the GraphQL schema |
| `multiTenant` | `bool` | Requires a `TenantKey` property (`FDY3002`) |
| `tenantProperty` | `string?` | |
| `ownerScoped` | `bool` | Requires an owner key (`FDY3013`) |
| `ownerExemptRoles` | `List<string>` | Roles that bypass ownership on write |
| `ownerReadExemptRoles` | `List<string>` | Roles that bypass ownership on read |
| `apiEnabledMethods` | `List<string>` | `GET`, `GET_BY_ID`, `POST`, `PUT`, `DELETE` |
| `apiRoles` | `Dictionary<string, List<string>>` | Per-method role requirements |
| `apiCaching` | `Dictionary<string, ApiCachingConfig>` | Per-method `{enabled, ttlSeconds}` |
| `apiBusinessRules` | `Dictionary<string, List<string>>` | Per-method rule class names |
| `properties` | `List<Property>` | |
| `indexes` | `List<Index>` | Compound indexes |

**`GET_BY_ID` is a distinct method token**, not implied by `GET`. An entity declaring only `GET`
serves the collection route and answers 404 on `/{id}`.

### 2.3 `Property`

| Property | Type | Notes |
| :--- | :--- | :--- |
| `name` | `string` | Valid identifier |
| `type` | `string` | See scalar table below |
| `isKey` | `bool` | **Must be `ObjectId`** — see below |
| `isTenantKey` | `bool` | Property must be named `TenantId` (`FDY3011`) |
| `isOwnerKey` | `bool` | Property must be named `OwnerId` (`FDY3014`) |
| `isSharedWithKey` | `bool` | Must be `List<string>` shaped (`FDY3017`) |
| `isEnum` | `bool` | `type` names a declared enum |
| `sensitiveCategory` | `string?` | Scope category for masked reads |
| `attributes` | `List<string>` | See the attribute vocabulary |

**Key types are `ObjectId` only.** `Vocabulary.KeyTypes` once also advertised `Guid`, `string`, `int`
and `long`, none of which work: `IRepository<T>` is constrained to `IEntity<ObjectId>`, so an entity
keyed on anything else compiles into a type with no resolvable repository — it cannot be persisted,
queried or served.

### 2.4 Scalar types (`Vocabulary.ScalarTypes`)

`string`, `int`, `long`, `decimal`, `double`, `float`, `bool`, `DateTime`, `DateOnly`, `TimeOnly`,
`Guid`, `ObjectId`. Matching is case-insensitive; unknown names pass through unchanged and raise
`FDY3006`.

### 2.5 Attribute vocabulary (`Vocabulary.Attributes`)

| Attribute | Aliases | Arity | Effect | On DTOs |
| :--- | :--- | :--- | :--- | :--- |
| `Required` | | bare | `[Required]` + C# `required` | yes |
| `Unique` | `UniqueIndex` | bare | Unique Mongo index | no |
| `Indexed` | `Index` | bare | Non-unique Mongo index | no |
| `TextIndex` | | bare | Joins the full-text index | no |
| `TenantKey` | | bare | Tenant discriminator | no |
| `Encrypt` | | bare | AES-256-GCM at rest via KMS envelope | no |
| `Mask` | | bare | Irreversible masking in logs and responses | no |
| `MaskEmail` | | bare | Email-shaped masking (`j***@domain.com`) | no |
| `PiiEmail` | | bare | Tags as PII email for audit | no |
| `PiiCreditCard` | | bare | Tags as payment card data | no |
| `MinLength(n)` | | 1 arg | Minimum string length | yes |
| `MaxLength(n)` | | 1 arg | Maximum string length | yes |
| `Range(a, b)` | | 2 args | Inclusive numeric range | yes |
| `Regex("…")` | | 1 arg | Emits `[GeneratedRegex]` | yes |
| `Email` | | bare | Email validation | yes |
| `Url` | | bare | Absolute URL validation | yes |
| `Phone` | | bare | Telephone validation | yes |

Unknown attributes raise `FDY3005`.

### 2.6 Enumerated vocabularies

| Set | Values |
| :--- | :--- |
| `HttpMethods` | `GET`, `POST`, `PUT`, `PATCH`, `DELETE` |
| `OperationTypes` | `Query`, `Insert`, `Update`, `Custom` |
| `ConnectorTypes` | `REST`, `SOAP`, `GraphQL` |
| `AuthTypes` | `None`, `Basic`, `ApiKey`, `Bearer`, `OAuth2` |

### 2.7 Workflows

`WorkflowModel` → `states[]` (`WorkflowStateModel`: `name`, `isInitial`, `isFinal`, `allowedRoles`),
`transitions[]` (`WorkflowTransitionModel`), `choiceNodes[]` (`WorkflowChoiceNodeModel` with
`branches[]` and a `defaultState`).

`WorkflowTransitionModel`: `id`, `name`, `fromState`, `toState`, `trigger`, `useCustomCommand`,
`requiredRoles[]`, `conditions[]`, `actions[]`.

**`useCustomCommand: true` means the compiler emits neither the command nor its handler** — the
application supplies both. The workflow definition still names the trigger, so the engine matches a
hand-written command exactly as it matches a generated one; if nobody writes it, the build fails on
the missing type, which is the right place to find out.

`WorkflowConditionModel` carries **`source`** — `entity` (default) or `request` — naming which object
the guard reads. The engine used to evaluate every guard against the request *and* the entity and
pass if either satisfied it, so a guard about a value the server owns could be answered with a value
the caller sent, and the same fallback decided choice-node routing. An unrecognised `source` falls
back to `entity`, never to the caller.

`WorkflowActionModel` covers both internal (`requestType`, `payloadTemplate`) and external
(`method`, `url`, `headers`, `bodyTemplate`) actions.

**External actions**, in execution order and with their limits:

| | |
| :--- | :--- |
| Ordering | Guards → handler → actions → state save → activity log. The handler's rejection is cheap and local; an external call is neither, so it happens after. |
| Templating | `{{Property}}` tokens escaped for their grammar — JSON string, URL data segment, header value (CRLF rejected). A token cannot change the host, add a path segment, or open a query. |
| Redirects | **Not followed.** A 3xx is not a success status, so the action fails visibly rather than letting the remote endpoint choose the next address. |
| Retries (transport) | Only for methods HTTP defines as repeatable (`GET`, `HEAD`, `OPTIONS`, `TRACE`, `PUT`, `DELETE`). `POST` and `PATCH` get one attempt at this layer, because a timed-out POST may already have been acted on. This is the HTTP client's own resilience handler, separate from the action-level retry below. |
| Response size | Capped at `WorkflowHttpLimits.MaxResponseBytes` (1 MB) when read, trimmed to 8 KB when recorded — with a marker saying it was trimmed. |
| Retries (action) | An action declaring `Retryable` gets up to 3 total attempts with exponential backoff, regardless of HTTP method — an author-level assertion that the whole action, not just the transport, is safe to attempt again. Every attempt is logged individually with its `AttemptNumber`. Not retried by default. |
| Compensation | **Opt-in, best-effort, single-level.** An action can declare a `CompensateWith` (the same shape as any action). When a later action in the same transition ultimately fails, already-succeeded earlier actions with a declared `CompensateWith` get it run, in reverse order — one compensation failing does not stop the sweep from attempting the rest. An action with no `CompensateWith` is left untouched, exactly as before. This is not a saga: nothing persists across a process restart, there is no cross-transition coordination, and a compensation is neither retried nor itself compensatable. Every attempt and every compensation is recorded in the activity log, including on the failure path, so what happened — and what wasn't undone — is knowable. |

The activity log's `PayloadDetails` is written through `WorkflowPayloadRedactor`: properties carrying
`[SensitiveData]` or `[PiiData]` are replaced with `[redacted]`. It previously stored the command as
sent, so a value the entity encrypts at rest sat in clear text in a second collection. Redaction
covers top-level properties only — a sensitive value nested inside another object is not caught.

### 2.8 DTOs, custom endpoints, connectors

- **`DtoModel`** — `name`, `properties[]` (`DtoProperty` with `sourceEntity`/`sourceProperty`
  projection), plus its own `kafkaOutboxEnabled` / `fileIoEnabled` opt-ins.
- **`CustomEndpoint`** — `method`, `route`, `requestType`, `targetEntity`, `operationType`,
  `filterField`/`filterOperator`/`filterSourceValue`, `assignments[]`, `roles[]`, `businessRules[]`.
- **`ConnectorModel`** — name, transport, base URL, auth mode and credentials, timeout, retries.

---

## 3. Compiler API (`Foundry.Schema.Compiler`)

### 3.1 `PocoGenerator`

[`PocoGenerator.cs`](../../foundry-schema/compiler/PocoGenerator.cs)

```csharp
public static IReadOnlyList<GeneratedFile> GenerateFiles(SchemaModel schema)   // preferred
public static Dictionary<string, string> Generate(SchemaModel schema)          // path → content
public sealed record GeneratedFile(string Path, string Content, EmitKind Kind);
public enum EmitKind { Generated, Scaffold }
```

**Use `GenerateFiles`.** `Generate` loses the write policy; a caller that writes its output will
overwrite scaffolds and destroy hand-written logic.

Emitted paths:

| Path | Kind | Emitted when |
| :--- | :--- | :--- |
| `{Enum}` | Generated | per enum |
| `{Entity}` | Generated | per entity |
| `{Dto}` | Generated | per DTO |
| `Commands/{RequestType}` | Generated | per custom endpoint |
| `Handlers/{RequestType}Handler` | **Scaffold** | per custom endpoint |
| `Rules/{RuleName}` | **Scaffold** | per named business rule |
| `Rules/RuleRegistrations` | Generated | any rule declared |
| `Commands/{Trigger}`, `Handlers/{Trigger}Handler` | Generated / Scaffold | per transition, unless `useCustomCommand` |
| `Kafka/{Entity}KafkaConsumer` | Generated | `kafkaOutboxEnabled` |
| `Kafka/KafkaRegistrations` | Generated | any Kafka target |
| `Services/{Entity}FileService` | Generated | `fileIoEnabled` |
| `Workflow/WorkflowConfigurations` | Generated | any workflow |
| `Serialization/FoundryJsonContext` | Generated | always |
| `Diagnostics/IndexVerification` | Generated | any index |
| `RealTime/RealTimeConfiguration` | Generated | any `realTime` entity |

`Rules/RuleRegistrations` exists because rule stubs are classes and nothing bound them to the
container: `AddFoundryRules` registers the *engine*, and `BusinessRuleBehavior` resolves
`IBusinessRule<TRequest>` from DI. Without the registrations, every declared rule was compiled into
the application and never ran.

### 3.2 `ApiManifestGenerator`

[`ApiManifestGenerator.cs`](../../foundry-schema/compiler/ApiManifestGenerator.cs)

```csharp
public static string Generate(SchemaModel schema)                              // the manifest JSON
public static string RouteFor(string entityName)                               // "/api/{plural}"
public static string TransitionRouteFor(string entityName, string trigger)     // + "/transitions/{trigger}"
public static List<string> EnabledMethods(Entity entity)                       // filtered, upper-cased, distinct
```

`RouteFor` returns `/api/` + a minimal-English plural, lower-cased (`Category` → `/api/categories`,
`Box` → `/api/boxes`). It is `public` deliberately: this exact rule was reimplemented as
`/api/v1/{lowercase-singular}` in the OpenAPI exporter, the Postman exporter, Studio and the test
generator. **A rule kept private is a rule that gets reimplemented.**

`EnabledMethods` filters `apiEnabledMethods` against `{GET, GET_BY_ID, POST, PUT, DELETE}`. It is
public for the same reason — Studio gave method-less entities a full CRUD surface, and the test
generator emitted REST suites for entities that expose nothing.

### 3.3 `SchemaValidator`

```csharp
public static void ValidateRawDocument(string json, DiagnosticBag bag)
public static DiagnosticBag Validate(SchemaModel? schema)
```

Call **both**, `ValidateRawDocument` first. Deserialisation hides an entire class of problem: a
Studio canvas has no `entities` property and deserialises into a structurally valid empty model, so
the compiler reported success while emitting nothing.

### 3.4 `Diagnostics`

```csharp
public enum DiagnosticSeverity { Info, Warning, Error }

public sealed record Diagnostic { Code; Severity; Message; Path; Hint; }

public sealed class DiagnosticBag
{
    IReadOnlyList<Diagnostic> Items { get; }
    bool HasErrors { get; }
    int ErrorCount { get; }
    int WarningCount { get; }
    void Report(string code, DiagnosticSeverity severity, string message, string path = "", string hint = "");
    void Error(string code, string message, string path = "", string hint = "");
    void Warning(string code, string message, string path = "", string hint = "");
    void Info(string code, string message, string path = "", string hint = "");
    void AddRange(IEnumerable<Diagnostic> diagnostics);
    string Render();
}
```

`DiagnosticCatalog` holds every code as a constant, plus `RepairableWarnings` (warnings the AI repair
loop may attempt) and `Descriptions`. Full table in [Appendix A](#appendix-a-diagnostic-catalog).

### 3.5 `CodeGen` — the injection boundary

[`CodeGen.cs`](../../foundry-schema/compiler/CodeGen.cs)

```csharp
public const string GeneratedHeader, ScaffoldHeader;
public static readonly IReadOnlySet<string> ReservedKeywords;

public static bool IsValidIdentifier(string? name);
public static bool IsValidNamespace(string? ns);
public static string Ident(string? name, string what = "name");   // throws UnsafeSchemaValueException
public static string Ns(string? ns);
public static string Lit(string? value);                          // safe C# string literal
public static string LitOrNull(string? value);
public static string LitList(IEnumerable<string>? values);
public static string Bool(bool value);
public static bool TryParseAttribute(string? attribute, out string name, out string args);
public static string Indent(string text, int spaces);

public sealed class UnsafeSchemaValueException : Exception { public string Code { get; } }
```

Every schema-derived value that reaches emitted source goes through `Ident`, `Ns`, `Lit` or
`LitList`. Schema documents may be AI- or user-authored; interpolating one straight into C# is code
injection. Reaching `UnsafeSchemaValueException` at emit time means the validator has a gap — the
compiler fails loudly rather than emitting the value.

### 3.6 SDK generators

```csharp
public static string TypeScriptSdkGenerator.Generate(SchemaModel schema)
public static string CsharpSdkGenerator.Generate(SchemaModel schema)
public static string PythonSdkGenerator.Generate(SchemaModel schema)
```

All three delegate their shape decisions to `SdkSurface`
([`SdkSurface.cs`](../../foundry-schema/compiler/Generators/SdkSurface.cs)):

```csharp
internal static List<string> MethodsFor(Entity entity);      // == ApiManifestGenerator.EnabledMethods
internal static bool HasList / HasGetById / HasCreate / HasUpdate / HasDelete (Entity entity);
internal static bool HasAnySurface(Entity entity);
internal static bool IsCallerSupplied(Property p);           // not key/tenant/owner/sharedWith
internal static bool IsRequired(Property p);                 // caller-supplied AND [Required]
internal static IEnumerable<Property> CallerProperties(Entity entity);
```

Every generated client:

- **Sends `Authorization: Bearer <token>`.** Every generated endpoint calls `RequireAuthorization()`;
  clients that sent no token answered 401 for every call.
- **Checks the status before parsing.** TypeScript throws `FoundryApiError`, C# calls
  `EnsureSuccessStatusCode`, Python raises. Passing a 401 body to `res.json()` and returning it as
  the requested entity is the worst shape a client can have.
- **Emits only the methods the entity serves**, and emits `update` when the entity declares `PUT`.
- **Makes server-assigned fields optional** — the tenant key comes from the token, the owner key from
  the subject, the id is generated.

Both non-C# outputs are compiled in CI by the real toolchain (`tsc --noEmit --strict`,
`python3 -m py_compile`); see
[`SdkCompilesTests.cs`](../../foundry-schema/tests/Foundry.Schema.Compiler.Tests/SdkCompilesTests.cs).

### 3.7 Exporters

```csharp
public static string OpenApiExporter.ExportJson(SchemaModel schema);    // OpenAPI 3.x
public static string AsyncApiExporter.ExportJson(SchemaModel schema);   // AsyncAPI (Kafka topics)
public static string PostmanExporter.ExportJson(SchemaModel schema);    // Postman v2.1 collection
public static string MermaidExporter.ExportMermaid(SchemaModel schema); // classDiagram
```

All route composition goes through `ApiManifestGenerator.RouteFor`, and the OpenAPI exporter
describes only methods `EnabledMethods` returns — it previously documented endpoints that answered
404 for entities with no REST surface at all.

### 3.8 `CanvasMigrator`

```csharp
public static readonly JsonSerializerOptions IrOutputOptions;  // indented, camelCase, skip nulls
public static bool IsCanvasDocument(string json);
public static SchemaModel Migrate(string json);
public static string MigrateToJson(string json);
```

Reads both the current canvas shape (`data.entity`) and the legacy flat `data` shape. `Trim` and
`IsDefault` drop defaulted fields so the emitted IR stays readable.

### 3.9 `IrSchemaGenerator`

```csharp
public const string Dialect, IdentifierPattern, NamespacePattern, RoutePattern;
public static readonly IReadOnlyList<string> GrammarUnsafeTokens;   // \d \s \w \b
public static string BuildAttributePattern();
public static IReadOnlyList<string> FindGrammarUnsafePatterns();
public static string Generate();
```

Produces the IR JSON Schema from `SchemaModels.cs`, gated in CI against drift. `FindGrammarUnsafePatterns`
exists because Ollama's constrained decoding rejects certain regex shorthands — a pattern that is
legal JSON Schema can still make grammar-constrained generation impossible.

### 3.10 `AiSchemaGenerator`

```csharp
public sealed record AiGenerationOptions
{
    public const string DefaultHost = "http://localhost:11434";
    public const string DefaultModel = "qwen3-coder:30b";
    string Host; string Model;
    int MaxRepairAttempts = 3;
    int MaxSoftRepairAttempts = 1;
    TimeSpan Timeout = 5 min;
    double Temperature = 0.1;
    static AiGenerationOptions Resolve(string? host = null, string? model = null);
}

public sealed class AiSchemaGenerator
{
    AiSchemaGenerator(HttpClient client, AiGenerationOptions options);
    Task<AiGenerationResult> GenerateAsync(string instruction, SchemaModel? current = null, …);
    Task<(bool Ok, string Detail)> CheckAsync(CancellationToken ct = default);
}

public sealed record AiGenerationResult
{
    SchemaModel? Schema; bool Success;
    IReadOnlyList<Diagnostic> Diagnostics;
    IReadOnlyList<AiAttempt> Attempts;     // AiAttempt(int Attempt, string RawResponse, …)
    string? Error;
    bool GrammarConstrained;               // false if the server rejected the grammar
    string? GrammarFallbackReason;
}
```

`Resolve` layers CLI flags over `FOUNDRY_OLLAMA_HOST` / `FOUNDRY_OLLAMA_MODEL` over the defaults.
`GrammarConstrained` is reported rather than assumed: when a server rejects the JSON-Schema grammar,
the run continues unconstrained and says so.

### 3.11 `EvalHarness`

```csharp
public static readonly IReadOnlyList<EvalCase> Cases;
public static SchemaModel ModificationBaseline { get; }
public static Task<EvalRunResult> RunAsync(…);
public static string RenderMarkdown(EvalRunResult run);
public static string RenderJson(EvalRunResult run);
```

`EvalCase` = `{ Id, Difficulty (Core|Hard), Construct, Prompt, Assertions[] }` where each
`EvalAssertion` is a `Func<SchemaModel, bool>` with a description. `EvalRunResult` exposes `PassRate`
and `ValidIrRate` separately — producing valid IR and producing *correct* IR are different questions.

### 3.12 `AiSpecBundle`

```csharp
public static readonly IReadOnlyDictionary<string, string> Examples;
public static IReadOnlyList<string> Write(string outputDirectory);
public static string BuildSystemPrompt(SchemaModel? currentSchema = null, string? instruction = null);
```

### 3.13 `KafkaTopicNaming`

```csharp
public static string TopicFor(string name, string? declaredTopic);  // declared wins
public static string Default(string name);                          // camel→kebab + suffix
```

Single producer of topic names, so the entity attribute, the outbox dispatcher and the AsyncAPI
export cannot disagree.

---

## 4. Generated code surface

### 4.1 The Roslyn analyser

[`ApiRouteGenerator`](../../foundry-api/src/Foundry.Api.SourceGenerators/ApiRouteGenerator.cs) is an
`IIncrementalGenerator` that reads `api-manifest.json` as an `AdditionalFile` and emits:

```csharp
public static IServiceCollection AddGeneratedHandlers(this IServiceCollection services);
public static IEndpointRouteBuilder MapGeneratedEndpoints(this IEndpointRouteBuilder endpoints, ApiManifest manifest);
```

Wire the manifest as an additional file in the consuming `.csproj`:

```xml
<ItemGroup>
  <AdditionalFiles Include="api-manifest.json" />
</ItemGroup>
```

### 4.2 The generated HTTP contract

For an entity at route `R = /api/{plural}`:

| Method token | Route | Success | Notes |
| :--- | :--- | :--- | :--- |
| `GET` | `R` | 200 | Query: `sortBy`, `sortOrder` (`asc`/`desc`, default desc), `limit` (default 100), `criteria` |
| `GET_BY_ID` | `R/{id}` | 200 / 404 | 400 when `id` is not an `ObjectId` |
| `POST` | `R` | **201** | Body is the entity |
| `PUT` | `R/{id}` | 200 | 400 on bad `ObjectId` |
| `DELETE` | `R/{id}` | **204** | 400 on bad `ObjectId` |
| transition | `R/transitions/{trigger}` | 200 | POST only |
| workflow history | `R/{id}/history` | 200 | Only when the entity has a workflow |

Custom endpoints are mapped at their declared `route` and `method`. `GET` and `DELETE` construct the
request type parameterless; other verbs bind it from the body. A `null` handler result becomes
**204**, otherwise 200 with the serialised result.

Every generated endpoint calls `RequireAuthorization()`. Responses are serialised with
`FoundryJsonDefaults.Options` so `ObjectId` round-trips.

**Malformed `criteria` is rejected, not ignored.** The generator once emitted `} catch {}`, so an
unparseable filter left `criteria` null and the query ran *without the caller's filter* — a 200 with
the full unfiltered result set. In a multi-tenant framework, silently widening a result set is the
worst direction to fail. It is now a 400 `Problem` response.

### 4.3 Generated extension methods a host calls

| Method | From | Emitted when |
| :--- | :--- | :--- |
| `AddGeneratedHandlers()` | analyser | always |
| `MapGeneratedEndpoints(manifest)` | analyser | always |
| `AddGeneratedBusinessRules()` | `Rules/RuleRegistrations` | any rule declared |
| `AddGeneratedKafkaHandlers()` | `Kafka/KafkaRegistrations` | any Kafka target |
| `MapGeneratedRealTimeEndpoints()` | `RealTime/RealTimeConfiguration` | any `realTime` entity |
| `WorkflowConfigurations.GetConfigurations()` | `Workflow/…` | any workflow |
| `IndexVerification.EnsureIndexesAsync(provider, ct)` | `Diagnostics/…` | any index |
| `{Entity}FileService` (`ImportAsync`, `ImportAllAsync`, `ExportToCsvAsync`) | `Services/…` | `fileIoEnabled` |

---

## 5. `Foundry.Core`

Shared contracts. No infrastructure dependencies beyond `MongoDB.Bson` for `ObjectId`.

### 5.1 Entities

```csharp
public interface IEntity<TId> where TId : IEquatable<TId?>
{ TId Id { get; init; } DateTime CreatedAtUtc { get; set; } DateTime UpdatedAtUtc { get; set; } int Version { get; set; } }

public abstract record BaseEntity<TId> : IEntity<TId> where TId : IEquatable<TId?>
{ void OnUpdate(); }                                    // stamps UpdatedAtUtc

public interface ISoftDelete { bool IsDeleted { get; init; } DateTime? DeletedAt { get; init; } }
public interface IVersionable { }                        // marker: opt into revision history

public sealed class ConcurrencyException : Exception     // EntityId, CollectionName
public sealed class EntityRevision                       // Id, EntityId, Version, Data, ChangedAtUtc, ChangedBy, Action
public readonly record struct SoftDeleteMarker : ISoftDelete
```

### 5.2 Tenancy

```csharp
public interface IMultiTenant { string TenantId { get; set; } }
public class TenantKeyAttribute : Attribute

public interface ITenantContext
{ string? TenantId { get; } bool HasTenant { get; } void SetTenantId(string tenantId); }

public class TenantContext : ITenantContext              // AsyncLocal-backed ambient value
```

### 5.3 Security

```csharp
public interface IEncryptionProvider { string Encrypt(string plainText); string Decrypt(string cipherText); }

public interface IOwnedResource { string OwnerId { get; set; } }
public interface ISharedResource : IOwnedResource { List<string> SharedWith { get; set; } }

public sealed class OwnerKeyAttribute : Attribute
public sealed class SharedWithKeyAttribute : Attribute
public sealed class OwnerExemptRolesAttribute : Attribute      { string[] Roles { get; } }
public sealed class OwnerReadExemptRolesAttribute : Attribute  { string[] Roles { get; } }
public static class GroupClaims { static readonly string[] Types = ["groups", "group"]; }

public enum PiiType { Generic, Email, Phone, CreditCard, Ssn, Address }
public class PiiDataAttribute : Attribute { PiiType Type; string Mask = "****"; }

public enum MaskingType { Full, Partial, Email }
public enum ProtectionType { Mask, Encrypt }
public sealed class SensitiveDataAttribute : Attribute
{ ProtectionType Protection; string Category = "pii"; MaskingType MaskingType; int PreserveCount = 4; char MaskChar = '*';
  string MaskValue(object? value); }

public static class ViewSensitiveDataScope
{ const string ClaimType = "scope"; const string DefaultCategory = "pii"; const string ClaimValue = "view:pii";
  static string For(string? category); }
```

### 5.4 Audit

```csharp
public enum AuditAction : byte { Inserted = 1, Updated = 2, DeletedHard = 3, DeletedSoft = 4, Restored = 5, Read = 6 }

public sealed record AuditLogEntry
{
    ObjectId Id; string OperatorId; string? OperatorName; DateTime TimestampUtc;
    string EntityType; string EntityId; string CollectionName;
    IReadOnlyList<PropertyDiff> PropertyDiffs; AuditAction Action;
    int ChangeCount { get; } bool HasActualChanges { get; }

    static AuditLogEntry ForInsert(string operatorId, string entityType, string entityId, string collectionName);
    static AuditLogEntry ForUpdate(…, IReadOnlyList<PropertyDiff> diffs);
    static AuditLogEntry ForSoftDelete(…); ForHardDelete(…); ForRestore(…); ForRead(…);
}

public readonly record struct PropertyDiff(string PropertyName, object? OldValue, object? NewValue)
{ bool HasChanged { get; } static PropertyDiff Inserted(…); static PropertyDiff Removed(…); }

public interface IAuditSink
{ Task WriteAsync(AuditLogEntry entry, CancellationToken ct = default);
  Task WriteManyAsync(IReadOnlyList<AuditLogEntry> entries, CancellationToken ct = default); }

public sealed class InMemoryAuditSink : IAuditSink
{ IReadOnlyList<AuditLogEntry> GetEntries(); void Clear(); }
```

### 5.5 Paging

```csharp
public enum SortOrder : byte { Ascending, Descending }

public sealed record PagedRequest
{ int PageNumber = 1; int PageSize = 20; int MaxDepthCap = 10_000;
  CursorSeekInfo? CursorInfo; SortRequest? SortBy; bool IsCursor { get; } }

public sealed record SortRequest { string FieldName; SortOrder Order; }

public sealed record CursorSeekInfo
{ string FieldName; object? Value; SortOrder Order;
  static CursorSeekInfo FirstPage(string fieldName, SortOrder order = Ascending);
  static CursorSeekInfo FromValue<T>(T entity, string fieldName, SortOrder order) where T : class; }

public class PagedResult<T>
{ IReadOnlyList<T> Items; long TotalRecords; int PageNumber; int PageSize;
  long TotalPages { get; } bool HasNextPage { get; } bool HasPreviousPage { get; }
  CursorSeekInfo? NextCursor; int LastItemIndex { get; }
  static PagedResult<T> Empty(int pageNumber, int pageSize);
  static PagedResult<T> From(IReadOnlyList<T> items, long totalRecords, int pageNumber, int pageSize);
  static PagedResult<T> WithCursor(IReadOnlyList<T> items, long totalOrOneMore, …);
  PagedResult<TResult> Map<TResult>(Func<T, TResult> selector) where TResult : class; }
```

Offset paging is capped by `MaxDepthCap`. Deep pages are what seek paging (`CursorInfo`) exists for.

### 5.6 Search

```csharp
public enum SearchOperator : byte
{ Equals, NotEquals, Contains, StartsWith, EndsWith, GreaterThan, LessThan,
  GreaterThanOrEqual, LessThanOrEqual, In, NotIn, Exists }

public sealed record SearchCriterion
{ string Field; SearchOperator Operator; object? Value; string? GroupKey;
  static SearchCriterion Equals(string field, object? value);
  static SearchCriterion Contains(string field, string? value);
  static SearchCriterion StartsWith(string field, string? value);
  static SearchCriterion GreaterThan<T>(string field, T value) where T : IComparable;
  static SearchCriterion In(string field, IEnumerable<object?> values); }

public sealed record CompiledSearchExpression<T> where T : class
{ Expression<Func<T, bool>> FilterExpression; IReadOnlyList<SearchCriterion> Criteria;
  BsonDocument? FilterStage; bool UsesServerSideOnly { get; } }
```

### 5.7 Outbox

```csharp
public interface IOutboxQueue { Task EnqueueAsync<TEvent>(TEvent eventData, CancellationToken ct) where TEvent : class; }

public interface IOutboxDispatcher
{ Task DispatchAsync(string eventType, string payload, string? correlationId = null,
                     string? traceParent = null, string? topic = null, CancellationToken ct = default); }

public record OutboxMessage : BaseEntity<ObjectId>
{ string EventType; string Payload; string? Topic; DateTime CreatedAt; DateTime? ProcessedAt;
  int RetryCount; DateTime? NextAttemptAt; DateTime? DeadLetteredAt;
  string? CorrelationId; string? TraceParent; string? ErrorMessage; }

public class EntityMutationEvent<T> { string MutationType; T Entity; DateTime Timestamp; }
public static class KafkaTopicDeclaration { static string? For(Type eventType); }
```

`NextAttemptAt` implements exponential backoff; `DeadLetteredAt` marks messages that exhausted
retries so they stop consuming publisher throughput.

### 5.8 Telemetry, user context, serialization

```csharp
public interface ICorrelationContext { string CorrelationId { get; } void SetCorrelationId(string id); }
public class CorrelationContext : ICorrelationContext

public interface ICurrentUserContext { string OperatorId { get; } string? OperatorName { get; } ClaimsPrincipal? User { get; } }
public sealed class AmbientUserContext : ICurrentUserContext { AmbientUserContext(Func<ClaimsPrincipal?>? userProvider = null); }

public sealed class ObjectIdJsonConverter : JsonConverter<ObjectId>   // incl. property-name forms
public static class FoundryJsonDefaults
{ static readonly JsonSerializerOptions Options;
  static JsonSerializerOptions CreateOptions();
  static void Apply(JsonSerializerOptions options); }
```

Call `FoundryJsonDefaults.Apply(options.SerializerOptions)` in `ConfigureHttpJsonOptions`. Without
it, `ObjectId` does not round-trip through `System.Text.Json`.

### 5.9 Entity attributes

```csharp
public sealed class IndexedAttribute : Attribute        { bool Unique; bool Descending; string? Name; }
public sealed class TextIndexedAttribute : Attribute    { int Weight = 1; }
public sealed class CompoundIndexAttribute : Attribute  { string[] Fields; bool Unique; string? Name; }
public sealed class PartitionedAttribute : Attribute    { PartitionedAttribute(int archiveThresholdYears = 2); int ArchiveThresholdYears { get; } }
public sealed class KafkaTopicAttribute : Attribute     { string Name { get; } }
public sealed class ReadAuditedAttribute : Attribute
public class RealTimeAttribute : Attribute              { bool Enabled; string[] Roles; }
```

---

## 6. `Foundry.Mongo`

### 6.1 Registration

```csharp
public static IServiceCollection AddFoundryMongo(this IServiceCollection services, Action<FoundryMongoOptions> configureOptions);

public sealed class FoundryMongoOptions
{
    string ConnectionString = "";      // required — throws if empty
    string DatabaseName = "";          // required — throws if empty
    string? EncryptionKey;             // base64 AES-256 key
    string? EncryptedEncryptionKey;    // KMS-wrapped DEK; takes precedence
    bool EnableCaching = false;
    TimeSpan? DefaultCacheTtl;
    int MinConnectionPoolSize = 10;
    int MaxConnectionPoolSize = 100;
}
```

Registers `IMongoClient`, `IMongoDatabase`, `IUnitOfWorkFactory`, `ITenantContext`, the encryption
provider, and `IRepository<>` — wrapped in `CachedRepository<T>` when `EnableCaching` is set. Also
calls `MongoDbConventions.Register()`.

**It does not register an `IKmsClient`.** Setting `EncryptedEncryptionKey` selects envelope
encryption and requires you to register the client for your key management service; resolving
`IEncryptionProvider` without one throws with instructions. See [6.6](#66-encryption).

### 6.2 `IRepository<T> where T : class, IEntity<ObjectId>`

[`IRepository.cs`](../../foundry-mongo/src/Foundry.Mongo/Repositories/IRepository.cs)

Every method takes an optional `IClientSessionHandle? session` for transactional composition and a
`CancellationToken`.

```csharp
// Access
IMongoCollection<T> Collection { get; }
string CollectionName { get; }
IQueryable<T> Query();                 // already carries the read filters
int MaxDepthCap { get; set; }

// Read
Task<T?> GetByIdAsync(object id, …);
Task<IReadOnlyList<T>> FindManyAsync(Expression<Func<T,bool>>? filter = null, string? sortBy = null,
                                     SortOrder sortOrder = Descending, int limit = 100, …);
Task<long> CountAsync(Expression<Func<T,bool>>? filter = null, …);
Task<PagedResult<T>> GetPagedAsync(PagedRequest request, Expression<Func<T,bool>>? filter = null, …);
Task<IReadOnlyList<T>> GetPagedItemsAsync(PagedRequest request, …);
Task<IReadOnlyList<T>> FindByCriteriaAsync(SearchCriterion[] criteria, …);
Task<PagedResult<T>> SearchPagedAsync(SearchCriterion[] criteria, PagedRequest pageRequest, …);
Task<PagedResult<UnifiedSearchResult>> CrossCollectionSearchAsync(CrossCollectionSearchRequest request, …);
Task<IReadOnlyList<TResult>> AggregateAsync<TResult>(PipelineDefinition<T,TResult> pipeline, …);

// Write
Task InsertAsync(T entity, …);
Task BulkInsertAsync(IEnumerable<T> entities, …);
Task UpdateAsync(T entity, …);                                     // OCC on Version
Task UpdateByObjectIdAsync(object id, Func<T,T> updateSelector, string operatorId, …);
Task BulkUpdateAsync(IEnumerable<T> entities, …);
Task<IReadOnlyList<UpdateResult>> BulkUpdateManyAsync(Expression<Func<T,bool>> filter, Func<T,T> updateSelector, …);
Task DeleteByObjectIdAsync(object id, string operatorId, …);       // soft when ISoftDelete
Task DeleteAsync(ObjectId id, …);
Task RestoreDeletedAsync(ObjectId id, …);

// Versioning
Task<IReadOnlyList<EntityRevision>> GetRevisionsAsync(object id, …);
Task<EntityRevision?> GetRevisionByVersionAsync(object id, int version, …);
Task<T> RestoreVersionAsync(object id, int version, …);

// Other
Task CreateIndexesAsync(CancellationToken ct = default);
T MaskSensitiveFields(T entity);
```

Supporting records:

```csharp
public sealed record UnifiedSearchResult
{ string EntityId; string CollectionsName; string EntityType; Dictionary<string, object?> Properties; }

public sealed record CrossCollectionSearchRequest
{ IReadOnlyList<Type> EntityTypes; Dictionary<string, Type>? CollectionToEntityTypeMap;
  SearchCriterion[] Criteria; PagedRequest? Pagination; int MaxPropertyCount; }
```

### 6.3 Implementations

| Type | Role |
| :--- | :--- |
| `Repository<T>` | The real implementation. Injects tenant, soft-delete and ownership filters on every read; enforces OCC on `Version`; encrypts `[Encrypt]` fields; writes audit entries and revisions. |

Isolation applies to **every** method on the interface, including the three that once bypassed it:
`GetRevisionsAsync` and `GetRevisionByVersionAsync` serve history only for a record the caller can
already see; `CrossCollectionSearchAsync` scopes each unioned collection by that type's own tenant and
owner rules; and `AggregateAsync` prepends a `$match` so a caller's pipeline runs against the rows they
may see rather than the collection. A pipeline that needs the unfiltered collection has to go to
`Collection` directly, which says what it is doing.
| `CachedRepository<T>` | Decorator over `Repository<T>` with `IMemoryCache`. Reads are cached per id; writes evict. `CachedRepositoryOptions.DefaultTtl` = 5 min. |
| `PartitionedRepository<T>` | Hot/cold split by year. Reads span the archives when the filter's date range requires it (`DateRangeVisitor` extracts the range from the expression tree). |

### 6.4 Unit of work

```csharp
public interface IUnitOfWorkFactory { IUnitOfWork Create(); }
public interface IUnitOfWork : IDisposable
{ IClientSessionHandle Session { get; } Task CommitAsync(CancellationToken ct = default); Task AbortAsync(…); }
```

Multi-document transactions require a replica set. On a standalone `mongod` the transaction path is
unavailable, which is exactly the condition the archival fallback below handles.

### 6.5 Background workers

```csharp
public sealed class DataArchivalWorker : BackgroundService
{ Task RunSweepAsync(CancellationToken cancellationToken = default); }   // callable directly, for tests

public sealed class OutboxPublisherWorker : BackgroundService
```

`DataArchivalWorker` moves documents older than `archiveThresholdYears` into per-year archive
collections, using a transaction when the deployment supports one and a non-transactional fallback
when it does not.

**Per-year failures accumulate rather than abort the sweep.** One unarchivable year used to abandon
every later year silently; the worker now logs each failure, continues, and throws an
`AggregateException` naming the count at the end. The outer sweep flattens it.

### 6.6 Encryption

```csharp
public sealed class AesEncryptionProvider : IEncryptionProvider { AesEncryptionProvider(string base64Key); }

public interface IKmsClient { string DecryptKey(string encryptedDekBase64); string EncryptKey(string plaintextDekBase64); }
public class LocalMockKmsClient : IKmsClient      // NOT registered by default; see below
public class KmsEnvelopeEncryptionProvider : IEncryptionProvider
{ KmsEnvelopeEncryptionProvider(IKmsClient kmsClient, string encryptedDekBase64); }
```

Two mutually exclusive paths, chosen by which option you set:

| Option | Provider | Needs an `IKmsClient` |
| :--- | :--- | :--- |
| `EncryptionKey` (base64 AES-256) | `AesEncryptionProvider` | no |
| `EncryptedEncryptionKey` (KMS-wrapped DEK) | `KmsEnvelopeEncryptionProvider` | **yes — you register it** |

```csharp
services.AddSingleton<IKmsClient, MyKmsClient>();     // your KMS
services.AddFoundryMongo(o => { o.EncryptedEncryptionKey = wrappedDek; … });
```

`LocalMockKmsClient` protects keys with a master key that is a **constant in Foundry's published
source**. It exists so the envelope path can be exercised without a cloud dependency, and it secures
nothing.

`AddFoundryMongo` used to register it as a default. An application that selected envelope encryption
and forgot its own client got the mock, and every `[Encrypt]` field was protected by a publicly known
key with nothing said at startup or visible at rest. Registering it is now a line of your own code —
which is where a choice like that belongs — and selecting envelope encryption without any client
throws at resolution with instructions rather than falling back.

### 6.7 Infrastructure helpers

```csharp
public static class RetryPolicyHelper
{ static Task<TResult> ExecuteWithRetryAsync<TResult>(Func<Task<TResult>> action, CancellationToken ct = default);
  static Task ExecuteWithRetryAsync(Func<Task> action, CancellationToken ct = default); }

public static class DynamicExpressionBuilder
{ static Expression<Func<T,bool>> BuildExpression<T>(SearchCriterion[] criteria) where T : class; }

public static class OffsetPaginationHelper
{ static IEnumerable<PipelineStageDefinition<BsonDocument,BsonDocument>> BuildPipelineStages(…);
  static PaginationDepthCheck CheckDepth(int pageNumber, int pageSize, int maxDepthCap);
  static (long Skip, int Take) GetSkipTakeValues(PagedRequest request);
  static void ValidatePageSize(int? pageSize = null);
  static void ValidatePageNumber(int pageNumber); }

public static class SeekPaginationHelper
{ static Expression<Func<T,bool>> BuildSeekFilter<T>(…);
  static Expression<Func<T,bool>> BuildCompoundSeekFilter<T>(…); }

public static class MongoDbConventions { static void Register(); }
public sealed class MongoDbHealthCheck : IHealthCheck
```

---

## 7. `Foundry.Api`

### 7.1 Security

```csharp
public static IServiceCollection AddFoundryAuthentication(this IServiceCollection services,
    IConfiguration configuration, string sectionName = "Authentication:Jwt");
public static IServiceCollection AddFoundryAuthentication(this IServiceCollection services,
    FoundryAuthenticationOptions options, string sectionName = "Authentication:Jwt");
public static IServiceCollection AddFoundryOIDC(this IServiceCollection services, Action<FoundryOidcOptions> configure);

public class FoundryAuthenticationOptions
{ string? Authority; string? Audience; string? Issuer; string? SigningKey;
  bool RequireHttpsMetadata = true; string RoleClaimType = "role"; string NameClaimType = "sub"; }

public class FoundryOidcOptions { string Authority; string Audience; bool RequireHttpsMetadata = true; }
```

**Nothing is inferred and nothing is defaulted.** Configuring both `Authority` and `SigningKey`, or
neither, is an error at startup. An API that cannot tell a valid token from an invalid one must not
start.

```csharp
public static class GeneratedEndpointSecurityGuard { static void EnsureAuthenticationIsConfigured(IServiceProvider services); }
public class CurrentUserContext : ICurrentUserContext        // reads IHttpContextAccessor
public class PiiMaskingJsonConverterFactory : JsonConverterFactory
public static class PiiMasker { static string Mask(string input, PiiType piiType, string defaultMask = "****"); }
public class SecurityHeadersMiddleware
public static IApplicationBuilder UseFoundrySecurityHeaders(this IApplicationBuilder app);
```

### 7.2 Middleware

```csharp
public class CorrelationIdMiddleware { public const string CorrelationIdHeaderName = "X-Correlation-ID"; }
public class TenantContextMiddleware  { public const string TenantIdHeaderName = "X-Tenant-ID"; }
public sealed class TenantContextOptions { bool TrustCallerAssertedTenant { get; set; } }  // default false
public class GlobalExceptionHandler : IExceptionHandler
public class IdempotencyException : Exception { string IdempotencyKey { get; } }
```

`TenantContextMiddleware` resolves the tenant from the caller's authenticated token — a `tenant_id`
or `tenantId` claim — and **from nothing else by default**. It must run after `UseAuthentication`
and before any endpoint, or multi-tenant entities never get their filter.

The `X-Tenant-ID` header and the `?tenantId=` query parameter are chosen by whoever sent the
request. They are honoured only when a deployment opts in:

```csharp
services.Configure<TenantContextOptions>(o => o.TrustCallerAssertedTenant = true);
```

Enable that only where something in front has already established the tenant and clients cannot
reach the service directly — a gateway, a service mesh, a local development host — and make that
component strip both from inbound requests before setting its own.

The claim outranked the header once the ordering was corrected, but the header was still consulted
whenever the token carried no tenant, so a caller holding a valid token that simply did not describe
tenancy could name any tenant they liked and every filter downstream applied faithfully to it. When
no tenant can be established none is set, and a multi-tenant write then fails rather than landing
somewhere arbitrary.

### 7.3 MediatR contracts

```csharp
public record InsertCommand<TEntity>(TEntity Entity)               : IRequest<TEntity>;
public record UpdateCommand<TEntity>(TEntity Entity)               : IRequest<TEntity>;
public record DeleteCommand<TEntity>(ObjectId Id, string OperatorId): IRequest<bool>;
public record GetByIdQuery<TEntity>(ObjectId Id)                   : IRequest<TEntity?>;
public record FindManyQuery<TEntity>(…)                            : IRequest<IReadOnlyList<TEntity>>;
public record SearchPagedQuery<TEntity>(…)                         : IRequest<PagedResult<TEntity>>;
```

with `InsertCommandHandler<TEntity>`, `UpdateCommandHandler<TEntity>`, `DeleteCommandHandler<TEntity>`,
`GetByIdQueryHandler<TEntity>`, `FindManyQueryHandler<TEntity>`, `SearchPagedQueryHandler<TEntity>`.

### 7.4 Pipeline behaviours

Registration order is the execution order.

| Behaviour | Responsibility |
| :--- | :--- |
| `CorrelationBehavior<,>` | Establishes the correlation id for the request |
| `SecurityBehavior<,>` | Role and ownership checks before the handler |
| `ValidationBehavior<,>` | FluentValidation validators from DI |
| `BusinessRuleBehavior<,>` | `IBusinessRuleEngine.EnsurePassedAsync` |
| `WorkflowTransitionBehavior<,>` | State machine gate for `IWorkflowTransitionRequest` |
| `IdempotencyBehavior<,>` | Replays a stored response for a repeated idempotency key |
| `CachingBehavior<,>` | Reads/writes the response cache; tracked by `EntityCacheTracker` |
| `RequestTelemetryBehavior<,>` | Opens OpenTelemetry span, records metrics and logs; does NOT write audit entries (repository layer's `IAuditSink` does that) |
| `OutboxDomainEventBehavior<,>` | Enqueues domain events to `IOutboxQueue` |

### 7.5 Manifest model

```csharp
public class ApiManifest { string Namespace; List<EndpointConfig> Endpoints; List<CustomEndpointConfig> CustomEndpoints; List<WorkflowConfig> Workflows; }
public class EndpointConfig
{ string Route; string Entity; List<string> Methods;
  Dictionary<string, List<string>> Roles;
  Dictionary<string, CachingConfig> Caching;
  Dictionary<string, List<string>> BusinessRules;
  bool GraphQL; }
public class CachingConfig { bool Enabled; int TtlSeconds; }
public class CustomEndpointConfig { string Route; string Method; string RequestType; List<string> Roles; List<string> BusinessRules; }
```

### 7.6 GraphQL

```csharp
public static IServiceCollection AddDynamicGraphQL(this IServiceCollection services, ApiManifest? manifest = null);

public class EntityType<T> : ObjectType<T> where T : class
public class EntityInputType<T> : InputObjectType<T> where T : class
public static class GraphQLAccessGuard { static void Enforce(IResolverContext context, IReadOnlyList<string> roles); }
public static class GraphQLResolverHelper<TEntity> where TEntity : class, IEntity<ObjectId>
{ static IQueryable<TEntity> ResolveCollection(IResolverContext context);
  static Task<TEntity?> ResolveById(IResolverContext context, string id); }
public static class GraphQLMutationHelper<TEntity> where TEntity : class, IEntity<ObjectId>
{ static Task<TEntity> CreateEntity(IResolverContext context, TEntity input);
  static Task<TEntity> UpdateEntity(IResolverContext context, string id, TEntity input);
  static Task<bool> DeleteEntity(IResolverContext context, string id); }
```

`AddDynamicGraphQL` is manifest-driven, so REST and GraphQL cannot disagree about which entities
exist or who may read them. Note it lives in the `Microsoft.Extensions.DependencyInjection`
namespace — no extra `using` is needed.

### 7.7 Workflow hosting

```csharp
public static IServiceCollection AddFoundryWorkflows(this IServiceCollection services,
    Action<WorkflowEntityTypeRegistry> configureEntities);   // configureEntities is REQUIRED

public sealed class WorkflowEntityTypeRegistry
{ WorkflowEntityTypeRegistry Register<TEntity>();
  WorkflowEntityTypeRegistry Register(Type entityType);
  IReadOnlyCollection<string> RegisteredNames { get; }
  Type Resolve(string entityTypeName); }

public sealed class ApiManifestWorkflowDefinitionProvider : IWorkflowDefinitionProvider
public sealed class MongoWorkflowStateStore : IWorkflowStateStore
public static IEndpointRouteBuilder MapWorkflowHistory(this IEndpointRouteBuilder endpoints, ApiManifest manifest);
public sealed record WorkflowHistoryEntry   // WorkflowId, Version, TransitionId, From/ToState, TriggeredBy/At, Success, ErrorMessage, Actions[]
public sealed record WorkflowHistoryAction  // ActionType, ActionName, Success, StatusCode
```

The registry is mandatory so an unresolvable entity type fails at startup rather than at the first
transition with a null reference from inside an assembly scan.

`MapWorkflowHistory` maps `GET {entity-route}/{id}/history`. The entity is loaded first and history
is served only if that succeeded — that is what keeps history inside the same tenant and ownership
isolation as the record itself.

### 7.8 Rate limiting, telemetry, docs

```csharp
public static IServiceCollection AddFoundryRateLimiter(this IServiceCollection services, …);
public class FoundryRateLimitingOptions { int PermitLimit = 100; int WindowSeconds = 60; int QueueLimit = 20; }

public static IServiceCollection AddFoundryTelemetry(this IServiceCollection services, …);
public class FoundryTelemetryOptions { string ServiceName = "FoundryService"; string ServiceVersion = "1.0.0"; bool EnableTracing = true; bool EnableMetrics = true; }

public static class Diagnostics
{ static readonly ActivitySource ActivitySource; static readonly Meter Meter;
  static readonly Counter<long> RequestCounter, CacheHits, CacheMisses, ValidationFailures;
  static readonly Histogram<double> RequestDuration; }

public static IEndpointRouteBuilder MapDocsEndpoint(this IEndpointRouteBuilder endpoints, ApiManifest manifest);  // GET /docs/spec

public static class DynamicEndpointRouteBuilder
{ static Expression<Func<TEntity,bool>>? BuildFilterExpression<TEntity>(HttpContext context) where TEntity : class; }
```

---

## 8. `Foundry.Rules`

### 8.1 Business rules

```csharp
public static IServiceCollection AddFoundryRules(this IServiceCollection services);

public interface IBusinessRule<in TRequest> { Task<RuleResult> ValidateAsync(TRequest request, CancellationToken ct); }

public interface IBusinessRuleEngine
{ Task<IEnumerable<RuleResult>> EvaluateAsync<TRequest>(TRequest request, CancellationToken ct);
  Task EnsurePassedAsync<TRequest>(TRequest request, CancellationToken ct); }

public class BusinessRuleEngine : IBusinessRuleEngine

public record RuleResult(bool IsPassed, string? ErrorMessage = null, string? RuleCode = null)
{ static RuleResult Success(); static RuleResult Failure(string message, string? code = null); }

public class BusinessRuleException : Exception { IReadOnlyList<RuleResult> Failures { get; } }
```

`AddFoundryRules` registers the **engine**, not the rules. Individual `IBusinessRule<TRequest>`
implementations must be in DI — which is what the generated `AddGeneratedBusinessRules()` does.

### 8.2 Dynamic (data-driven) rules

```csharp
public record DynamicRule
{ string RuleName, Description, TargetEntity, PropertyName, Operator, Value, Expression, ErrorMessage, ErrorCode; }

public interface IDynamicRuleStore { Task<IEnumerable<DynamicRule>> GetRulesForEntityAsync(string entityName, CancellationToken ct); }
public class InMemoryDynamicRuleStore : IDynamicRuleStore
public class DynamicRulesEngineRule<TRequest> : IBusinessRule<TRequest>
public static class DynamicRuleEvaluator { static bool Evaluate(object target, string propertyName, string op, string expectedValue); }
```

### 8.3 Workflow engine

```csharp
public interface IWorkflowEngine
{ void ValidatePermission(…);
  bool EvaluateCondition(string propertyName, string op, string expectedValue, object requestPayload);
  Task<ActionExecutionDetail> ExecuteActionAsync(…); }

public class WorkflowEngine : IWorkflowEngine

public interface IWorkflowStateful { string CurrentState { get; set; } string WorkflowId { get; set; } string WorkflowVersion { get; set; } }
public interface IWorkflowTransitionRequest { string EntityId { get; } string EntityType { get; } string TransitionId { get; } string FromState { get; } string ToState { get; } }
public interface IWorkflowDefinitionProvider { IReadOnlyList<WorkflowConfig> GetWorkflows(); }
public interface IWorkflowStateStore
{ Task<IWorkflowStateful?> LoadAsync(string entityTypeName, string entityId, CancellationToken ct = default);
  Task SaveAsync(string entityTypeName, IWorkflowStateful entity, CancellationToken ct = default);
  Task AppendActivityLogAsync(WorkflowActivityLog log, CancellationToken ct = default);
  Task<IReadOnlyList<WorkflowActivityLog>> ReadActivityLogAsync(…); }

public class WorkflowTransitionBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
public class WorkflowException : Exception
public record WorkflowActivityLog : BaseEntity<ObjectId>   // full transition audit incl. ExecutedActions[]
public record ActionExecutionDetail { string ActionType, ActionName; bool Success; int StatusCode; string? ResponseBody; }
```

Configuration mirrors the IR: `WorkflowConfig`, `WorkflowStateConfig`, `WorkflowTransitionConfig`,
`WorkflowConditionConfig`, `WorkflowActionConfig`, `WorkflowChoiceNodeConfig`,
`WorkflowChoiceBranchConfig`.

Action templates are token-substituted with a context-aware escaper (`Json`, `Url`, `HeaderValue`) —
the same value is escaped differently depending on where it lands.

---

## 9. `Foundry.Kafka`

```csharp
public static IServiceCollection AddFoundryKafka(this IServiceCollection services, Action<KafkaOptions>? configureOptions = null);
public static IServiceCollection AddFoundryKafkaProducer(this IServiceCollection services, IConfiguration section);
public static IServiceCollection AddFoundryKafkaConsumerBridge(this IServiceCollection services, IConfiguration section);

public class KafkaOptions { string BootstrapServers; string ClientId; ProducerOptions ProducerOptions; ConsumerOptions ConsumerOptions; }
public class ProducerOptions { int LingerMs = 5; int Acks = -1; string CompressionType = "none"; }
public class ConsumerOptions
{ string GroupId; string AutoOffsetReset = "latest"; bool EnableAutoCommit = true; int SessionTimeoutMs = 60000;
  Dictionary<string,string> TopicApiMappings; }

public interface IKafkaProducer : IDisposable
{ Task<DeliveryResult<string,string>> ProduceAsync(string topic, string key, string value, Headers? headers = null, CancellationToken ct = default);
  void Produce(string topic, string key, string value, Action<DeliveryReport<string,string>>? deliveryHandler = null);
  void Flush(TimeSpan timeout); }
public class KafkaProducer : IKafkaProducer

public interface IKafkaMessageHandler
{ Task HandleAsync(string topic, string key, string value, IDictionary<string,string> headers, CancellationToken ct); }
public class KafkaConsumerHostedService : BackgroundService

public class KafkaOutboxDispatcher : IOutboxDispatcher      // outbox → Kafka, with W3C trace headers
public class KafkaToApiBridgeHandler : IKafkaMessageHandler // topic → MediatR request, via TopicApiMappings
public sealed class KafkaHealthCheck : IHealthCheck
```

`Acks = -1` is `all` — the durable default.

---

## 10. `Foundry.RealTime`

### 10.1 Registration and routes

```csharp
public static IServiceCollection AddFoundryRealTime(this IServiceCollection services, string? redisConnectionString = null);
public static IEndpointRouteBuilder MapFoundryRealTime(this IEndpointRouteBuilder endpoints);
```

| Route | Transport | Auth |
| :--- | :--- | :--- |
| `/realtime/hub` | SignalR | `RequireAuthorization()` |
| `/realtime/sse` | Server-Sent Events | `RequireAuthorization()` |
| `/realtime/ws` | WebSockets | `RequireAuthorization()` |

**All three require authorization.** They did not, which meant an anonymous client could watch every
mutation in the system while being refused the endpoint that produced it — and `NotificationHub`'s
role check was running against a principal nobody had established.

SSE sends `event: connected` on open and pings every 15 s. Passing a Redis connection string adds a
SignalR backplane for multi-node scale-out. `AddFoundryRealTime` also decorates any registered
`IAuditSink` with `RealTimeAuditSink`, so mutations reach the channels without the data layer knowing
the channels exist.

### 10.2 `RealTimeAccessPolicy` — the single access decision

```csharp
public static string ToSubscriptionName(string entityTypeName);
public static Type? ResolveEntityType(string entityTypeName);
public static bool MayObserve(ClaimsPrincipal? user, string entityTypeName, out string? reason);
public static bool MayObserve(ClaimsPrincipal? user, string entityTypeName);
public static bool IsKnownEntity(string entityTypeName);
```

One place decides who may observe what, for all three transports.

### 10.3 Channels

```csharp
public interface INotificationService { string ChannelName { get; } Task SendMutationAsync(AuditLogEntry entry, CancellationToken ct = default); }
public interface IRealTimeNotificationBroker { Task BroadcastMutationAsync(AuditLogEntry entry, CancellationToken ct = default); }
public class RealTimeNotificationBroker : IRealTimeNotificationBroker   // fans out to every INotificationService
public class RealTimeAuditSink : IAuditSink                              // decorator; optional inner sink

public class SseNotificationService : INotificationService   // "SSE"
{ SseClient RegisterClient(HttpResponse response, ClaimsPrincipal? user = null); void UnregisterClient(string id); }
public class SseClient { string Id { get; } ClaimsPrincipal? User { get; } Task SendEventAsync(string eventName, object data, CancellationToken ct = default); }

public class SignalRNotificationService : INotificationService   // "SignalR"
public class NotificationHub : Hub
{ static string ToSubscriptionName(string entityTypeName);
  static string RecordGroupName(string entityName, string recordId);
  Task SubscribeToEntity(string entityName);   Task UnsubscribeFromEntity(string entityName);
  Task SubscribeToRecord(string entityName, string recordId);  Task UnsubscribeFromRecord(string entityName, string recordId); }

public class WebSocketNotificationService : INotificationService   // "WebSockets"
public class WebSocketConnectionManager
{ public sealed record Connection(WebSocket Socket, ClaimsPrincipal? User);
  string AddSocket(WebSocket socket, ClaimsPrincipal? user = null);
  ConcurrentDictionary<string, Connection> GetAllSockets();
  Task RemoveSocketAsync(string id, string reason);
  Task SendMessageAsync(WebSocket socket, object message, CancellationToken ct = default);
  Task BroadcastMessageAsync(object message, CancellationToken ct = default);
  Task BroadcastMessageAsync(object message, string? entityTypeName, CancellationToken ct = default); }
```

The connection manager stores the `ClaimsPrincipal` alongside the socket precisely so the
entity-scoped broadcast overload can filter per recipient.

---

## 11. `Foundry.Connectors`

```csharp
public static IHttpClientBuilder AddFoundryRestConnector(this IServiceCollection services, string name, Action<ConnectorOptions> configureOptions);
public static IHttpClientBuilder AddFoundrySoapConnector(this IServiceCollection services, string name, Action<ConnectorOptions> configureOptions);
public static IHttpClientBuilder AddFoundryGraphQLConnector(this IServiceCollection services, string name, Action<ConnectorOptions> configureOptions);

public enum ConnectorType { REST, SOAP, GraphQL }
public enum AuthenticationType { None, Basic, ApiKey, Bearer, OAuth2 }

public class ConnectorOptions
{ string Name; ConnectorType Type = REST; string BaseUrl; AuthenticationType AuthType = None;
  string? Username, Password, ApiKey; string? ApiKeyHeaderName = "X-API-Key";
  string? Token, TokenEndpoint, ClientId, ClientSecret;
  string? SoapAction, TargetNamespace;
  int TimeoutSeconds = 30; int MaxRetries = 3;
  Dictionary<string,string> Headers; }

public interface IFoundryConnector
{ string Name { get; } ConnectorType Type { get; }
  Task<TResponse?> ExecuteAsync<TRequest,TResponse>(string endpoint, TRequest payload, CancellationToken cancellationToken = default);
  Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default); }

public class RestConnector : IFoundryConnector      { Uri? BaseAddress { get; } }
public class SoapConnector : IFoundryConnector      // first arg is the SOAP action
public class GraphQLConnector : IFoundryConnector   // first arg is the query; payload is the variables
public class FoundryConnectorHealthCheck : IHealthCheck
```

Each registration returns `IHttpClientBuilder`, so Polly resilience, message handlers and lifetimes
compose normally.

---

## 12. `Foundry.FileIO`

```csharp
public interface IDataParser<TOut> { IAsyncEnumerable<TOut> ParseAsync(Stream fileStream, CancellationToken ct = default); }

public sealed class CsvDataParser<TOut> : IDataParser<TOut> { CsvDataParser(CsvConfiguration? config = null); }

public sealed class ExcelDataParser<TOut> : IDataParser<TOut> where TOut : class
{ bool SkipUnconvertibleValues { get; init; }
  IList<ExcelCellError> UnconvertibleValues { get; }
  IReadOnlyCollection<string> UnmatchedProperties { get; } }

public sealed record ExcelCellError(int Row, string PropertyName, string? Value, Type TargetType);

public sealed class CsvDataExporter<TIn>
{ CsvDataExporter(CsvConfiguration? config = null);
  Task ExportAsync(IAsyncEnumerable<TIn> dataStream, Stream outputStream, CancellationToken ct = default); }

public sealed class FormulaSafeStringConverter : StringConverter   // CSV injection defence
public sealed class FileSecurityValidator
{ bool VerifySignature(string fileName, Stream fileStream);   // magic bytes, not the extension
  string SanitizeFileName(string fileName); }
```

Everything streams — `IAsyncEnumerable` in and out — so file size is bounded by disk, not memory.
`SkipUnconvertibleValues` turns hard failures into a collected `UnconvertibleValues` report, which is
usually what a bulk import wants. `FormulaSafeStringConverter` neutralises leading `=`, `+`, `-`, `@`
so an exported cell cannot execute in a spreadsheet.

---

## 13. `Foundry.Testing`

```csharp
public static Dictionary<string,string> AutomatedTestSuiteGenerator.GenerateAllTestSuites(SchemaModel schema);
public static Dictionary<string,object?> MockDataGenerator.GenerateEntityMockData(Entity entity);

public sealed record ProtocolResult(string Name, bool Passed, string Details);
public static string TestReportGenerator.GenerateHtmlReport(string namespaceName, int totalTests, int passedTests,
    int failedTests, double durationSeconds, IReadOnlyList<ProtocolResult>? protocols = null);
public static string TestReportGenerator.GenerateMarkdownReport(…same…);
```

Emitted files:

| File | Emitted when |
| :--- | :--- |
| `FoundryTestEnvironment.cs` | always |
| `{Entity}RestApiTests.cs` | `EnabledMethods(entity)` is non-empty |
| `{Entity}GraphQLTests.cs` | `graphQlEnabled` |
| `{Entity}RealTimeTests.cs` | `realTime` |
| `{Entity}WorkflowTests.cs` | the entity has a workflow |

`FoundryTestEnvironment` reads three environment variables:

| Variable | Default |
| :--- | :--- |
| `FOUNDRY_TEST_BASE_URL` | `http://localhost:5000` |
| `FOUNDRY_TEST_TOKEN` | none — `Authenticated()` **throws** if unset |
| `FOUNDRY_TEST_TENANT` | `tenant-demo` |

Throwing on a missing token is deliberate: these endpoints require one, so a suite that quietly
passed without it would be reporting on requests it never made. The generated tests ask real
questions — an anonymous caller is refused, a create is accepted, an unknown id is 404 — against the
route `ApiManifestGenerator.RouteFor` produces. Suite types that could only ever pass (Kafka, FileIO,
Rules) were removed rather than kept as decoration.

Reports render a status only when counts are supplied; with nothing supplied, nothing is claimed.

---

## 14. Tooling surfaces

### 14.1 Studio backend HTTP API

[`foundry-schema/backend/Program.cs`](../../foundry-schema/backend/Program.cs) — default port **5100**.

| Method | Route | Body | Returns |
| :--- | :--- | :--- | :--- |
| POST | `/api/compile` | `SchemaModel` | generated C# files as a map |
| POST | `/api/manifest` | `SchemaModel` | `api-manifest.json` content |
| POST | `/api/ai/prompt` | `AiRequest` | generated IR |
| GET | `/api/ai/models?host=` | — | available Ollama models |
| POST | `/api/save-pocos` | `SaveRequest` | written file paths |
| POST | `/api/save-manifest` | `SaveManifestRequest` | written path |

```csharp
public record AiRequest { string Prompt; SchemaModel? CurrentSchema; string? OllamaHost; string? OllamaModel; }
public record SaveRequest { Dictionary<string,string> Files; string OutputPath; }
public record SaveManifestRequest { SchemaModel Schema; string OutputPath; }
```

**`WorkspacePaths` is the containment rule** for both save endpoints
([`WorkspacePaths.cs`](../../foundry-schema/backend/WorkspacePaths.cs)):

```csharp
public static string Root { get; }
public static bool IsInside(string root, string candidate);
public static bool TryResolveDirectory(string? requested, out string resolved, out string? error);
public static bool TryResolveFile(string directory, string relativeName, out string resolved, out string? error);
```

`TryResolveFile` rejects a rooted `relativeName` outright before resolving, then re-checks
containment. Both matter: `Path.Combine(dir, "/etc/passwd")` returns the second argument, and a
prefix check without a trailing separator lets `/work-evil` pass as inside `/work`. Every target is
resolved *before* anything is written, so a partial write cannot leave the workspace half-updated.

### 14.2 Studio (React)

[`foundry-studio/src/`](../../foundry-studio/src/)

```ts
export const BACKEND_URL = 'http://localhost:5100';
export async function deriveApiManifest(schema: unknown): Promise<string>;
export function parseManifestRoutes(manifestJson: string): RouteMap;
export async function compileToCs(schema: unknown): Promise<Record<string, string>>;
export type RouteMap = Readonly<Record<string, string>>;
```

Routes displayed in the API playground come from `parseManifestRoutes` over the real manifest, not
from a client-side guess. `types.ts` mirrors the IR (`Entity`, `Property`, `Index`, `Enum`,
`Connector`, `CustomEndpoint`, `DtoModel`, `WorkflowDefinition`, …) plus the canvas node types
(`ClassNode`, `EnumNode`, `AppNode`).

### 14.3 VS Code extension

[`foundry-vscode/`](../../foundry-vscode/) — v1.1.0, requires VS Code ≥ 1.85.

| Command | Title |
| :--- | :--- |
| `foundry.openStudio` | Open Studio Visual Editor |
| `foundry.compileSchema` | Compile Active Schema to C# Code |
| `foundry.newSchema` | Create New Schema Manifest |
| `foundry.migrateSchema` | Migrate Studio Canvas to IR |
| `foundry.validateSchema` | Validate Active IR Document |

Custom editor `foundry.studioEditor` claims `*.ir.json`, `*.foundry.json`, `*.foundry`. A language
client starts `foundry lsp` over stdio for `**/*.ir.json`.

The invocation planning lives in [`invocation.ts`](../../foundry-vscode/src/invocation.ts), kept free
of the `vscode` module so it is unit-testable:

```ts
export const COMPILER_PROJECT: string;
export function cliDllPath(configuration?: 'Release' | 'Debug'): string;
export function resolveCliCommand(workspaceRoot: string, exists?: Exists): Invocation;
export function planCompile(workspaceRoot: string, schemaPath: string, exists?: Exists): CompilePlan;
export function resolveServerCommand(…): Invocation;
export function defaultIrPathFor(canvasPath: string): string;
```

`planCompile` emits `--input <schema> --output <dir>/Generated --manifest <dir>/api-manifest.json`.
`CompilerService.runCompile` surfaces failures with `showErrorMessage`; it previously showed a
hard-coded success notification whatever the compiler did.

---

## 15. Appendices

### Appendix A: Diagnostic catalog

Codes are grouped by band: **1xxx** structural, **2xxx** referential, **3xxx** semantic,
**4xxx** safety. Constants in
[`Diagnostics.cs`](../../foundry-schema/compiler/Diagnostics.cs).

| Code | Meaning |
| :--- | :--- |
| `FDY1001` | Missing namespace |
| `FDY1002` | No entities |
| `FDY1003` | Entity missing name |
| `FDY1004` | Entity has no properties |
| `FDY1005` | Entity has no key |
| `FDY1006` | Entity has multiple keys |
| `FDY1007` | Property missing name |
| `FDY1008` | Property missing type |
| `FDY1009` | Enum has no values |
| `FDY1010` | Document is a Studio canvas, not IR — run `foundry migrate` |
| `FDY1011` | Unsupported key type (only `ObjectId`) |
| `FDY2001` / `FDY2002` | Duplicate entity / property name |
| `FDY2003`–`FDY2006` | Workflow: unknown state, unknown entity, no initial state, no final state |
| `FDY2007` | Index references an unknown property |
| `FDY2008` | Unknown enum type |
| `FDY2009` | Endpoint targets an unknown entity |
| `FDY2010` / `FDY2011` | DTO source entity / property unknown |
| `FDY2012` | Workflow references an unknown choice node |
| `FDY2013` | Duplicate transition trigger |
| `FDY2014` | Duplicate type name |
| `FDY3001` / `FDY3002` | `TenantKey` without `multiTenant` / `multiTenant` without a tenant key |
| `FDY3003` | `kafkaTopic` without the outbox enabled |
| `FDY3004` | FileIO extensions without FileIO enabled |
| `FDY3005` / `FDY3006` | Unknown attribute / unknown type |
| `FDY3007` | Invalid archive threshold |
| `FDY3008` / `FDY3009` | Invalid HTTP method / route |
| `FDY3010` | Unused enum |
| `FDY3011` | Tenant key must be named `TenantId` |
| `FDY3012`–`FDY3014` | Owner key without `ownerScoped`, and the converse; must be named `OwnerId` |
| `FDY3015` | Owner-exempt roles without `ownerScoped` |
| `FDY3016` / `FDY3017` | `SharedWithKey` without `ownerScoped` / wrong shape |
| `FDY3018` | A role is both owner-exempt and read-exempt |
| `FDY3019` | Workflow gate without a default branch |
| `FDY4001`–`FDY4003` | Invalid identifier / reserved keyword / invalid namespace |
| `FDY4004` | Unsafe attribute argument |

`DiagnosticCatalog.RepairableWarnings` names the subset the AI repair loop may attempt to fix
automatically.

### Appendix B: Environment variables

| Variable | Consumer | Default |
| :--- | :--- | :--- |
| `FOUNDRY_OLLAMA_HOST` | `foundry ai`, `eval`, Studio backend | `http://localhost:11434` |
| `FOUNDRY_OLLAMA_MODEL` | same | `qwen3-coder:30b` |
| `FOUNDRY_TEST_BASE_URL` | generated test suites | `http://localhost:5000` |
| `FOUNDRY_TEST_TOKEN` | generated test suites | none (throws) |
| `FOUNDRY_TEST_TENANT` | generated test suites | `tenant-demo` |
| `MONGODB_CONNECTION` | host convention | `mongodb://localhost:27017` |
| `MONGODB_DATABASE` | host convention | per app |
| `MONGODB_ENCRYPTION_KEY` | host convention | dev-only derived key |

### Appendix C: Host wiring order

Working reference: [`samples/Foundry.E2E.Showcase/Program.cs`](../../samples/Foundry.E2E.Showcase/Program.cs).
Order matters where noted.

```csharp
// --- Services ---
builder.Services.AddSingleton(manifest);                        // deserialised api-manifest.json
builder.Services.AddFoundryMongo(o => { … });
builder.Services.AddFoundryRealTime();
builder.Services.AddFoundryRules();
builder.Services.AddFoundryAuthentication(builder.Configuration);   // required: endpoints call RequireAuthorization
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserContext, CurrentUserContext>();
builder.Services.AddMemoryCache();
builder.Services.AddValidatorsFromAssembly(typeof(Program).Assembly);
builder.Services.AddMediatR(cfg => {
    cfg.RegisterServicesFromAssembly(typeof(Program).Assembly);
    cfg.RegisterServicesFromAssembly(typeof(InsertCommand<>).Assembly);
});

builder.Services.AddGeneratedHandlers();
builder.Services.AddGeneratedBusinessRules();     // else declared rules never run
builder.Services.AddGeneratedKafkaHandlers();
builder.Services.AddFoundryWorkflows(r => r.Register<Order>());

// Behaviours execute in registration order.
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(SecurityBehavior<,>));
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(ValidationBehavior<,>));
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(BusinessRuleBehavior<,>));
builder.Services.AddTransient(typeof(IPipelineBehavior<,>), typeof(CachingBehavior<,>));

builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.ConfigureHttpJsonOptions(o => FoundryJsonDefaults.Apply(o.SerializerOptions));  // ObjectId
builder.Services.AddDynamicGraphQL(manifest);

// --- Pipeline ---
var app = builder.Build();
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TenantContextMiddleware>();   // before any endpoint, or tenant filters never apply
app.MapGeneratedEndpoints(manifest);
app.MapWorkflowHistory(manifest);
app.MapFoundryRealTime();
app.MapGeneratedRealTimeEndpoints();
app.MapGraphQL();
app.Run();
```

Two things the host must not omit:

1. **`api-manifest.json` as an `AdditionalFiles` entry** in the `.csproj`. Without it the analyser
   emits empty registrations and every entity route answers 404.
2. **`AddFoundryAuthentication`**. Generated endpoints call `RequireAuthorization()`; with no scheme
   the application refuses to start rather than serving 500s.

### Appendix D: Verification status

| Generated artifact | Compiled / executed by |
| :--- | :--- |
| Entities, handlers, rules | `dotnet build` over the showcase IR |
| The showcase application | Built, run, and gated against schema drift |
| Scaffolded applications | Runtime smoke test, driven over HTTP |
| Generated test suites | Compiled, and run against a live app |
| C# SDK | `dotnet build` |
| TypeScript SDK | `tsc --noEmit --strict` |
| Python SDK | `python3 -m py_compile` |
| The `foundry` distro binary | Published for `linux-x64` in CI, then run — `version`, `validate`, `schema build`, and serving the embedded Studio page |

Every artifact in that list is checked by a real compiler or by an executed test. Semantic string
assertions still exist alongside those gates — they are useful for pinning intent — but no emitted
artifact relies on one as its only evidence.
