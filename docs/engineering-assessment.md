# Engineering Assessment — 2026-07-26 to 2026-07-27

An honest read of where Foundry stands, written from a working session *inside* the codebase rather
than from reading it. It is deliberately blunt: the point is to be useful for planning the next cycle,
not to market the project.

**Summary.** The architecture is strong and the differentiation is real. The implementation was
demo-grade. It is now verified where it matters: the repository builds from a clean clone, five CI
jobs pass, every module has tests, and a scaffolded application is driven over HTTP through
authentication, roles, ownership, tenancy, workflows and a restart. The suite went from 258 tests to
751, on a repository whose CI had never passed once.

The single most important finding is not any individual bug. It is this:

> **Six separate features had never executed.** Not under-tested — never run.
>
> 1. **Multi-tenancy** did not compile. `IMultiTenant` needs a `set`; the compiler emitted `init`.
> 2. **Generated APIs were anonymous**, while their own OpenAPI output named the roles they required.
> 3. **Workflows** were compiled, validated, and went nowhere: the manifest never carried them.
> 4. **The Excel import path** had never been called by any test.
> 5. **The outbox** had never published, and the compose file meant to supply a broker exited on
>    startup — which is why nobody noticed.
> 6. **Hot/cold partitioning** archived nothing: the sweep ran inside a MongoDB transaction, which a
>    standalone server does not support, and the failure was caught and logged.

Each looked correct in source. Each had convincing scaffolding around it — a meticulous validator, a
pipeline behaviour with its own tests, an OpenAPI description naming the roles. Reading the code
would have confirmed all six were fine. That is the disposition worth naming: this codebase was
consistently good at *appearing* to work, and the appearance was load-bearing.

The enumerated backlog is now clear. But the sixth was found *after* that sentence was first written,
by picking the most-advertised feature with no tests and running it — and it failed within minutes.
That is the honest state: the known list is done, and the base rate for "never run" in this codebase
is six for six. Section 5 lists what is still unexercised, in that spirit.

---

## 1. Where it genuinely stands

The runtime layer — `foundry-mongo`, 4,775 lines across 27 files covering tenant filter injection,
envelope encryption, optimistic concurrency, seek pagination and hot/cold partitioning — is real
senior-level work. (An earlier draft of this document said 11.5k lines; that figure was inherited and
never checked. Measured, `foundry-mongo/src` is 4,775 lines and the whole module including tests and
samples is 6,909.)

An earlier draft added "and held up under inspection". That clause is deleted rather than softened,
because it turned out to be the problem in miniature: hot/cold partitioning is in that list, it does
read as careful work, and **it had never archived a single record**. Inspection was exactly what it
held up under.

The AI thesis is **validated rather than aspirational**. The local model never writes C#; it writes IR,
and the compiler writes C#. Measured with `qwen3-coder:30b`:

| Band | Cases | Domain accuracy | Schema-valid IR |
| ---- | ----- | --------------- | --------------- |
| Core | 30    | 100%            | 100%            |
| Hard | 10    | 40%             | 100%            |

The load-bearing number is the last column. At 40% hard-domain accuracy the model's *judgement*
degrades while output *validity* never does — precisely what the design predicts. Best-practice output
is guaranteed by construction, because the model does not author the layer where a missing tenant
filter or an N+1 query would live.

An awkward consequence, still true: **the AI layer is the most mature part of the system.** It
generates onto a runtime that is less verified than it is — though the gap is now much narrower than it
was.

---

## 2. The systemic defect pattern

Nearly every bug found had the same shape: **success reported, nothing done.**

- The compiler printed `Success` having emitted zero files.
- An index was declared in the schema and never created.
- Studio config was emitted and silently dropped by the compiler.
- An endpoint was declared and silently skipped.
- A client-supplied id was accepted, then silently replaced by the driver.
- `foundry api` slept 100 ms and returned success.
- A **test report** stated that every protocol passed, regardless of the results it was given.

This is a design disposition, not bad luck. The codebase preferred *carry on* over *fail loudly*. For a
code generator that is the most damaging default available: the output looks plausible, passes review,
and the failure surfaces in production.

**Treat "returns success without doing the thing" as a bug class, not a list of individual defects.**
Fixes should change the default, not patch instances.

### The pattern at its largest scale: the repository could not be built

The root repository recorded its seven sibling modules as **gitlinks** (mode `160000`) with no
`.gitmodules` file, so Git had no path-to-URL mapping for them:

- `git clone` produced seven **empty** directories.
- `git submodule update --init` failed — *"No url found for submodule path"*.
- `dotnet restore` could not find ~15 of the projects referenced by `Foundry.slnx`.
- **CI had never passed. Not once, on any commit, since the workflow was added.**

Every `git push` reported success. The local tree built and tested clean, because those directories
happened to be populated on one machine. The published repository was unusable by every person and
system other than its author.

It was found by cloning from GitHub and building *that*, which exposed a second dependency underneath:
`Foundry.Cli` embedded `foundry-studio/dist/index.html`, a correctly-gitignored build artifact, so a
clean clone failed with CS1566 until someone ran an undocumented `npm run build`. And underneath that a
third: `dotnet test Foundry.slnx` tried to run the `Foundry.Testing` helper library as a suite and
exited 1 while all 258 tests passed — invisible for as long as the suites were run individually.

Three defects stacked, each masked by the one above. The two generalisable lessons:

1. **Verify against a fresh clone, not your working tree.** A working tree accumulates state the
   repository does not contain.
2. **Run the command CI runs.** "All suites green" and `dotnet test Foundry.slnx` were not the same
   statement, and only one of them mattered.

---

## 3. What is now verified

| Gate | What it proves |
| ---- | -------------- |
| Clean clone builds | The repository is usable by someone other than its author |
| `Build and test` | 746 C# tests across 13 suites |
| `Outbox round trip` | 5 tests driving a mutation through MongoDB and a **real Kafka broker** |
| `Studio tests and typecheck` | 33 TypeScript tests, plus the bundle builds |
| `Schema gates` | Sample schemas validate; the AI skill bundle regenerates and its golden examples validate |
| `Runtime smoke test` | Two scaffolded apps boot and are driven over HTTP with real JWTs: **authentication, declared roles, row-level ownership and workflow transitions**, the CRUD contract (create, read, update, delete, filter, validate, optimistic concurrency, restart) and **tenant isolation** |

CI first went green on `bf3e227`. The runtime smoke test is the one that matters most, because it is the
only gate that runs a generated application rather than compiling one — and reaching it required six
fixes, including that **creating an entity over HTTP had never worked** (`required Id` failed model
binding on every `POST`) and that a CLI-scaffolded project produced no `api-manifest.json`, so it served
no routes at all.

Two contracts that had silently forked now have single producers:

- **`api-manifest.json`** — Studio derived it independently and disagreed with the compiler on the
  route prefix and on whether an entity with no declared methods gets full CRUD. Studio now exports IR;
  the compiler derives the manifest.
- **C# generation** — six TypeScript generators across two Studio files reimplemented `PocoGenerator`,
  emitting a namespace that does not exist and omitting `partial`, on which the whole `*.Custom.cs`
  design depends. Studio now fetches from `POST /api/compile`.

And the largest untestable-by-design block is gone: `WorkflowTransitionBehavior` located four
collaborators by scanning `AppDomain` for simple-name matches and invoking methods through `MethodInfo`.
Two interfaces preserve the `Foundry.Rules` → `Foundry.Api` layering constraint that the reflection was
working around, with compile-time contracts instead. It now has 24 tests.

The sections below are ordered by what a regulated buyer would ask about first, not by the order the
work happened in. Most of them share one shape, which is why they are worth reading together rather
than separately: a capability the framework advertised, every supporting layer present and correct,
and nothing that had ever run the path end to end. Several describe work to make a feature function
at all, not merely to test it.

### Tenant isolation now exists


It did not before, in four independent ways, and extending the smoke test is what surfaced them. The
first is the one that reframes the rest:

> **No multi-tenant Foundry application had ever compiled.** `IMultiTenant` declares
> `string TenantId { get; set; }`; the compiler emitted the tenant key as `init` like every other
> property, and C# will not accept `init` as an implementation of `set` (CS8854). Every multi-tenant
> schema produced a project that failed to build.

Nothing caught it because no test had ever *built* a multi-tenant schema — the showcase IR declares none,
and the compiler's own tests assert on generated text, which looked perfectly correct. Behind it sat
three more breaks that only a running application could show:

| Break | Effect |
| ----- | ------ |
| `TenantContextMiddleware` was never wired into any template, sample or scaffold | `HasTenant` was false on every request ever served, and the repository's filter is written `if (HasTenant)` — so it never applied |
| Nothing stamped the tenant on write | The tenant of a new row was whatever the caller's request body said, so a client could write into another tenant by naming it |
| The expression overload of the read filter applied soft delete and *not* the tenant | `FindManyAsync`, `CountAsync` and `FindByCriteriaAsync` — the methods behind every generated list endpoint — returned every tenant's rows |

That last one is worth dwelling on. There were two overloads, both named `ApplySoftDeleteFilter`; the
`FilterDefinition` one applied the tenant, the expression one did not, and it was `static`, which put
`_tenantContext` out of reach and made the omission look deliberate. The name is why it survived: every
call site read as though soft delete were the only concern. They are now `ApplyReadFilters`.

Writes addressed by id were unscoped too — an id is not a secret, it is handed out in every `Location`
header — so knowing one was enough to update, delete or restore another tenant's row.

Isolation is covered at both levels: 13 repository tests against a real MongoDB, and the smoke test
driving two tenants over HTTP through a scaffolded application. Both were confirmed load-bearing by
reverting each fix and watching the right tests fail.

---

### Generated APIs are no longer anonymous


The schema lets an entity declare `apiRoles` per method. Those roles reached exactly one place:

```csharp
.WithDescription($"Access {entityType.Name} documents. Requires roles: {rolesStr}")
.Produces(401)
.Produces(403, typeof(ProblemDetails))
```

The endpoint's OpenAPI description, and nothing else. Where a schema declared no roles the description
defaulted to `"Requires roles: Admin"` — so an endpoint documented itself as admin-only, advertised the
401 and 403 it could never return, and was open to anyone. Custom endpoints had the identical defect.

One qualification, because the first version of this section overstated it: role checking was not
entirely absent. `SecurityBehavior`, a MediatR pipeline behaviour, does compare the manifest's roles
against the caller's claims. But it was registered in the template and the sample and **not by
`foundry new`**, so scaffolded applications had no check at all; and nothing anywhere registered an
authentication scheme, so `IsAuthenticated` was false on every request ever served. In the template an
endpoint with declared roles refused everybody; in a scaffolded app everything was open. Neither is
access control.

What changed:

| Layer | Now |
| ----- | --- |
| Generated endpoints | `RequireAuthorization` with the declared roles, or authenticated-only where a schema declares none — anonymous is not a policy |
| `AddFoundryAuthentication` | One entry point for an OIDC authority *or* a symmetric key, refusing to start on a missing, short, or ambiguous configuration |
| Startup guard | Endpoints requiring authorization with no scheme registered fails at boot, naming the missing call, instead of a 500 on the first request |
| Scaffold, template, sample | All three wire authentication, `UseAuthentication`/`UseAuthorization`, and `SecurityBehavior` identically |
| `TenantContextMiddleware` | A signed `tenant_id` claim now outranks the `X-Tenant-ID` header, which it did not |

That last row was its own hole: the header was read first, so an authenticated caller could override the
tenant their own token asserted just by setting one, and every tenant filter downstream then applied
faithfully to the wrong tenant.

The smoke test drives this with genuine HS256 tokens it mints itself — anonymous requests get 401, a
token signed with a different key gets 401, a `Clerk` gets 403 where the schema requires `Admin`, and an
`Admin` gets 204. Enforcement that refuses everyone is not enforcement, so the positive case is asserted
alongside the negatives.

### Authorization reaches individual rows


Roles decide whether a caller may use an endpoint. They say nothing about which rows come back
through it, so any caller holding a declared role reached every row in their tenant — adequate for a
back-office tool, and not for anything where users hold their own records.

An entity can now declare ownership:

```json
{
  "name": "Note",
  "multiTenant": true,
  "ownerScoped": true,
  "ownerExemptRoles": ["Supervisor"],
  "properties": [
    { "name": "OwnerId", "type": "string", "isOwnerKey": true }
  ]
}
```

Enforcement lives in the data layer rather than at the endpoint, which is the decision the whole
feature turns on. An endpoint check can refuse a request; it cannot filter a list. Putting ownership
where the tenant filter already lives means the list path, the by-id path, updates and deletes are
narrowed by one mechanism instead of four, and the two filters compose: **ownership narrows within a
tenant and can never widen across one.** That last property is asserted directly — a `Supervisor`
exempt from the owner filter in one tenant still sees nothing belonging to another.

`OwnerId` is server-assigned from the caller's `sub` claim and overwritten if the request body sets
it, for the same reason the tenant is. It reads the authenticated principal rather than
`ICurrentUserContext.OperatorId`, which falls back to the literal `"anonymous"` — right for audit,
and wrong here, where it would become a legitimate owner value shared by every unauthenticated write.

**A pre-existing bug surfaced while building it.** A tenant key named anything other than `TenantId`
produced a project that would not compile (CS0535, `IMultiTenant.TenantId` unimplemented) — and had
it compiled, the repository's filter on the field `"TenantId"` would have matched no document. The
data layer identifies these fields by name, so both keys are now required to be named `TenantId` and
`OwnerId`, with diagnostics FDY3011 and FDY3014 rejecting anything else at validation time. That is a
constraint rather than a fix, but it is stated at the point where the message can name the property
instead of surfacing as a compile error in generated code or an empty result set at runtime.

### Workflows reach a running application


A workflow declared in a schema was compiled, validated, and went nowhere. This was the clearest
remaining instance of the section 2 defect class, at feature scale — and the shape is worth stating
precisely, because it is not the shape people expect:

> **Every layer was already built.** The definition provider, the Mongo state store, the pipeline
> behaviour with 24 tests, the generated command implementing `IWorkflowTransitionRequest`, the
> generated handler — all present, all correct, all waiting on a list that arrived empty every time.

`ApiManifestGenerator` emitted `Namespace`, `Endpoints` and `CustomEndpoints`, and never `Workflows`.
The manifest is the only channel between the compiler and a running application, so
`ApiManifestWorkflowDefinitionProvider.GetWorkflows()` returned nothing on every request that had
ever been served. The scaffolder never called `AddFoundryWorkflows`, and no route could send a
transition command even if one had been built.

Four defects sat behind that, none reachable without building and running a workflow schema — which
nothing did:

| Defect | Effect |
| ------ | ------ |
| The transition handler never imported the command's namespace | CS0246: **no schema with a workflow in it had ever compiled** |
| The command implemented the void `IRequest` | `ISender.Send` returns a bare `Task`, which the endpoint generator cannot assign a result from |
| A transition declaring no `id` emitted an empty `TransitionId` | The engine matches on that value, so it would match the wrong transition or none |
| `WorkflowTransitionBehavior` read roles only from `ClaimTypes.Role` | The framework's own authentication emits raw `role` claims, so every role-gated transition refused everybody |

Transitions are exposed as custom endpoints — `POST /api/orders/transitions/approveorder` — because
the endpoint generator already maps a custom endpoint by POSTing its request type and the behaviour
already intercepts anything implementing `IWorkflowTransitionRequest`. The whole path existed and
needed connecting, not building. Roles declared on a transition are carried onto its endpoint, so
they are enforced before the request reaches the pipeline as well as inside it.

A refused transition is now a **409** naming the state the record is actually in, rather than a bare
500 with the engine's explanation swallowed.

### The outbox publishes, and the compose file never started a broker


The transactional outbox is the framework's durability claim: a mutation is recorded in the same
database as the data and published afterwards, so a broker outage cannot lose an event. Every part
of that chain had unit tests and **the chain had never run**.

The first obstacle was not in the code. The repository's own `docker-compose.yml` configured Kafka
for ZooKeeper against `confluentinc/cp-kafka:latest`, which is KRaft-only in current versions and
exits immediately with `environment variable "KAFKA_PROCESS_ROLES" is not set`. Anyone following the
README's `docker compose up -d` got a broker that was never there. Nothing depended on it closely
enough to notice — which is the whole reason the round trip had never been run. It is now KRaft,
single-node, and **pinned**, since floating to a new major is what broke it.

Behind that sat a defect the round trip found immediately:

> A nested event type produced an **illegal Kafka topic name**. The topic is derived from the CLR
> type name, and the nested-type separator `+` was never stripped — so `Orders+Placed` became
> `orders+placed-events`, which the broker rejects. The publish failed with librdkafka's
> `Broker: Invalid topic`, the outbox worker logged it and retried, and the message never left.
> Forever, quietly: the first symptom is a queue that does not drain.

A nested record is an ordinary way to declare an event. Topic derivation now handles nested and
generic types explicitly — a generic event is named for what it is *about*, so
`EntityMutationEvent<Order>` publishes to `order-events` rather than putting every entity's
mutations on one topic, which had been an accident of where the comma fell in the qualified name —
and a name that still cannot be made legal is refused before publishing, naming the event type and
the offending characters.

The new `Outbox round trip` job provisions MongoDB **and** a real broker. The tests fail rather than
skip without them, so the job cannot pass by doing nothing; they are excluded from the fast job by
an explicit category filter, which keeps the split visible in the workflow rather than hidden in a
conditional inside the tests.

### The outside-world paths are covered, and one was injectable


Three paths were named in earlier cycles as where untrusted input meets the framework. Covering them
found one real defect class, in the place with the most surface:

**A workflow action interpolated caller data into three grammars at once, unescaped.**
`ExecuteActionAsync` builds a URL, a set of headers and a JSON body by substituting `{{Property}}`
tokens from the request — which is the MediatR command bound from an HTTP body, so every value is
caller-controlled. Substitution was raw `string.Replace`. Demonstrated by test before the fix:

| Value sent by the caller | Effect |
| --- | --- |
| `", "approved": true, "x": "` | Closed its own JSON string and added a field the workflow never intended to send |
| `../../admin/shutdown` | Escaped its path segment |
| `INV-001?admin=true` | Appended a query parameter to an internal endpoint |

Escaping is now chosen by context: JSON values through `JsonEncodedText`, URL values as a single path
segment, and header values containing a line break refused outright rather than stripped. URL escaping
means a template cannot accept a caller-supplied path or whole URL — deliberately, since that is the
shape that turns a workflow action into server-side request forgery. `ExecuteActionAsync` had no tests
at all; it now has 17.

**The Excel path had never been executed.** `ExcelDataParser.ParseAsync` was not run by any test, and
the cell-conversion suite carried a remark saying the end-to-end path was "covered by the integration
suite" — it was not; the integration test of that shape drives the *CSV* parser. **The claim is what
made the gap look deliberate**, which is a more useful finding than any defect would have been: a
comment asserting coverage is indistinguishable from coverage until someone checks. It now has 12
tests that build real workbooks.

One gap surfaced there and is now visible rather than fixed: a column absent from a file leaves every
row at the type's default, with a row count exactly matching what the user expected. The parser cannot
decide that is an error — plenty of imports fill only some fields — so it reports the unmatched
properties and lets the caller decide.

**The REST connector came back clean.** Six tests on how it handles a response it did not expect —
HTML served with a 200, malformed JSON, an oversized error body — and all passed against the existing
code. Worth stating plainly: coverage that finds nothing is still worth having, and reporting it as a
finding would be dishonest.

### Hot/cold partitioning archived nothing

Named in section 1 as part of what makes the data layer senior-level work, and it had **no tests at
all** — neither `PartitionedRepository<T>` nor `DataArchivalWorker` was executed by anything. It was
checked because the pattern above had held five times and predicted where to look next. It held a
sixth.

**The archival sweep could never run.** It moved documents inside `WithTransactionAsync`, and MongoDB
supports multi-document transactions only on a replica set. Every deployment this project ships is a
standalone server — its own `docker-compose.yml`, and the `mongo:7` service in all three CI jobs — so
the sweep threw `NotSupportedException: Standalone servers do not support transactions` on every pass.
The exception was caught and logged.

That failure is worse than it first looks, because of how reads route. `GetByIdAsync` picks a
collection from the age embedded in the record's `ObjectId` and **does not fall back**:

> A record older than the threshold is looked for only in `Ledgers_{year}`. The sweep never moved it
> there. So on the day a record crossed the threshold it stopped being readable — and the archival
> that was supposed to have moved it had been failing silently since the day the feature was written.

The sweep now detects whether the server supports transactions and uses them when it can. Without
them it copies, **verifies the copy is present**, and only then deletes — insert-then-delete, never
the reverse, so an interruption duplicates a document (which a re-run corrects) rather than losing
one. A verification shortfall aborts with the active collection untouched. The sweep is also public
now, so an operator can trigger one, and it reports failure instead of logging it: one unarchivable
entity no longer blocks the others, and a sweep with any failure throws rather than returning quietly.

**A second finding, in the same shape as the tenancy work.** `PartitionedRepository` accepted an
`ITenantContext`, passed it to the active and deleted repositories, and dropped it. Archive
repositories are created lazily per year and had nothing to pass — so **a row left tenant isolation
the moment it aged past the threshold**, and any tenant could read any other tenant's archived
records by id. Isolation must not depend on how old a record is.

Nine tests now cover routing, the tenant boundary across the partition, and the sweep itself.

### The last mirrored implementation is gone


Studio derived CRUD routes itself, in a `crudRouteFor` that reimplemented the compiler's `RouteFor`
and its pluraliser in TypeScript. It was kept deliberately — the designer and playground need a route
to display, and a request per keystroke would be absurd — and held to the compiler by a test table
duplicated on the C# side.

That table is the part worth examining, because it looked like a sufficient guarantee and was not:

> A shared table catches a derivation that **changes**. It cannot catch a rule the compiler
> **gains**, because a rule nobody thought to write down twice is not in the table.

Routes now come from a manifest the compiler derived and the store cached, keyed on a signature of
entity names and their declared methods — so a keystroke that cannot affect routing costs no request,
which is what made deleting the mirror affordable. Two behaviours changed, and both are improvements:

- An entity declaring no API methods now shows **no route**, because the compiler generates none for
  it. The designer previously displayed one, advertising an endpoint that would never be served.
- With no derived manifest the playground **refuses to send** rather than calling a URL it made up.
  Two earlier versions of that code guessed wrongly — one emitted an `/api/v1/` prefix, the other did
  not pluralise — and every request 404'd, which reads as a broken application rather than a broken
  playground.

The pluralisation contract did not move to a weaker place: `ApiManifestGeneratorTests` still owns it,
and is now its only home rather than one of two copies. Studio's suite went from 28 tests to 33.

## 4. Coverage and what covering it found

Every module has tests. **751 in total**, from 258 at the start:

| Suite | Tests | Needs |
| ----- | ----: | ----- |
| `foundry-schema` | 184 | — |
| `foundry-rules` | 87 | — |
| `foundry-integration-tests` | 75 | MongoDB |
| `foundry-file-io` | 75 | — |
| `foundry-mongo` | 70 | MongoDB |
| `foundry-api` | 54 | MongoDB |
| `foundry-core` | 52 | — |
| `foundry-kafka` | 39 | — |
| `foundry-connectors` | 37 | — |
| `foundry-studio` | 33 | — |
| `foundry-realtime` | 26 | — |
| `foundry-testing` | 26 | — |
| `foundry-cli` | 21 | — |
| `foundry-kafka-integration` | 5 | MongoDB **and** a Kafka broker |

The suites that need infrastructure **fail rather than skip** without it. That is the house rule, and
it is why the Kafka suite is split out and excluded from the fast job by an explicit category filter
rather than by a conditional inside the tests: a gate that quietly skips its own subject reports
success for something it never checked.

**Nine modules went from zero tests to a suite each, and seven of the nine yielded five defects.** The
count did not decline as the work went on, which is the strongest available evidence that the pattern
was systemic rather than local. Well over eighty defects were fixed across the session — the module
sweep alone accounted for roughly thirty-six, before the six features in section 3 were made to work
at all. An exact figure would be false precision: several "defects" were one cause with four symptoms,
and several were a feature that had never run rather than a line that was wrong.

The two exceptions are informative rather than lucky:

- **`foundry-cli` yielded one.** It was the module worked over earliest, so its defects had already been
  found. Its 21 tests drive the built binary as a process, because the CLI's contract is its exit code
  and its stdio. That also verified the earlier LSP byte-framing fix **end to end** — a request
  containing `café-naïve-piñata-日本語` followed by a second request proves the stream does not
  desynchronise, which inspection alone could not establish.
- **`foundry-studio` yielded divergences, not omissions** — see section 3.

### The findings worth remembering

Ordered by consequence, not by module.

**A cross-tenant data leak.** `foundry-realtime` sent every mutation to SignalR's `Clients.All` *in
addition* to the subscription groups, bypassing the `[RealTime(roles: …)]` RBAC that `NotificationHub`
carefully validates on subscribe — and in a multi-tenant deployment delivering one tenant's mutations to
another's clients. An `AuditLogEntry` carries `PropertyDiffs`, so what leaked was the changed *values*.
Two more defects in the same area cancelled each other out and hid it: entity subscription groups never
matched (subscribers joined under the simple type name, delivery targeted the assembly-qualified one),
so subscriptions were broken while the firehose delivered everything anyway. Record subscriptions were
also authorised against a client-supplied entity name but keyed on the record id alone, and an
unresolvable name failed *open*.

**Credentials sent to the wrong service.** `foundry-connectors` keyed `ConnectorOptions` and the typed
`HttpClient` on the *type* rather than the connector name, so registering a second connector of the same
type replaced the first. An application integrating a CRM and a billing provider ended up with both
pointing at one base URL carrying one set of credentials. Five of six new registration tests fail
against the original code; the one that passes is the single-connector demo case.

**Both guarantees of the transactional outbox.** `foundry-kafka` broke ordering at both ends — a fresh
`Guid` partition key per message scattered one entity's mutations across partitions, and the publisher
requested a sort order it never received because `FindManyAsync` ignores `sortOrder` when `sortBy` is
null. Durability defaulted to `Acks=Leader`, acknowledging before replication, so a broker failure loses
a message the outbox has already marked processed. It fails only under broker failure, so never in
development.

**Two security defects in the security component.** `foundry-file-io`'s `SanitizeFileName` is documented
as eliminating path traversal and returned `".."` unchanged, so `Path.Combine(uploadDir, sanitised)`
resolved to the parent directory. And the CSV exporter was open to **formula injection**: a field
beginning with `=`, `+`, `@` or `-` executes when the export is opened in Excel or Sheets.

**A test report that reported success unconditionally.** `foundry-testing` embedded a fixed seven-row
"Protocol Coverage Matrix" where every row read `PASSED`, beside "100% Endpoint Coverage" and "Zero
Breach" — none measured, none affected by the results passed in. A run with fifty failures produced a
clean bill of health, and a zero-test run rendered a "100.0%" pass rate. One level up, `foundry test`
fed it invented counts for suites it had generated and never executed. This is the purest expression of
the pattern in the codebase.

**Silent data corruption on import.** `foundry-file-io`'s Excel parser caught every conversion failure
and left the property at its default, so a cell of `"1,234.00 USD"` imported as an amount of **zero**
while the row count matched what the user expected.

**Guard conditions that silently blocked their own transitions.** `foundry-rules` had four defects in
condition evaluation — enums never matched, dates could not be ordered, decimals were parsed with the
ambient culture, and a null value ignored the operator. Each failed by refusing a legitimate transition
with "guard condition failed", indistinguishable from a correct refusal. The cause of all four was a
second copy of the comparison logic that had drifted from the business-rule evaluator.

**Pagination metadata that made clients stop early.** `foundry-core`'s `TotalPages` divided by
`PageSize` unguarded (a zero size saturating to `long.MaxValue`), `WithCursor` did not trim the seek
sentinel, and `Map` dropped the cursor so a mapped result reported itself as the last page.

### Four areas came back clean

Recorded as evidence, not omitted for being unexciting:

- **Ambient tenant propagation** held under 50 interleaved async flows, did not escape a child task, and
  was inherited by child operations. This is the highest-consequence surface in the framework, so a
  negative result here is a real finding.
- **The serialization defaults** were correct on every property — including that `Version` is
  deliberately *not* server-owned, because it is the OCC token and hiding it would silently disable
  optimistic concurrency.
- **`AddFoundryRules`** produced a valid container under `ValidateOnBuild`/`ValidateScopes`.
- **The real-time audit sink** honours `[RealTime(false)]` while still writing audit records.

---

## 5. What is not fixed

Stated plainly, because a document that only lists wins is not useful for planning. Grouped by what
each would actually cost, because a flat list of fifteen caveats reads as uniformly alarming and is
not.

### Would block a regulated deployment

**Ownership is one relationship, not an authorization language.** `ownerScoped` answers "this row
belongs to one caller", which covers the common case and nothing beyond it. Sharing, delegation, team
or hierarchy scoping, and field-level restrictions all have nowhere to be expressed. A schema needing
those writes them by hand in a business rule, where nothing checks the rule was applied to every path.

**Exempt roles are per entity, not per operation.** A `Supervisor` exempted from the owner filter on
an entity is exempted for reads, updates and deletes alike. Read-only oversight — the common shape for
an auditor — cannot be expressed; combining `ownerExemptRoles` with `apiRoles` on DELETE is the closest
approximation.

**Workflow history has no read path.** `AppendActivityLogAsync` writes an entry per transition and
nothing serves it. For a regulated buyer the audit trail is the point, so a record that can be written
and not read is half a feature.

**No token issuance, and no refresh or revocation story.** The framework validates tokens; it does not
mint them. That is the right split — an identity provider's job is not a code generator's — but a team
adopting this still has to stand one up, and the scaffolded project says so only by leaving the
configuration empty.

**Scaffolded projects pull in `MessagePack` 2.5.187, which carries known high-severity advisories**
(transitively, via SignalR). The framework's own dependencies are clean; the generated application's
are not, and a scaffolded project prints those warnings on its first build.

**MongoDB is still the only data provider.** That is the commercial ceiling for enterprise .NET shops.

### Deliberate limits, chosen with the reasoning recorded

**A multi-tenant write with no tenant returns 500.** An application that declares multi-tenant entities
and cannot resolve a tenant is misconfigured, and refusing is much better than writing a row belonging
to nobody. The caller is authenticated and their token simply carries no tenant — a deployment mistake
rather than a bad request. The smoke test asserts the exact status, so making it a 4xx later has to be
a decision rather than a drift.

**`TenantContextMiddleware` still falls back to the `X-Tenant-ID` header and a query parameter.** The
signed claim now wins whenever the caller is authenticated, which closes the override. The fallback
remains for callers a token cannot describe — a gateway that has already terminated authentication, a
background job, local development — and in those deployments the tenant is caller-asserted. Issue
tenants in the token where clients reach the service directly.

**Studio needs the backend running to show a route.** Removing the last mirror means the designer and
playground read routes from a derived manifest, so with the backend down a route is *unknown* rather
than guessed. That is correct, and it is also a worse offline experience than the wrong answer it
replaced. The last successfully derived routes are kept and shown beside the error, so an editing
session survives a backend restart; a cold start with no backend shows none.

**A transition's endpoint takes the entity id in the request body**, not in the route
(`POST /api/orders/transitions/approve` with `{"entityId":"..."}`). That falls out of reusing the
custom-endpoint machinery, which binds one request object from the body.
`POST /api/orders/{id}/transitions/approve` would read better and needs route-parameter binding the
generator does not do for custom endpoints.

### Verified thinly, or only by inspection

**Coverage is a floor, not a finish.** The suites target each module's highest-consequence surface, not
its whole surface area. Still unexercised, and listed because the base rate for "never run" in this
codebase is now six for six: `CheckHealthAsync` on the connectors, the CSV exporter's streaming path,
GraphQL (mapped by the template, never in the smoke test), a real-time client connecting to a running
application, and the generated SDKs — the CLI suite covers `validate`, `schema build`, `ai-spec` and
LSP framing, not `sdk` or `export`.

**Archival is verified on a standalone server, which is not how it should be deployed.** The
copy-verify-delete path is what the tests exercise, because that is what the project's own
infrastructure supports. The transactional path is selected by a server capability check and is
covered only by inspection — the thing this document says is worth little. Running the suite against
a replica set would close that.

**The outbox is proven for one message, not under load or failure.** The round trip runs; what it does
not cover is a broker that goes away mid-publish, the retry ceiling of five attempts, or ordering
across a partition under concurrent writers. Those are the properties an outbox exists for, and they
need a test that can stop and start the broker.

**Workflow choice nodes cross the manifest boundary but nothing runs one.** They are emitted and the
behaviour resolves them, but no test and no smoke-test phase drives a decision gate — so this is the
one part of the workflow path still verified only by inspection, which is exactly the standing this
document argues is worth little. The IR also has nowhere to express a gate's default target, so an
unmatched gate is a routing failure rather than a fallback.

### Unexplained

**A flake was mitigated, not diagnosed.** Two `FoundryMongo.Tests` audit tests failed once in a
solution-wide run and were **never reproduced** across ~27 subsequent runs (isolated, under CPU load,
and solution-wide). The suites that share process-global state now run serially and the MongoDB
serialization configuration is registered from a module initializer, which removes a real class of
nondeterminism — xUnit runs classes in parallel, MongoDB freezes a class map on first use, and
NSubstitute queues matchers per thread. This suite had already produced one confirmed bug of that
shape. But the specific failure remains unexplained: the original assertion message was not captured,
and several candidate mechanisms were eliminated by reading the code. **If it recurs, capture the
assertion message before anything else.**

---

## 6. Recommended priority for the next cycle

Everything that was on a list is done: the catch-site audit, the smoke test extension, endpoint
authorization, row-level ownership, workflows reaching a running application, coverage on the
outside-world paths, the outbox round trip against a real broker, the last mirrored implementation,
and hot/cold partitioning. **Three of those were not on any list before the cycle that found them** —
they came from asking whether the framework was production-ready, then checking rather than answering
from memory. That remains the more useful lesson than any item below.

Two items, and the first exists because checking one unverified feature immediately found a sixth
failure. The order matters: finish looking before building more.

1. **Run the five remaining unexercised features once, end to end** — `CheckHealthAsync`, the CSV
   exporter's streaming path, GraphQL, a real-time client, and the generated SDKs. This was not on
   the list until partitioning was checked and failed; six for six is no longer a coincidence, and
   this is cheap relative to what it keeps finding.
2. **A second data provider.** The repository abstraction exists, so it is plausible rather than a
   rewrite — but it doubles the surface, and it should follow the verification work rather than
   precede it. Item 1 is the last of that.

Worth adding to that list before starting it, from section 5 rather than from this one: resource-level
authorization beyond ownership, a read path for workflow history, and the outbox under failure rather
than for a single message. None of those is damage; all three are gaps a buyer would find.

---

## 7. Strategic read

The differentiation is real and defensible: local-model-first so the domain model never leaves the
building, IR-not-C# so generated code cannot drift from best practice, and tenant isolation plus
envelope encryption at the data layer. That aims squarely at regulated .NET shops which OutSystems
prices out and Retool cannot serve. Nothing found undermines the thesis.

The risk was always execution, and the specific risk was sharper than "some bugs". The fastest way to
lose a regulated-industry buyer is a silent failure in tenant isolation or audit — and this codebase
contained exactly that: a cross-tenant broadcast of changed values, an audit trail that misdescribed
changes, and a test report that certified passes it had never observed. None of them would have
announced themselves.

The sharpest example arrived last. **Tenant isolation — named in the paragraph above as core
differentiation, and the first thing a regulated buyer would ask about — had never compiled, let alone
run.** Everything around it was in place and looked convincing: the validator was meticulous about
half-configured tenancy in the IR, the compiler emitted the interface, the repository carried a filter.
Only nothing had ever executed the path, and each layer's correctness made the next layer's absence
harder to see. That is the strongest argument in this document for the priority order: features that
have never run are worth less than features that are checked, and the difference is invisible from the
inside.

Then the same shape appeared one layer up, and worse. **Every generated API endpoint was anonymous
while its own documentation named the roles it required.** A reviewer reading the OpenAPI output, or
the schema, or the `Produces(401)` on the route, would have concluded access control was working. The
one thing that would have shown otherwise was sending a request without a token, and nothing did.

By the end the shape had repeated six times, which stops it being a coincidence and makes it a
property of how the project was built: **layers were completed in isolation and connected on faith.**
Each layer was reviewable and correct. The connection between them was neither, because nothing
executed it, and code review cannot see an absence.

Three lessons, in order of how much they would have saved:

1. **A guarantee is only as good as the request that tests it.** Tenancy and authorization were both
   verified by assertion-in-documentation, and both survived years of green builds.
2. **Ask the blunt question and then check.** "Is this production-ready?" was answered by reading the
   code rather than by recalling the design — which is the only reason the authorization gap was found.
   Three of the six were found this way, from a question rather than a plan.
3. **A comment claiming coverage is indistinguishable from coverage.** The Excel gap survived because a
   remark in a neighbouring test file explained it away, and nobody checked whether the thing it
   pointed at existed.

### Where this leaves it

Not production-ready, and much closer than it was. The front door works, the rooms have locks, and the
building tells you when something is wrong. What it still lacks is the paperwork a regulated buyer
asks for after they are satisfied it works at all: authorization richer than one ownership
relationship, a readable audit trail, and durability proven under failure rather than in the happy
case. Those are in section 5, and none of them is damage.

The honest one-line version: **what changed is not that the code is now correct, but that it can now
tell you when it is not.** Everything else depends on that, and it was the thing most missing.
