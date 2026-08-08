# Engineering Assessment — 2026-07-26 to 2026-07-28

An honest read of where Foundry stands, written from a working session *inside* the codebase rather
than from reading it. It is deliberately blunt: the point is to be useful for planning the next cycle,
not to market the project.

**Summary.** The architecture is strong and the differentiation is real. The implementation was
demo-grade. It is now verified where it matters: the repository builds from a clean clone, five CI
jobs pass, every module has tests, and a scaffolded application is driven over HTTP through
authentication, roles, ownership, tenancy, workflows and a restart. The suite went from 258 tests to
1,000, on a repository whose CI had never passed once.

The single most important finding is not any individual bug. It is this:

> **Ten separate features had never executed.** Not under-tested — never run.
>
> 1. **Multi-tenancy** did not compile. `IMultiTenant` needs a `set`; the compiler emitted `init`.
> 2. **Generated APIs were anonymous**, while their own OpenAPI output named the roles they required.
> 3. **Workflows** were compiled, validated, and went nowhere: the manifest never carried them.
> 4. **The Excel import path** had never been called by any test.
> 5. **The outbox** had never published, and the compose file meant to supply a broker exited on
>    startup — which is why nobody noticed.
> 6. **Hot/cold partitioning** archived nothing: the sweep ran inside a MongoDB transaction, which a
>    standalone server does not support, and the failure was caught and logged.
> 7. **The generated client SDKs** called `/api/v1/{singular}` in all three languages while the
>    application serves `/api/{plural}`, so every request 404'd — and the C# one did not compile.
> 8. **The real-time channels** required no authentication, in an application where every CRUD
>    endpoint answers 401 without a token.
> 9. **The GraphQL server** could not build its schema, so every GraphQL request any Foundry
>    application ever served returned a bare 500 — and `MapGraphQL` carried no authorization.
> 10. **`foundry export`** documented `/api/v1/{singular}` in OpenAPI and Postman, and emitted full
>     CRUD for entities that declare no REST surface at all.

Each looked correct in source. Each had convincing scaffolding around it — a meticulous validator, a
pipeline behaviour with its own tests, an OpenAPI description naming the roles. Reading the code
would have confirmed all ten were fine. That is the disposition worth naming: this codebase was
consistently good at *appearing* to work, and the appearance was load-bearing.

Every item on every list is now done. Several of the ten were not on any list until someone asked
whether an unverified feature worked and then went and ran it — and each failed within minutes of
being run for the first time. **The base rate for "never executed" in this codebase is ten for ten.**
The list of never-executed features is now empty for the first time; section 5 records what is
verified thinly instead, and should be read in that light rather than as a tidy backlog.

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

Since then the framework has been through five security reviews of that same "real senior-level work",
and each one found something. That does not contradict the assessment above — it sharpens it. The code
is competent and the defects were never incompetence; they were the difference between code that works
and code that permits only what it should. Section 2 separates the two.

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

### The second pattern: works exactly as written, and permits too much

The first pattern is exhausted as a search strategy — everything that had never run has been run. What
five consecutive security reviews then found was a different shape, and it needs its own name because
the method that catches it is different.

- A guard meaning *the order's total is over 10000* was equally satisfied by a number the caller sent.
- The tenant came from a header any caller could set, whenever their token did not carry one.
- A masked card number could be recovered a character at a time by filtering on it.
- Revision history was readable by anyone who knew an id, and ids are handed out in every response.
- Envelope encryption fell back to a mock whose key is published in this repository.

None of these failed. Every one behaved exactly as its author intended, returned the right status
code, and passed its tests. **The intent was the defect.** No test catches that, because a test asks
whether behaviour matches intent; no CI job catches it, for the same reason. Running the feature finds
nothing, because the feature works.

What found all five was reading the code and asking **what does this permit**, rather than whether it
looks right or whether it works. Three surfaces were reviewed that way — the repository's filter
composition, the search `criteria` parameter, and the connectors — and all three yielded defects in
code that was already tested and already green. Three for three, on top of ten for ten for the first
pattern.

The two patterns also fail differently, which matters for where to look. Silent success is found by
*execution* and hides where nothing runs. Excess permission is found by *reading* and hides in the
code that runs most. The archival sweep and the test generator were the first kind; `Repository<T>` —
reviewed twice and fixed twice before this cycle — was the second, and still held three read paths
that composed no isolation at all.

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
| `Build and test` | 1,074 C# tests across 14 suites, against a replica set **and** a standalone MongoDB; also type-checks the generated TypeScript SDK with `tsc --strict` and byte-compiles the Python one |
| `Outbox round trip` | 6 tests driving a mutation through MongoDB and a **real Kafka broker** |
| `Studio tests and typecheck` | 33 TypeScript tests, plus the bundle builds |
| `VS Code extension` | 17 TypeScript tests, plus typecheck and bundle |
| `Schema gates` | Sample schemas validate; the AI skill bundle regenerates and its golden examples validate |
| `Runtime smoke test` | Two scaffolded apps boot and are driven over HTTP with real JWTs: **authentication, declared roles, row-level ownership, workflow transitions and the real-time channels**, the CRUD contract (create, read, update, delete, filter, validate, optimistic concurrency, restart) and **tenant isolation** |
| `Distro binary` | The self-contained `foundry` binary **publishes and then runs**: version, the non-zero exit on no arguments, `validate`, `schema build`, and serving the embedded Studio page |

`Distro binary` closes a wide gap. `dotnet publish` of the shipped binary was failing while all six
other jobs stayed green, because `dotnet build` does not exercise the publish graph — so the one
artifact a user installs was the only thing nothing built. How long it had been broken is not
known; the last binary in `dist-bin/` was published on 25 July and predates every fix made since.
It was found by being asked for rather than by a gate, which is how most of what is in this document
was found. The job publishes and then runs what it published, because the Studio bundle it embeds
can go missing with nothing but an MSBuild warning to say so.

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
| `TenantContextMiddleware` | The signed token is the only source of the tenant; caller-supplied header and query values are ignored unless a deployment opts in |
| `AddFoundryMongo` | No longer registers a development KMS mock as the default `IKmsClient`; selecting envelope encryption without a real one throws instead of encrypting under a key published in this repository |
| `WorkflowTransitionBehavior` | A guard reads the one source it names (`entity` by default) instead of passing if either the entity **or** the caller's command satisfied it |
| Workflow external actions | Run after the handler, do not follow redirects, retry only repeatable methods, and cap what they read and record |
| `WorkflowActivityLog` | Declared-sensitive payload values are redacted instead of stored as sent |

That penultimate row took two passes, and the first was not enough. The header was read *before* the
token, so an authenticated caller could override the tenant their own token asserted just by setting
one. Ranking the claim first fixed that — but the header still applied whenever the token carried no
tenant, so a caller with a valid tenantless token could name any tenant at all. Only the second pass
made the token the sole source.

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

### The generated SDKs called URLs that do not exist

Three generators — TypeScript, C# and Python — and none had ever been executed by a test. All three
composed a route as `/api/v1/{singular-lowercase}`:

> `/api/v1/customer`, against an application serving `/api/customers`. **Every SDK the framework has
> ever produced, in every language, 404'd on every call.**

That is the identical mistake found and fixed in Studio's designer and playground earlier in this
session. It was fixed there and left here, which is the sharpest available argument for deriving a
route from `ApiManifestGenerator` rather than composing one: **a rule fixed in one copy is not
fixed.** All three now ask the single producer.

Two more, each characteristic of its language:

- **The C# SDK did not compile.** It emitted `public ObjectId Id` into a file with no
  `using MongoDB.Bson` and no driver reference, and named enums it never declared. Since the SDK
  ships as *source*, that was a build error in the consumer's project rather than in ours. Ids now
  map to `string` — a REST client has no business taking a MongoDB dependency to read an id the API
  sends as a JSON string — and the enums are emitted alongside.
- **The TypeScript SDK compiled and was wrong.** It lower-cased every field name, and the API applies
  no JSON naming policy: it serialises `FullName`, not `fullname`. Every field on every generated
  interface read back `undefined`, with no error anywhere. TypeScript compiling it happily is exactly
  why nothing caught it.

The C# SDK is now built by the same real-compile gate that has caught this class four times.

### The real-time channels were open to anyone

`/realtime/sse`, `/realtime/ws` and the SignalR hub required no authentication. Verified against a
scaffolded application, side by side:

| Request, no token | Response |
| --- | --- |
| `GET /api/customers` | **401** |
| `GET /realtime/sse` | **200**, streaming, with a connection id |
| `POST /realtime/hub/negotiate` | **200** |

These channels carry `AuditLogEntry` notifications, and an `AuditLogEntry` carries `PropertyDiffs` —
the changed values. So an anonymous client could watch every mutation in the system while being
refused the endpoint that produced it.

`NotificationHub` authorises subscriptions against the caller's roles, which an anonymous connection
does not have: that check was doing careful work on a principal nobody had established. All three
channels now require an authenticated caller, and the smoke test asserts 401 for each and acceptance
for a token holder.

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

### GraphQL had never built its schema

`app.MapGraphQL()` is in both the template and the sample, and GraphQL was recorded as covered
because there are fifteen tests against the GraphQL *connector* — a different component that shares
the word.

The server did not work at all, in the most complete sense available:

> `AddDynamicGraphQL` built the collection field as `ListType<Order>`. `ListType<T>` constrains `T` to
> a GraphQL type, not a CLR entity, so `MakeGenericType` produced a type the schema builder rejected
> during type discovery. The mutations separately passed the entity's *output* type where an *input*
> type belongs.

The schema therefore threw `SchemaException` on the first request and every request after it, and the
global exception handler turned that into a bare 500 with a trace id. **Every GraphQL query any
Foundry application has ever served returned "an error occurred while processing your request".**

Making it build exposed what it would have been if it had:

- **`MapGraphQL` carried no authorization**, while every REST endpoint beside it did — a full CRUD
  surface, `deleteOrder` included, open to anyone who could reach the port. This is the second
  instance of exactly the real-time defect, in a component mapped three lines away from it.
- **The manifest's roles were not enforced.** `SecurityBehavior` reads the `EndpointConfig` from the
  matched ASP.NET Core endpoint's metadata; the GraphQL endpoint carries none, so it returned early
  for every GraphQL request including mutations. Roles were enforced on one transport and documented
  on the other.
- **The collection resolver read `repo.Collection.AsQueryable()`** — the raw MongoDB collection,
  which applies no soft-delete filter, no tenant filter and no owner scope. The isolation failure
  fixed on the REST path was reachable through a door beside it. `IRepository` now exposes `Query()`,
  a queryable carrying the same read filters, and `Collection` says in its own summary that it does
  not.
- **Every entity got all five operations** regardless of what it declared, so an entity published
  read-only over REST accepted `create`, `update` and `delete` over GraphQL.
- **`[JsonIgnore]` meant nothing to it.** HotChocolate builds from CLR properties and does not read
  `System.Text.Json` attributes, so `isDeleted` was a settable input field. The comment on
  `Order.IsDeleted` states the exact reason that matters: hiding it "stops a PUT from setting it,
  which would delete a record via the update route and skip whatever roles the manifest applies to
  DELETE". `updateOrder(input: { isDeleted: true })` was that bypass, reopened.
- **The mutations could not be called correctly by anyone.** A non-nullable CLR property becomes a
  required GraphQL input field, so `createOrder` demanded `id`, `createdAtUtc`, `updatedAtUtc` and
  `version` — every one of which the repository assigns and then overwrites.

Gating fields by the manifest introduced a new crash of the same family — a read-only manifest
produces an empty `Mutation` type, which HotChocolate rejects — caught by the test written for the
gating itself. The root type is now omitted rather than emitted empty, and a manifest with nothing
readable is refused at startup instead of at first request. 13 tests.

### `foundry export` described an API that did not exist

Four formats, no test in any form, and all four exit 0 whatever they emit — so "it works" had only
ever meant "the process did not crash".

**OpenAPI and Postman composed `/api/v1/{lowercase-singular}` while the application serves
`/api/{plural}`.** That is the fifth and sixth copy of a route rule found to disagree with the one
that runs, after Studio's designer, Studio's playground and the three SDK generators. Both now read
`ApiManifestGenerator`.

Run against this repository's own showcase schema, the old exporter was wrong twice over: none of
that schema's three entities declares `apiEnabledMethods`, so the manifest gives them no REST surface
at all, and the exporter emitted six CRUD paths anyway. Wrong prefix and wrong existence,
independently.

Three more, each of which had never been looked at because nothing had ever read the output:

- **The OpenAPI exporter corrupted its own document.** C# cannot name a key `$ref` or `200`, so it
  serialised placeholders (`_ref`, `_200`, `application_json`) and repaired them with
  `string.Replace` over the whole serialised document — rewriting those substrings wherever they
  appeared. An entity named `Report_200` came out as `Report200`. Both exporters now build a
  `JsonObject`.
- **`PUT` was undocumented** — the collection had `get`/`post` and the item had `get`/`delete`, so
  update was missing from the specification of an API that serves it. Custom endpoints and workflow
  transitions were absent entirely.
- **Every Postman `POST` body was the literal `{"sampleField": "value"}`**, which no endpoint could
  accept, and no request carried an `Authorization` header against an API that answers 401 without
  one.

The AsyncAPI topic rule lower-cased the whole entity name where the dispatcher kebab-cases it, so a
single-word entity agreed by luck and every multi-word one named a topic with no publisher:
`PurchaseOrder` publishes to `purchase-order-events` and was documented as `purchaseorder-events`.

20 exporter tests, one of which asserts that every path in the OpenAPI document is a path the
manifest declares. The smoke test now goes further and checks the exported specification against the
**running server** — a claim two components cannot satisfy by agreeing on the same mistake.

### The fix existed; the generated application never got it

`Directory.Packages.props` pins MessagePack off 2.5.187 — two high-severity advisories and nine
moderate ones, arriving transitively through SignalR. Every project in this repository resolves the
patched version, and `dotnet list package --vulnerable` reports nothing across all thirty of them.

A scaffolded project does not:

```
> MessagePack  2.5.187  High  GHSA-hv8m-jj95-wg3x
```

`Directory.Packages.props` governs the directory tree it sits in, and a generated project is outside
that tree. **The framework's own dependencies were clean and the applications it generates were not**
— which is the half that matters, and the same shape as the template-versus-scaffold drift that let
generated APIs ship anonymous.

The scaffolder already pins packages it must, reading the versions from `Directory.Packages.props` so
the repository stays the single place that says which version. MessagePack was simply not on the list
of *which packages*, because the earlier pins were added to make a scaffolded project run rather than
to make it safe.

The check added is deliberately not "MessagePack is pinned". The smoke test runs
`dotnet list package --vulnerable --include-transitive` against the project it scaffolds and fails on
any hit, so the next transitive advisory is caught by the same assertion rather than by someone
remembering to look. Reverting the pin fails it.

### Masking is a policy rather than one switch

`view:pii` unmasked every masked property on every entity, so letting a caller read one field meant
letting them read all of them. "A claims handler may see a policy number but not a card number" could
not be said.

A masked property now names the **category** it belongs to, and the scope that unmasks it is
`view:{category}`. One mechanism, generalised rather than replaced — the category defaults to `pii`,
so every existing declaration keeps answering to `view:pii` exactly as it did, which a test asserts
rather than assumes.

The load-bearing assertion is the negative one: **`view:pii` no longer unmasks a property that names
another category.** Without that, naming a category would be decoration and the switch would still be
a switch. Reverting the per-property check fails four tests, that one among them.

Masking is decided per property rather than per entity, so a caller holding `view:policy` reads
policy numbers in full and still sees card numbers masked in the same response. The smoke test proves
it end to end on one record carrying two categories.

### Decision gates, driven for the first time

Emitted by the compiler, carried by the manifest, resolved by the behaviour, and never once
executed. The routing worked. The **unmatched** case did not:

> A gate whose branches all failed assigned `DefaultState` regardless — the empty string, which the
> manifest emitter hardcoded for every gate because the IR had no field for it — and saved. The
> record landed in a state no transition matches: unreachable, invisible to every state-based query,
> behind a 200 and a history entry naming `""`.

The comment beside that emitter said an unmatched gate was "a routing failure rather than guessing".
Nothing implemented that. It says what it does now: an unmatched gate with no declared default throws
and names the gate, and `defaultState` is a field the IR can express so the other answer is available
deliberately rather than by accident. `FDY3019` warns at compile time when a gate's branches are all
conditional and it declares no fallback.

Driving it end to end also caught the validator and the engine disagreeing about what a transition
may target. The engine resolves a gate by **id or name**; the validator accepted only the name — so
targeting a gate by its id, which is the obvious thing to write and what the manifest carries as the
node's identity, was rejected at compile time by a rule stricter than the one that runs. That is the
same "two implementations of one rule" that produced six wrong route prefixes, in a place nobody had
looked because nobody had written the schema that exposes it.

Five behaviour tests plus a smoke-test phase that routes a real record through a gate both ways and
asserts it never lands in the gate's own id nor in nothing at all.

### CI runs a replica set, and two things that could not execute now do

MongoDB offers multi-document transactions only on a replica set, and every environment this project
shipped was a standalone: its own `docker-compose.yml`, and the `mongo:7` service container in three
CI jobs. So the archival sweep's transactional path could not be selected *anywhere* — the worker
asks the server, gets no, and takes the copy-verify-delete fallback. Every existing assertion about
archival was exercising the fallback while reading as though it covered both.

Both are now single-node replica sets, initiated from a health check locally and from a composite
action in CI. One node is enough; the point is the replica set, not the redundancy. It is a composite
action rather than three copies of the same steps for the reason this repository keeps relearning.

A test asserts the server under test **is** a replica set, on its own, so that reverting the
infrastructure fails loudly. Without it every transactional test would still pass via the fallback
and report the wrong thing.

**The contended outbox was the other thing this unblocked, and it was broken.** Two workers sweeping
at once both selected the same row, both published it, and then the loser's optimistic-concurrency
failure was handled by writing the document again — so the message went out twice and ended marked
*processed and scheduled for retry at once*:

```
published=2 version=3 retry=1
```

Two changes. A message is now **claimed before it is published**, by an atomic find-and-update that
takes a one-minute lease on `NextAttemptAt`; whoever moves it wins and everyone else stops matching.
And the failure bookkeeping writes fields rather than replacing the document, so a failed attempt can
no longer overwrite another worker's successful mark. An at-least-once outbox cannot promise a
duplicate is impossible — a worker killed between publishing and marking will re-send — but it must
not make one the ordinary result of running two replicas.

### The client SDKs could not make a single successful call

Three SDK generators, and the C# one was compiled by a gate that had already earned its place — it
caught an SDK shipping `public ObjectId Id` with no `using MongoDB.Bson`, a build error in the
*consumer's* project. TypeScript and Python had no equivalent: every assertion about them was a
string comparison. Both turn out to be valid code that asks the wrong questions.

- **No authentication, in any of the three.** Every generated endpoint calls
  `RequireAuthorization()`, and the TypeScript and Python clients sent no `Authorization` header at
  all, so every call answered 401. The C# client takes an injected `HttpClient`, which is the
  idiomatic place for a token — but nothing said so.
- **The TypeScript client returned failures as data.** It passed every response straight to
  `res.json()` without looking at the status, so a 401 body was parsed and handed back typed as the
  entity the caller asked for. A failure that arrives typed as success is the worst shape a client
  can have. The C# `CreateAsync` did the same.
- **All three ignored `apiEnabledMethods`.** Every entity got `getAll`, `getById`, `create` and
  `delete` whatever it declared — so the showcase's SDK offered `delete_product` and
  `delete_ledgerentry` against entities that serve no DELETE.
- **And none of them offered `update`,** though four of the showcase's five entities declare PUT. The
  SDKs shipped calls the API answers 405 to, and omitted one it serves.
- **Only the key was optional in the TypeScript types,** so a caller had to construct every field to
  satisfy the interface — including the tenant and owner keys the server stamps from their token and
  refuses to take from a request body.

Three generators deciding the same question three times is what produced one answer three times
wrong, so `SdkSurface` now answers it once for all of them. Optionality follows the schema: the key,
the tenant key, the owner key and anything not marked `Required` are optional, and a `Required`
property stays required — asserted both ways.

The gates are the real fix. The TypeScript SDK is type-checked by the real `tsc` under `--strict`,
and the Python SDK byte-compiled by the real `python3`; both were verified by breaking the generator.
A live end-to-end phase was considered and left out on purpose: exercising the Python client against
the running application would need `requests` installed on the runner, and the route it calls already
comes from `ApiManifestGenerator.RouteFor`, which the smoke test checks against the running server
through the exported OpenAPI.

### The autonomous testing engine could not fail

`foundry test` generates xUnit suites from a schema. Nothing compiled them and nothing ran them, and
both halves of that showed.

**The route was wrong.** It composed `/api/v1/{lowercase-singular}` while the application serves
`/api/{plural}` — the *fourth* copy of that same rule, after the OpenAPI exporter, the Postman
exporter and Studio. The first three were corrected in earlier cycles; this one survived both
cleanups because nothing ever ran what it writes. It survived even though this module already
references the compiler: `RouteFor` was `internal`, so the one consumer that could have asked was
made to guess. It is public now, with `EnabledMethods` and `TransitionRouteFor`, for exactly that
reason.

**It asserted `200 OK` with no `Authorization` header**, against a framework where every generated
endpoint calls `RequireAuthorization()`. Every REST assertion it produced failed on a healthy
application, and blamed it.

**And five of its seven suite types could not fail at all.** The Kafka suite read `var topic =
"order-events"; topic.Should().NotBeNullOrEmpty();`. The FileIO and business-rule suites were a bare
`await Task.CompletedTask;`. The workflow suite compared a literal with itself. So the product whose
job is to tell you whether your application works could produce a green report without contacting
one — which is worse than producing nothing, because the report claims the coverage.

The tautologies are gone rather than papered over. Real-time and workflow suites now assert something
a schema can actually support: those channels and transitions are HTTP routes, so who may reach them
is checkable. Kafka, FileIO and rules suites are no longer emitted, because whether a message reached
a broker or a rule refused the right payload cannot be derived from a schema — that needs a harness
the developer writes, and saying so is more useful than a passing stub.

REST suites are emitted only for the methods an entity declares, GraphQL suites only for entities
that opted in, the POST payload carries the entity's required properties, and the address and token
come from the environment through one emitted helper rather than being baked into every file.

Two gates, and both were verified by breaking the generator. The suites are compiled by a real
`dotnet build` — a brace slip fails it. And the smoke test generates suites for the schema its
application was built from, points them at the running process and runs them: **18 generated tests
pass against a live application**, and putting the old route back fails six of them.

### The Studio backend wrote wherever it was told

`Foundry.Schema.Backend` is the service every compiler-backed feature in Studio talks to. It is in
the solution, so it compiled on every CI run — and nothing had ever sent it a request. No tests, no
job that starts it.

`/api/save-pocos` had a path-traversal guard that checked the output **directory** and then did
`Path.Combine(resolvedPath, file.Key)` with a key taken straight from the request body. Proven by
sending one:

```
POST /api/save-pocos   { "Files": { "../../../../../../../../tmp/x.txt": "…" } }
→ {"message":"Successfully saved 1 classes to: …/foundry-studio"}
```

The file was written to `/tmp`. So it escaped the workspace **and** reported success naming a
directory it had not written to. A guard that reads as protection and is not is worse than no guard,
because it stops anyone looking again.

Rejecting separators was not the fix — the compiler emits into subdirectories, so
`Commands/SubmitOrderCommand.cs` is an ordinary key. The rule is that the *resolved* destination must
stay inside the root, and it now lives in one place that both writing endpoints call.
`/api/save-manifest` was already correct; sharing the rule is what stops the two drifting apart
again. Containment is also checked with a trailing separator, so a sibling named `foundry-evil` no
longer passes a prefix test against `foundry`. The workspace root is read from
`FOUNDRY_WORKSPACE_ROOT` rather than derived from the current directory, because a boundary that
moves with the caller's shell is not one anyone can reason about.

Thirteen tests drive the endpoints over HTTP, six of which fail against the old guard. The success
response now lists the paths actually written.

**And the README sent every user into a broken setup.** It said to start Studio with
`foundry studio --port 5100`, which serves the *UI* on 5100 — the port the UI expects the *backend*
on. Following the documentation, every compiler-backed feature answered 404 against a server that was
plainly listening, and nothing anywhere mentioned starting the backend at all.

### The VS Code extension told users their schema had compiled

It had not been touched since before the IR became normative, it was in no CI job — not built, not
typechecked, not run — and it was the last component with no gate of any kind. Everything below was
verified by running it, not by reading it:

- **Its compile command had never worked.** It invoked the compiler positionally,
  `dotnet run --project ... -- <in> <out>`, and the compiler declares `--input` and `--output`. Every
  invocation exited with *"Both --input and --output are required"* having written zero files.
- **And every failure was reported as success.** The `if (error)` branch called
  `showInformationMessage("Foundry: Schema compiled! Saved manifest successfully.")`, with stdout and
  stderr discarded. The compiler failed every single time and said so to nobody — the silent-success
  class in its purest form, in the one place a user would never think to check.
- **"Create New Schema Manifest" wrote a document the toolchain refuses.** It emitted a Studio canvas
  file, and `foundry validate` answers FDY1010 — *"Document is in Studio canvas format, which the
  compiler does not consume. No code would be generated"* — plus FDY1002.
- **FDY1010's own hint named a command that did not exist.** It says to convert the document with
  `foundry migrate`, and the CLI had no such command; it printed the help banner. The one diagnostic
  that explains the difference between the two formats ended in an instruction nobody could follow.
- **The advertised LSP integration was not there at all.** No `vscode-languageclient` dependency, no
  reference to the LSP anywhere in the source, while the README called it "Native VS Code Extension &
  LSP Server integration". `foundry lsp` speaks the protocol and had its framing bug fixed a cycle
  earlier — and nothing had ever connected to it.

`foundry migrate` now exists and reads **both** shipped canvas shapes, because the extension emitted
an older one than Studio currently writes — a third format, discovered by trying to migrate the
extension's own output. It validates before writing, so a migration whose result the compiler would
still reject writes nothing, and it trims defaults because the migrated file is what its author edits
next.

The extension compiles through the real CLI, reports the compiler's own output in an output channel,
starts a language client against `foundry lsp`, and creates IR rather than canvas. It has tests for
the first time — the invocation logic is now free of any `vscode` import so it can be asserted
outside an editor, and reverting the flags to positional fails two of them — and a CI job that
installs, typechecks, tests and bundles it.

### One rule, three transports, one implementation

`realTimeRoles` says who may watch an entity's events. The framework ships three ways to watch, and
the rule was implemented in one of them.

`NotificationHub` checked the roles when a client subscribed over SignalR, and SignalR delivery had
already been narrowed to subscription groups precisely because an unconditional `Clients.All` send
bypassed that check. **SSE and WebSockets were that same send, still there.** Neither had any notion
of a subscription; neither recorded who was connected; both handed every mutation in the system to
every connected client. An `AuditLogEntry` carries `PropertyDiffs` — the before and after values —
so any authenticated caller could read the contents of every write to every entity, whatever the
schema demanded, by connecting to one of the other two URLs.

The earlier fix had been applied to the transport rather than to the rule, which is the same shape as
the six wrong route prefixes and the two Kafka topic namers. So the decision is now one thing,
`RealTimeAccessPolicy`, and all three transports call it: the hub on subscribe, SSE and WebSockets on
delivery. Both of those now capture the caller's principal when the connection is accepted — there
was nothing to check a role against before, because nobody had kept the identity.

It fails closed on a type the process cannot resolve, and says so at warning level rather than debug,
because that case is a misconfiguration whose only other symptom is silence.

Fifteen tests, nine of which fail if the policy is made permissive. The smoke test proves it against
a running application in both directions: a caller without the role gets nothing, a caller with it
gets the event — and the denied client's stream is asserted to have opened at all, since otherwise
"received nothing" would be indistinguishable from a dead connection.

### The showcase demonstrated a third of the framework, and compiled none of it

The one artefact that claims to demonstrate Foundry was hand-written C# sitting next to a schema
nothing compiled. `foundry validate` checked the schema in CI; no other thing read it. So the two had
drifted, provably:

| `e2e-schema.ir.json` said | The C# did |
| --- | --- |
| four entities | three — `CustomerNote`, carrying ownership and grants, did not exist |
| `Order` publishes to `order-events` | no Kafka reference anywhere |
| the submit endpoint requires `Customer` or `Admin` | `app.MapPost(...)` with no authorization |
| rules `OrderCreditLimitRule`, `SubmitOrderRule` | neither existed |

And it used **34 of the 100 declarable IR fields**. Zero workflows, DTOs or connectors; no
multi-tenancy, partitioning, real-time, file IO, caching, method gating or masking categories.

The showcase is now generated: everything under `Generated/` comes from the schema, and what is
hand-written is what a schema cannot state — the logic inside the scaffolds, one marker interface,
one workflow command, and the host. Its schema now uses **100 of 100**.

**Widening it was the point, and running it was the payoff.** Every construct that had never been
compiled was broken:

- **`enableGraphQL` produced a project that could not build.** The compiler emitted an
  `[ExtendObjectType]` query and mutation class calling `repo.AsQueryable()` and `repo.AddAsync()`,
  neither of which exists on `IRepository<T>`. It was also a *second* GraphQL implementation — the
  manifest-driven one in `Foundry.Api` already builds the surface, with role guards this one did not
  have. Deleted rather than repaired.
- **`enableGraphQL` also decided nothing.** It never reached the manifest, so `AddDynamicGraphQL`
  exposed every entity declaring a GET, including entities that had opted out. The flag now travels
  in the manifest, and the entities that did not ask for GraphQL are not served over it.
- **`enableFileIO` produced a file that did not parse.** Extension literals were emitted as `""`
  through a verbatim-string escape that interpolation does not re-process, so `".csv"` reached the
  output as an empty string followed by a bare `.csv`. Behind that, the service awaited an
  `IAsyncEnumerable` and passed an `IEnumerable` where one was required.
- **`enableFileIO` plus any `Required` property was a compile error even after that.** A generated
  entity with `required` members cannot satisfy the `new()` constraint `ExcelDataParser<T>` carried
  (CS9040), though it has a public parameterless constructor. The constraint was the wrong tool for
  what it was guarding.
- **`baseClass` made the entity unusable.** It *replaced* `BaseEntity<TKey>`, so the entity lost its
  Id and its `IEntity<ObjectId>` — and every generic in the framework is constrained on that. Naming
  a base class took the repository, the endpoint generator and the workflow engine down with it. It
  is now listed after `BaseEntity`, not instead of it.
- **`useCustomCommand` did nothing at all.** It was written into the generated workflow definition,
  read by no code in the engine, and the command and handler were emitted regardless. A transition
  asking to supply its own got the generated pair anyway. It now suppresses both.
- **`filterOperator` was ignored.** Every Query endpoint emitted `x.Field.ToString() == request.Value`
  whatever the schema declared, so `GreaterThan` silently became equality and the endpoint answered
  with the wrong rows. Now a closed set, where an unrecognised operator stops the compile.
- **An Update endpoint's handler referenced a property its request did not have.** It read
  `request.Id` unconditionally; nothing declared `Id` unless the schema named a `filterSourceValue`.
  Request fields are also typed from the property they are assigned to now — `Status =
  request.NewStatus` does not compile when one is an enum and the other a string.
- **Business rules were never registered.** `AddFoundryRules` registers the engine;
  `BusinessRuleBehavior` resolves `IBusinessRule<TRequest>` from the container, and nothing put them
  there. Both `apiBusinessRules` and a custom endpoint's `businessRules` were **declared, emitted as
  classes, and enforced by nothing** in every generated application. The compiler now emits
  `AddGeneratedBusinessRules()` and the scaffolder calls it.
- **`foundry schema build` wrote no `api-manifest.json`.** Only `foundry new` did, from its own second
  implementation. Compiling a schema into an existing project — the documented way to do exactly
  that — produced entities, handlers, rules and Kafka consumers, and an application with no API.

The scaffolded application also had no GraphQL endpoint while the README listed GraphQL as
first-class. That was previously left alone because mapping it unconditionally is a product decision;
now that `enableGraphQL` means something, it is the *schema's* decision, and `foundry new` maps
GraphQL exactly when an entity asks for it.

Two gates hold the line. One fails when the IR gains a field the showcase does not exercise, with an
allowlist that is empty and a second test asserting it stays empty. The other regenerates and
compares against the committed output, ignoring scaffolds by design and reporting orphans in both
directions. Both were verified by breaking the showcase and watching them fail.

### The fallback the replica set made unreachable

Moving to a replica set fixed the transactional archival branch and, in the same stroke, made the
copy-verify-delete fallback the one that could not run: the worker asks the server whether it
supports transactions, so **exactly one of the two branches can execute against any given server**.
The two swapped places rather than the gap closing, and that was recorded here as an open item rather
than left to be rediscovered.

The fallback is not the lesser path. It is what a developer gets when they point the framework at a
plain `mongod`, and it is what stands between an interrupted sweep and permanent data loss: insert
into the archive, confirm every document arrived, delete only then.

CI and `docker-compose.yml` now run a **second MongoDB, deliberately standalone**, on 27018. Both
branches run on every CI run. It is an ordinary service container rather than a composite action
because a plain `mongod` needs no post-start command — only the replica set does.

Ten tests drive the fallback, and they are structured so that each of the three steps fails on its
own rather than being covered incidentally by the happy path. Removing the verification step fails
three; making the inserts ordered fails one; removing the duplicate-key tolerance that lets an
interrupted sweep be re-run fails three.

**Running it found a defect the transactional path hides.** A year that cannot be archived aborted
every year after it in the same entity type — the rule the sweep already applied *across* entity
types ("one entity that cannot be archived must not silently block the rest") had never been applied
*within* one. Documents are selected by a filter on `_id`, so years arrive oldest first, which means
the abandoned years were the **newer** ones: the likeliest to matter, and the ones an operator would
notice last.

The test that catches this passed at first for the wrong reason. It put the failing year second, and
index order meant the good year had already been archived before anything threw — an assertion that
holds whether or not the years are independent, which is the same as not testing it. Reversing the
ages made it fail, and the fix is per-year failure accumulation matching the per-type accumulation
above it, flattened at the top so an operator reads causes rather than unwrapping two layers of
`AggregateException`.

### The outbox under failure, where it did not work

Recorded as "proven for one message, not under failure" for two cycles. Run under failure, it turned
out not to be unproven but broken.

The worker polls every two seconds, retried on that interval with **no delay between attempts**, and
selected messages with `RetryCount < 5`:

> A broker outage of ten seconds exhausted every pending message. Five attempts, two seconds apart.

And exhaustion was not an event — it was the absence of one. The fifth failure simply stopped
matching the query, so the rows sat in the collection unpublished, unmarked and unmentioned, while an
operator watching the queue drain saw precisely what success looks like. For a component whose entire
purpose is not losing messages when the broker is down, that is the worst available failure.

Two changes, and they are separable on purpose:

**Exponential backoff.** The same five attempts now span about four minutes rather than ten seconds,
which is the difference between surviving a broker restart and losing everything queued during one.
`NextAttemptAt` carries the schedule on the row, so it survives a worker restart too.

**Abandonment is recorded and announced.** `DeadLetteredAt` marks a message the worker has given up
on, and it is logged at critical the moment it happens. "Published", "still retrying" and "given up
on" were previously three states with two representations between them.

An abandoned message is deliberately *not* republished if the broker returns: releasing an
arbitrarily old message without someone deciding to is its own hazard, and clearing `DeadLetteredAt`
is that decision. Nine tests, six of which fail against the previous worker. They drive the worker's
own loop with a dispatcher whose availability the test controls, so what is under test is the retry
semantics rather than Kafka's.

Still not covered: ordering across a partition under genuinely concurrent writers. The sweep is
single-threaded and publishes oldest-first, which two tests assert, but two workers against one
collection would contend and nothing yet proves what happens.

### The catch-site audit

Carried out as a census rather than a hunt, because "audit the ~54 catch sites" had been on the list
for three cycles and the figure was inherited. **There are 85**, not 54. Classified by what each one
actually does:

| | |
| --- | --- |
| Rethrow or translate | 8 |
| Log and continue | 35 |
| Return a fallback | 40 |
| Genuinely silent and wrong | **1** |

The headline is the last row, and it is not the result the item implied. The swallowing had already
been fixed — not as an audit, but one site at a time as each feature was made to actually run. The
`Debug.WriteLine`-then-`continue` that silently widened a filtered result set, the activity log
written before the handler with `Success` hardcoded true, the connector that turned an unparseable
SOAP envelope into "no results", the outbox topic that failed validation and retried forever: each
was found by executing the path, not by reading the catch block.

**That is the transferable finding.** A census of `catch` blocks is a weak instrument — most of the
40 fallbacks are correct and documented, and reading them cannot tell you which of the 35 logged ones
matters. Running the feature tells you.

The one that survived: **`IdempotencyBehavior` stepped over a distributed-cache failure.** The
in-memory cache is per instance, so behind more than one replica the distributed cache is the only
thing that can observe a duplicate — and a warning-and-continue turned "at most once" into "at least
once" while every request still returned 200. For the operation idempotency exists to protect, that
is a double charge. It now fails closed *before* the command runs and stays permissive after it,
which is the whole design: a 409 is retryable and a duplicate payment is not, but once the command
has succeeded, failing the response is what would cause the retry that runs it twice.

Two bare suppressions were left in place with their reasoning written down rather than changed — a
browser that will not launch, and an abort inside `Dispose` that must not replace an in-flight
exception. An unexplained `catch {}` and a `/* Suppress */` are indistinguishable from an oversight,
which is the whole reason this item existed.

Worth knowing, and deliberately not changed: `RetryPolicyHelper` retries MongoDB operations three
times with no logging at all, so a flapping database is invisible until it fails outright; and
`AesEncryptionProvider.Decrypt` returns the input verbatim when it is not valid base64, which is what
lets encryption be enabled on an existing collection and also means a corrupted value round-trips
silently.

### Sensitive properties are actually masked

The last of the never-run features, and the same shape as the other ten: `MaskSensitiveFields` was
written, unit-tested in isolation, and **called by nothing**. A property declared
`[SensitiveData(Protection = Mask)]` came back in the clear on every transport, so the declaration
described a protection nothing performed.

Masking is applied in the repository rather than in each endpoint, so one rule covers REST, GraphQL,
the generated SDKs and anything else reading through `IRepository<T>`. Masking per transport is how
the route rule came to be wrong in six separate places.

Three things had to be decided, and one existing behaviour was wrong:

**`Encrypt` no longer masks.** `MaskSensitiveFields` masked every `[SensitiveData]` property
regardless of its protection, which made the two settings do the same thing on the way out. The
enum's own documentation says otherwise — `Mask` masks "for presentation/logs", `Encrypt` encrypts at
rest "and decrypts it on read" — so an encrypted field, declared that way to protect the database
rather than to hide the value from its own API, came back as a row of asterisks. Nothing noticed
because nothing called it.

**Writing back a masked read is refused.** This is the hazard masking creates, and it would have been
negligent to ship without it: a caller reads an entity, changes one field, writes it back, and
`j***e@example.com` replaces the real address — silently, irreversibly, through a change made to
protect data. The guard compares the incoming value against the stored one and refuses only when the
incoming value is precisely the mask of what is stored. That has no false positives, and it survives
`record with`, which produces a new object and defeats identity tracking entirely. An earlier version
of this guard *did* track identities, and its own test showed it guarding the case nobody hits.

**The seek cursor is taken before masking.** A cursor built from a masked value names a position no
document holds, so the next page comes back empty — and only for callers without the scope, on
entities that page by a masked field.

Two things the smoke test caught that unit tests could not: the default scaffold declares
`Customer.Email` with `MaskEmail`, so the durability assertion had been reading back a value that is
now masked; and the scaffolded projects share one database across runs, because the scaffold
hardcoded its database name while the template and the sample both read `MONGODB_DATABASE`. The
scaffold now reads it too — a scaffolded application could not otherwise be pointed at a second
database without editing its code — and each smoke run gets its own.

### Authorization reaches past a single owner

`ownerScoped` answered "this row belongs to one caller" and nothing further. Sharing, delegation,
team and hierarchy scoping all had nowhere to be expressed, so a schema needing any of them wrote the
rule by hand in a business rule — where nothing checked it had been applied to every read path.
Separately, exempt roles were per entity rather than per operation, so a role exempted from the owner
filter was exempted for reads, updates and deletes alike: **read-only oversight, the ordinary shape
for an auditor, could not be stated at all.**

Those became two additions rather than four, because three of the four are one predicate seen from
different angles:

> A row is visible to its owner and to any identity in its `SharedWith` set. The caller's identities
> are their subject plus the groups their token carries.

Granting to a subject id is sharing; granting to a group id is team scoping; granting to a
role-shaped group is delegation. Nothing in the data layer needs to know which of the three a
deployment meant, and the filter has one shape rather than three.

Four decisions in it are worth stating, because each is a place where the safe answer and the
convenient one differ:

**A grant confers read access only.** Updates and deletes stay with the owner and whoever holds a
full exemption. A grant that silently conferred write access would turn "let my colleague see this"
into "let my colleague rewrite this", and the smoke test asserts a grantee's `PUT` and `DELETE` both
leave the row untouched. Stated here so that widening it later is a decision rather than a drift.

**A grant never crosses a tenant.** It widens the owner predicate, which is a separate conjunct from
the tenant predicate — the same guarantee exemptions have always had. Asserted for both an individual
and a group grant, because "the same group id in another tenant" is the shape that would fail if a
grant were ever hoisted above the tenant filter.

**Both filter overloads were changed together.** `ApplyReadFilters` exists as a `FilterDefinition`
and as an expression tree, and the tenant filter was once missing from the expression one for as long
as it existed — so every list endpoint returned every tenant's rows. The expression overload builds a
chain of `Enumerable.Contains` calls rather than a nested `Any(...)` lambda, matching what `AnyIn`
does on the other side, and a test asserts a grantee's list result equals their by-id result.

**`ownerReadExemptRoles` is a second list rather than a richer `ownerExemptRoles`.** A role in both is
a warning (FDY3018), because it is fully exempt and the read-only listing then describes a
restriction that is not applied — a document that reads as though an auditor cannot write, when it
can.

The grant set is the one collection-typed property the framework understands, so it is exempted from
the unknown-type check rather than added to the scalar vocabulary: `List<string>` stays legal only
where the repository filter, the emitted property and the interface all know what to do with it. The
compiler emits `ISharedResource`, which extends `IOwnedResource` — and getting the accessor wrong
reproduces CS8854 exactly as multi-tenancy once did, which the generated-code compile check confirms
by failing when the setter is reverted to `init`.

### Workflow history can be read

`AppendActivityLogAsync` wrote an entry for every transition — who triggered it, when, from which
state to which, and the outcome of every automated action — and nothing served it. For a regulated
buyer the audit trail is the point, so a record that could be written and not read was half a
feature.

`GET {entity-route}/{id}/history` is now mapped from the manifest for every entity with a workflow,
alongside `MapDocsEndpoint` rather than through the source generator, because it needs no per-entity
code — only the list of entities that have one.

Two decisions in it are worth stating, because both were places to get it wrong:

**The entity is loaded first, and the history is served only if that succeeded.**
`WorkflowActivityLog` is not `IMultiTenant` and carries no owner, so querying it directly by entity
id would have been a read path beside the generated endpoints with none of their filtering — the
defect already found twice here, in the archive reader and in the GraphQL collection resolver.
Loading through the repository applies the tenant and owner filters, so a caller who cannot see the
record cannot see what happened to it. The smoke test drives two real transitions as one tenant and
then confirms the other tenant gets a 404.

**Access answers to the roles the entity already declares for `GET_BY_ID`.** Reading a record's
history is a read of that record, and inventing a second declaration would give an entity two answers
to "who may see this" — which is precisely how the two transports came to disagree in the GraphQL
case.

The response is a projection rather than the stored record, which keeps the log's own storage id and
audit columns out of an API contract. It also drops each action's `ResponseBody`: an action can
target any URL the workflow names, so relaying what came back would hand an arbitrary third party's
output to a caller authorized to read this entity and nothing else.

### A declared Kafka topic now reaches the publisher

Found while checking the AsyncAPI export against the runtime rather than against the other exporter,
and it turned out to be a defect in the outbox rather than in the export.

A schema may declare `kafkaTopic`, and the compiler used that name for exactly one thing: the topic
the generated consumer subscribes to. Nothing carried it to the publishing side.
`KafkaOutboxDispatcher` derived a topic from the event type alone, and `OutboxMessage` had nowhere to
record one.

> Declaring `"kafkaTopic": "orders.v2"` produced a consumer listening on `orders.v2` and a publisher
> writing to `order-events`. **The consumer received nothing, and both halves reported success.**

`OutboxMessage` now carries a `Topic`, resolved from a `[KafkaTopic]` attribute the compiler emits
onto the entity, and recorded when the message is enqueued rather than when it is published — so a
message sitting in the outbox across a deployment publishes where it was addressed rather than where
the current build would guess. A declared name gets the same legal-character check as a derived one,
because an illegal topic is refused at produce time with an error naming neither the topic nor the
event, and the worker retries that forever while reporting nothing.

The same investigation found the default naming wrong in the compiler independently of any
declaration. The dispatcher kebab-cases the type name; `PocoGenerator` and `AsyncApiExporter` each
lower-cased the whole thing. A single-word entity agreed by luck — which is why nothing had ever
shown, since every schema in this repository uses single-word names — and `PurchaseOrder`'s generated
consumer subscribed to `purchaseorder-events` while the publisher wrote `purchase-order-events`.

The two copies of that rule are now one on each side of a boundary the compiler cannot cross (it has
no project references at all), held together by a test in `Foundry.IntegrationTests` — the only
project referencing both — that calls each implementation and compares them. That is deliberately not
the shape used for the route rule, where a table of expected values was duplicated on each side and
drifted five separate times: **a shared table catches a rule that changes and cannot catch a rule
that is wrong in both copies.** Calling both implementations can.

The round trip is proven against a real broker: the event is published, it arrives on the declared
topic, and nothing arrives on the name the default would have produced. Reverting the dispatcher's
handling makes exactly that test fail.

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

### A guard could be answered by the caller

The workflow engine was reviewed deliberately rather than found broken by running it — it was the
largest surface nothing had audited, and the one that executes templated HTTP requests against
external systems on a caller's initiative.

**What was expected to be wrong was not.** Token substitution escapes each value for the grammar it
lands in: `JsonEncodedText` inside a JSON string, `Uri.EscapeDataString` inside a URL, and a header
value containing CRLF is rejected rather than stripped. A caller cannot change an action's host, add
a path segment, open a query, or inject a header. That work was done in an earlier cycle and it
holds. The findings were all in the code around it.

**A guard passed if either the entity or the caller's command satisfied it.** The engine evaluated
both and took the first that held. So a guard meaning *the order's total is over 10000* was equally
satisfied by a command carrying its own `TotalAmount` — a value the caller sets. Transition commands
are `partial` records and guards on request payloads are a supported feature, so the collision is an
ordinary design rather than a contrived one. The same fallback decided choice-node routing, which let
a caller pick which state they landed in. Conditions now carry `source` (`entity` by default) and
read the one object they name; an unrecognised value falls back to the entity, never to the caller.

**A failed transition still moved the record.** The entity was saved in its new state before the
handler ran, so a handler that threw left the record advanced while its own history correctly
recorded the failure: an order sitting in `Approved` whose log says approving it failed. The order is
now handler, then actions, then state, then log.

**A test was asserting the defect.** `TheHandlerRunsAfterTheStateIsPersisted` passed for as long as
the bug existed, because it asserted exactly what the buggy code did. It failed only on contact with
the fix. This is the second instance in two cycles of a green test documenting a defect as intent —
the tenant fallback was the first, recorded in prose rather than a test. **A passing test is evidence
that the code does what the test says, and nothing about whether that is what it should do.**

**Four limits an external action did not have.** It ran before the handler, so a transition the
handler went on to reject had already charged a card or sent a notification. It followed redirects,
so any service a workflow calls could answer 302 and send the request to an address of its choosing —
a metadata endpoint, or something reachable only from inside the network — with the response captured
into the log. It was retried on any method, so a `POST` that timed out could be delivered twice. And
its response body was unbounded both into memory and into a MongoDB document. All four are now
bounded; see the developer reference for the table.

**The activity log kept what the entity protects.** `PayloadDetails` stored the command as sent, so a
card number travelling through a transition was written in clear text to a second collection with
none of the entity's encryption or masking. Values declared `[SensitiveData]` or `[PiiData]` are now
redacted.

Twenty-six tests. Reverting the guard and the redaction fails five of them.

### Three read paths on the repository composed no isolation

The next surface on the list, reviewed with the same question. Eight read paths route through
`ApplyReadFilters` and both of its overloads now agree — the defect fixed in an earlier cycle stayed
fixed. Every write path scopes by tenant and owner. Three read paths did neither:

- **Revision history was keyed on the entity id alone.** `GetRevisionsAsync` and
  `GetRevisionByVersionAsync` queried `{Collection}_History` with no tenant and no owner predicate, so
  naming another tenant's id returned that record's full history. `[Encrypt]` fields stay ciphertext in
  a revision; `[Mask]` and `[SensitiveData]` ones do not, because masking happens on the way out. The
  repository's own `ScopeToTenant` already argues the point — *an id is not a secret* — and applies the
  filter to writes for that reason. Reads now load the record through the tenant- and owner-scoped
  filter first, and return empty rather than confirming the id exists elsewhere.
- **`CrossCollectionSearchAsync` applied the soft-delete predicate and stopped.** 206 lines with no
  reference to tenancy or ownership anywhere in them, projecting `$$ROOT` — the whole document — for
  every collection it was given.
- **`AggregateAsync` ran the caller's pipeline against the collection.** No `$match`, so any
  aggregation saw every tenant's rows and deleted rows too. It sat on `IRepository<T>` beside eleven
  methods that enforce isolation, with nothing in its name or documentation to say it was different.

None is a generated endpoint, so reaching them takes application code — which is why they matter
rather than why they do not. `IRepository<T>` is the layer this framework presents as the isolation
boundary, and code calling one of these is entitled to what the other eleven methods give.

**A fourth defect surfaced from fixing the second.** The tenant filter added to cross-collection
search matched nothing, and the test that expected rows got none. The cause was that a hand-built
`BsonDocument` stage does not resolve names through the class map, and `MongoDbConventions` registers
`CamelCaseElementNameConvention` — so `TenantId` had to be `tenantId`. **The soft-delete predicate
that method has always applied was written the same way**, which means cross-collection search never
excluded a soft-deleted row either. A wrong element name does not error; it matches nothing, and a
filter that matches nothing is a filter that does not filter. Element names now come from the class
map.

Six tests, all six failing when the fixes are reverted.

### Filtering was a read nobody entitled

The generated list endpoint's `criteria` parameter, reviewed next. It composes correctly — criteria
are ANDed into the expression `ApplyReadFilters` then extends, so no caller can widen past their own
rows — and it is not injectable: operators are a closed enum and values are typed constants, never
concatenated text. An unknown field throws rather than being ignored.

**But any property was filterable, including the ones every response masks.** Masking is a read-time
transform and the stored value is clear text, so `PaymentCardNumber startsWith "4111"` answered the
question the mask exists to refuse. Rows come back or they do not; sixteen digits fall out of a few
hundred requests, with every response along the way correctly masked. The showcase carries exactly
that shape — `Order.PaymentCardNumber` is `[Mask]` in category `financial`, and `Customer.PhoneNumber`
is `[Mask]` in `contact`.

The sharpest case is the auditor. `[OwnerReadExemptRoles]` exists so someone can read every row in a
tenant while still seeing sensitive fields masked — and an unrestricted filter turned that grant into
"may extract every value in the tenant", which is precisely what its shape was meant to prevent.

Filtering on a sensitive property now requires the same scope that unmasks it, refused rather than
silently dropped: a dropped predicate widens the result set, which is the wrong direction to fail. The
entitlement is `ShouldMask` itself, so "may read it" and "may filter on it" cannot drift apart.

Two limits came with it. `limit` was read from the query string and passed to the repository
unclamped, so `?limit=100000000` asked a generated endpoint to serialise as much as the tenant held —
`MaxDepthCap` guards offset paging and never applied here; it is now bounded to 500. And a request may
combine at most 32 criteria, which bounds the expression tree rather than protecting anything.

Seven tests. Five fail when the entitlement check is removed; the two that still pass are the
controls — an ordinary field keeps filtering, and an entitled caller still gets both a hit and a miss.

### The connectors, and the rule that never existed

The last of the three surfaces. Two findings mattered.

**No connector client had a redirect policy or a response cap.** Any service a connector called could
answer 302 and send the request — carrying that connector's credentials — somewhere it was never
configured to go, and answer with an unbounded body. The workflow engine had been hardened against
exactly this one cycle earlier.

The instinct is to call that a missed propagation. It was not: the two outbound paths were never one
rule, so there was nothing for the earlier fix to propagate along. `OutboundHttpPolicy` in
`Foundry.Core` is now the single producer, and both the connectors and the workflow engine build their
clients from it. This is the fourth time a rule has been collapsed into one place after existing in
two — route composition, SDK surface, real-time access, and now outbound HTTP — and the first time it
was found by reviewing the *second* implementation rather than by the two disagreeing.

**Connector credentials were literals in the IR.** `password`, `apiKey` and `token` took plain
strings, and the IR is committed to source control, opened in Studio, and passed to a local model as
prompt context by `foundry ai` — three places a secret should never reach, none of which look like a
mistake while it is happening. `FDY3020` now requires a `${...}` reference. The showcase already used
that form: the convention existed and nothing enforced it.

Also: an absolute `endpoint` silently overrode a connector's `BaseUrl`, taking its credentials to
another host, and `SOAPAction` was interpolated into a quoted header unvalidated. Both refused now.

XXE was checked and is **not** a finding. `XmlSerializer` creates a reader with `DtdProcessing.Prohibit`
on .NET Core and later, so the SOAP path is safe — by platform default rather than by anything this
code chose, which is worth knowing but not worth changing working code over.

Ten tests. The validator rule was written and inserted in the wrong place, where it ran only when
there was no schema to validate; four tests failed on it. Had only the accepting case been written, it
would have passed and the rule would have been dead code — the same shape as every silent gate in this
document, this time caught before it shipped.

### A bulk write picked its rows with a read filter

`Repository<T>` was decomposed into collaborators over four pure-move commits, and putting the access
policy in one file for the first time is what showed that `BulkUpdateManyAsync` selected the rows it
was about to replace with `ApplyReadFilters`. That call passes `forWrite: false`, which is the one
flag that lifts the owner filter for an `[OwnerReadExemptRoles]` holder and that adds the `SharedWith`
disjunct for a grantee. Nothing downstream re-checked: the version filter a bulk write carries is
scoped to neither tenant nor owner, and unlike `BulkUpdateAsync` there was no second scoped read to
skip rows on.

**So the rows a caller could see were the rows a caller could replace.** Two guarantees this codebase
states in its own source were not true through this one method — an auditor holding a read-exempt
role overwrote another owner's row, and a grantee on `SharedWith` overwrote the owner's row.
`OwnerReadExemptRolesAttribute` promises that its holder *"can still only modify their own rows"*, and
`ISharedResource` that *"a grant confers read access only"*, on the stated grounds that a grant
conferring writes would turn "let my colleague see this" into "let my colleague delete this". Both
were reproduced against a real database before anything was changed rather than inferred from
reading, and both are reachable with no race and no concurrency of any kind.

**Why it was invisible.** Nothing failed. The write was applied, the audit entry written, the revision
saved, and the caller handed an acknowledged `UpdateResult` with a matched count — the correct-looking
answer to an operation that had just corrupted somebody else's row. The one line that made a write
into a read is three hundred lines from the write paths that get this right, and the method name says
nothing about reading. It is the silent-success pattern this document is about, sitting in the
authorization layer.

**It was found by the refactor, not by a test.** The ownership and grant suites cover exactly these
two rules, in detail, and every test in them passed — because every one of them addresses its write by
an id, and every write addressed by an id was correct. The rule was right everywhere it was stated;
this path never stated it. No test that existed could have caught this, and none of the coverage was
wrong, which is the case for reading code against what it permits rather than adding tests to a
surface that is already green.

**The fix is in the row selection, not in the version check.** `ApplyWriteFilters` is the write-side
counterpart of the expression overload — the same soft-delete and tenant predicates, with the owner
scope taken on the write side — and the two share one body, so they cannot drift the way the two read
overloads once did. Scoping the bulk version filter instead would have blocked exactly the same writes
and reported them wrongly: a replace matching no row routes into `ThrowOnBulkConcurrencyConflict`, so
an authorization failure would have reached the caller as a concurrency conflict, inviting a retry
that can never succeed. A row the caller may not write is now not a candidate, and a bulk update with
nothing writable in it returns an empty result — the same answer as a filter that matched nothing,
which is what it is.

**The sweep for the same shape found nothing else.** Every call site of `ApplyReadFilters` and of
`TryGetOwnerScope` was checked, on the principle that this codebase has repeatedly had one rule wrong
in several places at once. Ten call sites, nine of them genuine reads, and this the only write among
them; every other write composes `ScopeToOwner(ScopeToTenant(...))`. `BulkUpdateAsync` was verified
rather than assumed — it re-reads each row through the write-scoped filter and skips what does not
match. `CachedRepository` and `PartitionedRepository` both delegate to this method and inherit the fix.
A useful negative result, and the first sweep of this kind here to come back clean.

Six tests. Four fail when the one-line selection change is reverted, and two of those failures
reproduce verbatim the strings the original investigation recorded — `OVERWRITTEN BY AUDITOR` and
`OVERWRITTEN BY GRANTEE`. The other two are controls against over-correcting: a fully exempt role must
keep the write breadth `[OwnerExemptRoles]` does grant, and an owner must still be able to bulk-update
their own rows. Both fail if the selection is scoped by owner unconditionally instead of through the
write-side scope. MongoDB is the oracle for all six, deliberately: the predicate does not stay in .NET
— the LINQ provider translates it into a query the server evaluates — so a compiled delegate would
answer what an in-process `Func` admits and would agree with itself about an owner predicate the
provider had dropped. The assertions are on the stored body of each row, because the harm is a row
whose contents were replaced.

## 4. Coverage and what covering it found

Every module has tests. **1,074 C# tests in total**, from 258 at the start, plus 50 TypeScript across
Studio and the VS Code extension. Counts below are read off a solution-wide run rather than carried forward — the figures in
this table had drifted from the suites they describe, which is the same defect the document is about:

| Suite | Tests | Needs |
| ----- | ----: | ----- |
| `foundry-schema` | 277 | — |
| `foundry-mongo` | 145 | MongoDB, **both** a replica set and a standalone |
| `foundry-rules` | 118 | — |
| `foundry-integration-tests` | 90 | MongoDB |
| `foundry-api` | 81 | MongoDB |
| `foundry-file-io` | 75 | — |
| `foundry-core` | 52 | — |
| `foundry-connectors` | 60 | — |
| `foundry-kafka` | 39 | — |
| `foundry-schema` backend | 13 | — |
| `foundry-studio` | 33 | — (TypeScript) |
| `foundry-vscode` | 17 | — (TypeScript) |
| `foundry-realtime` | 40 | — |
| `foundry-testing` | 37 | — |
| `foundry-cli` | 41 | — |
| `foundry-kafka-integration` | 6 | MongoDB **and** a Kafka broker |

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

**Masking categories are scopes, and scopes only.** A caller is entitled by `view:{category}` and by
nothing else — a deployment issuing roles rather than scopes has to mint a scope claim to use masking
at all. One mechanism was chosen deliberately over accepting both, but it is a constraint worth
knowing before adopting.

**Group claims are read from `groups` and `group` and are not configurable.** Role and tenant claim
names are both configurable under `Authentication:Jwt`; this one is not, so a provider emitting
something else silently matches no grants — which looks exactly like "the user has no access", the
least diagnosable outcome available. Matching two spellings covers the common providers and is not
the same as letting a deployment say.

**An unauthenticated principal is not narrowed by ownership or grants on reads.** With no
authenticated caller the owner filter does not apply, which is deliberate — background jobs,
migrations and the archival worker read without a caller and must see every row — and safe only
because every generated endpoint refuses an unauthenticated request before the repository is reached.
It is defence in depth this layer does not provide, so a host exposing a repository without
authentication in front of it has no row-level filtering at all.

**No token issuance, and no refresh or revocation story.** The framework validates tokens; it does not
mint them. That is the right split — an identity provider's job is not a code generator's — but a team
adopting this still has to stand one up, and the scaffolded project says so only by leaving the
configuration empty.

**Nothing else known.** The MessagePack advisory that stood here is fixed; see section 3.

### Deliberate limits, chosen with the reasoning recorded

**MongoDB is the only data provider, and that is now a decision rather than a gap.** The repository
abstraction would make a second one plausible, and it was carried in this document as the largest
piece of outstanding work and the commercial ceiling for enterprise .NET shops that mandate SQL
Server. The owner has descoped it: Foundry targets document-shaped domains on MongoDB. Recorded here
rather than deleted, because a reader evaluating the framework needs to know it is a boundary that was
chosen, not one nobody noticed.

**A multi-tenant write with no tenant returns 500.** An application that declares multi-tenant entities
and cannot resolve a tenant is misconfigured, and refusing is much better than writing a row belonging
to nobody. The caller is authenticated and their token simply carries no tenant — a deployment mistake
rather than a bad request. The smoke test asserts the exact status, so making it a 4xx later has to be
a decision rather than a drift.

**`TenantContextMiddleware` no longer trusts the `X-Tenant-ID` header by default.** Ranking the signed
claim above the header closed the override but not the hole: the header only ever lost to a claim that
*existed*, so a caller holding a valid token that simply did not describe tenancy met no claim to lose
to and could name any tenant they liked. The paragraph that used to sit here described that as an
acceptable residual risk. It was not — it was a tenant-isolation bypass reachable by anyone the
deployment had authenticated, in the feature the framework leads with.

The token is now the only source. Deployments where something in front genuinely establishes the
tenant opt in with `services.Configure<TenantContextOptions>(o => o.TrustCallerAssertedTenant = true)`,
which names the trust boundary instead of assuming it. The smoke test asserts the closed case against
a live application: a tenantless token that sends `X-Tenant-ID: globex` is refused, and no row appears
in globex.

**The two bulk paths still build their version check without tenant or owner scoping.** The three
single-document write paths scope theirs; `BulkUpdateManyAsync` and `BulkUpdateAsync` filter on the id
and the version alone. Now that the selection defect above is fixed, both are in the position
`BulkUpdateAsync` has always been in: the unscoped filter is applied to a row that a scoped read has
just returned. Reaching past it needs a writer outside `IRepository<T>` — a migration, an admin tool,
anything using the publicly exposed `Repository<T>.Collection` — that changes a row's `TenantId` or
`OwnerId` **without bumping `Version`**, which no path through the repository does, *plus* a race
landing in the window between that read and the `BulkWriteAsync`. Missing depth rather than an open
door. It was left out of the selection fix on purpose, so that each is one decision: with the
selection corrected, closing this is a cheap defence-in-depth change rather than a behaviour change
with an error-reporting problem attached. `EntityWriteGuardTests.TheBulkFilterIsNotTenantScoped`
records today's behaviour so that changing it has to be deliberate.

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
its whole surface area. Everything previously named here has now been run, and the list is empty for
the first time — which is a milestone about the *list*, not about the surface area, and the base rate
below is the reason to keep looking rather than to stop.

**The workflow engine has now been read rather than only run.** The real-time gating that stood here
is fixed; see section 3. The workflow review that followed produced seven findings, four of which
were reachable defects and three of which remain below as stated limits. It is worth noting *how*
they were found: not by a failing test, and not by exercising the feature — the feature worked — but
by reading the code asking what it permits rather than whether it looks right. Tests and CI cannot
ask that question, and neither had. The rest of the framework has had far more running than reading.


**An archival sweep loads a whole entity type into memory before moving anything.** Both branches
share this: `Find(filter).ToListAsync()` reads every document past the threshold, and the
transactional branch then writes a year of them in one transaction — against MongoDB's 16MB oplog
entry limit and its 60-second default transaction lifetime. Nothing here has been run at a size where
that bites, so this is a stated limit rather than a measured one. Batching within a year would fix
both, and would change the branch that currently works, so it is not a change to make on a hunch.

**A duplicate publish is still possible, by design rather than by accident.** A worker killed between
publishing and marking the message will re-send it when its lease expires. That is what at-least-once
means and the reason consumers must be idempotent; the claim removes duplication as the *normal*
result of running two replicas, not as a possibility.

**A workflow action that fails after an earlier one succeeded leaves that effect in place.** Actions
now run after the handler, which removes the common case — a transition rejected by its own handler
after it had already called out. What remains is a partial sequence: the second action fails, the
first has already happened, and nothing compensates. Every executed action is recorded in the
activity log including on the failure path, so the state is knowable rather than hidden. A saga is
the real answer to this and is a larger thing than the engine currently is.

**Payload redaction covers top-level properties only.** A value declared `[SensitiveData]` or
`[PiiData]` inside a nested object is serialised whole and reaches the activity log. Transition
commands are flat records by construction, so this is a limit rather than a live exposure — but it is
a limit that a future command shape could quietly walk into. A recursive walk with cycle detection
over arbitrary caller types is the fix, and was judged larger than this needed to be.

**`InternalApi` resolves a command type by simple name across every loaded assembly.** The engine
takes the schema's `requestType`, searches `AppDomain.CurrentDomain.GetAssemblies()` for the first
type whose `Name` matches case-insensitively, deserialises the payload into it and `Send`s it. The
name comes from the schema rather than from a caller, so this is not reachable from outside — but two
types sharing a simple name resolve by assembly load order, which means the command that runs can
depend on something no one is thinking about. `WorkflowEntityTypeRegistry` already exists as the
pattern for solving exactly this for entity types, and the same treatment would fit here. **Not
fixed**, and named because a reviewer should not have to rediscover it.

**A transition with no roles and a state with no roles is open to any authenticated caller.** That is
consistent with how the rest of the framework treats an absent policy, and it is stated here because
"no roles declared" reads as *restrictive* to most people and means the opposite. For a state machine
whose transitions are the privileged operations, it is worth an explicit decision per workflow rather
than a default nobody looked at.

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

**The list of never-executed features is empty.** Ten were checked, one at a time, over three cycles:
multi-tenancy, endpoint authorization, workflows, Excel parsing, the Kafka outbox, hot/cold
partitioning, the generated SDKs, the real-time channels, the GraphQL server, and `foundry export`.

> **Ten for ten. Every feature that had never been executed was broken.**

That is the single most useful number in this document. It is a statement about a *method*, not about
these ten features: reading the source found none of them, and running it found every one. Several
were not on any list before the cycle that found them — they came from asking whether the framework
was production-ready and then checking, rather than answering from memory.

The base rate also argues against treating the empty list as a finish. What it means is that the
cheapest question — "has this ever run?" — no longer has an obvious target, so the next cycle has to
ask a more expensive one.

It did, and the more expensive question has its own number now. Three surfaces were reviewed by
reading them and asking what they permit rather than whether they work:

> **Three for three. Every surface reviewed for what it permits was permitting too much.**

Two numbers, two methods, and neither one substitutes for the other. Ten for ten came from running
things; three for three came from reading them, on code that running had already declared fine.

**Nothing is knowably unexercised any more.** Every feature that had never run has been run, and every
path recorded as "run once, never under stress" has been driven under it. What follows is design work
rather than verification, which is a different kind of list and a more expensive one.

**And the backlog is empty.** A second data provider was the one item left on it, and it has been
descoped rather than deferred — see section 5. There is no recorded work outstanding, which is a
statement about this document rather than about the software: the next thing worth doing has to be
chosen from the product, not read off a list.

### The next question is not "has this run?"

The workflow review changed what this section should say. That engine had been run — its transitions,
its decision gates and its history endpoint all have tests, and they passed. Running it again would
have found nothing. Reading it with a different question, *what does this permit?*, found a guard a
caller could answer, a failed transition that still moved the record, and four missing limits on an
outbound HTTP call.

So the cheap question is exhausted and the replacement is now known: **read the security-relevant
paths asking what they allow, not whether they work.** Two of the last three genuine findings —
the tenant header fallback and the workflow guard — were features working exactly as written, where
what was written was wrong. Neither a test nor a CI job can catch that class, because both ask
whether behaviour matches intent and the intent was the defect.

The surfaces that deserve that treatment next, in order:

1. ~~`Repository<T>`'s filter composition.~~ **Done** — three unfiltered read paths and one
   silently-inert filter, in section 3. It produced the strongest argument yet for this method: the
   file had already been reviewed twice and fixed twice, and still held three paths that composed no
   isolation at all.
2. ~~The generated GET endpoint's `criteria` parameter.~~ **Done** — a filter oracle over masked
   values, and an unclamped page size, in section 3.
3. ~~`Foundry.Connectors`.~~ **Done** — no redirect or size policy on any client, and credentials as
   literals in the IR, in section 3.
4. ~~Every other hand-built `BsonDocument` filter.~~ **Done** — `BuildBsonFilter` had the same defect
   one line from the one already fixed, and survived a cycle longer because it costs results rather
   than isolation: a filter matching no field returns nothing, and for a *search* "no results" is a
   plausible answer every time. The only other raw-BSON name, a `$sort` on `EntityId`, is correct —
   it sorts on a field the projection produces. Worth noting that "same root cause, lower severity"
   is a category that gets deferred and then forgotten.

The three workflow items left unfixed above are smaller than any of these, and are recorded so they
are not rediscovered rather than because they should come first.

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

By the end the shape had repeated eight times, which stops it being a coincidence and makes it a
property of how the project was built: **layers were completed in isolation and connected on faith.**
Each layer was reviewable and correct. The connection between them was neither, because nothing
executed it, and code review cannot see an absence.

Three lessons, in order of how much they would have saved:

1. **A guarantee is only as good as the request that tests it.** Tenancy and authorization were both
   verified by assertion-in-documentation, and both survived years of green builds.
2. **Ask the blunt question and then check.** "Is this production-ready?" was answered by reading the
   code rather than by recalling the design — which is the only reason the authorization gap was found.
   Five of the eight were found this way, from a question rather than a plan.
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

One qualification, earned the hard way over five security reviews. Telling you when it *fails* and
telling you when it *permits too much* are different capabilities, and only the first has been built.
A gate reports a broken thing; nothing reports a thing that works precisely as written and allows more
than it should. Five such defects were found here — a caller-answerable guard, a caller-assertable
tenant, a filter oracle over masked values, revision history readable by id, and envelope encryption
falling back to a published key — and not one of them was found by anything automated, because each
was behaving correctly by its own definition. That gap is not closed and probably cannot be closed by
tooling. It is closed by someone reading the code with the right question, which is now the first item
in section 6 rather than an afterthought.

---

## 8. Addendum — 2026-08-08

A production-readiness review external to this document (not one of the five security reviews above)
concluded the same thing section 7 already said — not production-ready, closer than it was — and named
six concrete gaps, four of them lifted straight from section 5 and 6 above rather than newly found.
This addendum records what closed and what did not, in the same register as the rest of the document:
what changed, not why it should be believed to be enough.

**Closed, matching section 5's own description of the fix:**

- **The bulk-write OCC asymmetry**, named in section 5 as "now exactly the cheap defence-in-depth
  change described above, with no error-reporting problem attached to it." Both `BulkUpdateManyAsync`
  and `BulkUpdateAsync` now build their optimistic-concurrency filter through the same scoped
  `_writeGuard.OccFilter` every single-document write path already used, instead of the static
  unscoped one. `EntityWriteGuardTests.TheBulkFilterIsNotTenantScoped` — the characterization test
  that pinned the old behaviour — is renamed and its assertion inverted, and a cross-tenant refusal
  test now exists for the bulk path the way it already did for single-document writes.
- **Group claim names**, named in section 5 as "read from `groups` and `group` and are not
  configurable." `FoundryAuthenticationOptions.GroupClaimTypes` now exists, bound the same way
  `RoleClaimType` already was, and pushes into `Foundry.Core.Security.GroupClaims.Types` — kept as a
  settable static rather than reaching across the `foundry-mongo` → `foundry-api` assembly boundary.
- **`InternalApi`'s reflection scan**, named in section 5 as "Not fixed, and named because a reviewer
  should not have to rediscover it." A `WorkflowCommandTypeRegistry` now exists in `foundry-api`,
  mirroring `WorkflowEntityTypeRegistry`'s own design exactly — explicit registration, actionable
  failure on a miss — reached through an `IWorkflowCommandTypeResolver` interface in `foundry-rules`
  so the dependency direction stays intact. Finding this fix's first draft is itself a small data point
  for section 6's thesis: it passed `foundry-rules`' own test suite, and broke a pre-existing test in
  `foundry-api`'s — `WorkflowEngineTests`, which constructs `WorkflowEngine` directly rather than
  through DI — that a narrower test run did not touch. Caught by running the whole suite, not by
  reading either change.
- **The archival sweep's unbounded load**, named in section 5 as "nothing here has been run at a size
  where that bites, so this is a stated limit rather than a measured one." `ProcessPartitionedTypeAsync`
  now chunks its read against the same `_id`-ordered filter the sweep already used — because archiving
  deletes what it processes, repeating the filter naturally yields the next batch — so neither the
  in-memory load nor a single transaction is sized by how much a type has accumulated.

**Named in section 5, addressed but not closed the way the item asked:**

- **The no-roles transition/state gap** — section 5's own framing was "worth an explicit decision per
  workflow rather than a default nobody looked at," and that is what shipped: a schema-validation
  warning (`FDY3021`) when a transition or state declares no roles, not a change to the runtime
  default. The behaviour section 5 described is unchanged; it is now visible at compile time instead
  of silent.
- **Payload redaction's top-level-only limit** — extended one level of nesting, not the full recursive
  walk section 5 judged "a larger thing than this needs to be" and declined for the same reason here.
  A sensitive value two levels deep still reaches the activity log un-redacted; the class's own remarks
  say so now, where they previously said only "top-level."

**Not named in section 5 — a different kind of gap, found by the same question ("is this
production-ready?") asked of the *project* rather than the runtime:**

- No LICENSE existed. Now Apache-2.0, repo-wide.
- No version discipline existed: no git tags, no `Directory.Build.props`, and `foundry version` printed
  a hardcoded string that had already drifted from what `foundry doctor` read off the assembly. Fixed
  with a single version source and CI wired to stamp it into the distro binary and its artifact name —
  which promptly caught its own bug: `git describe --tags --always` never fails, so with no tags yet it
  silently returned a bare commit hash instead of a version, and that non-numeric string reached
  `-p:Version=` and failed the build. Not caught by writing the fix or by the local build — the local
  publish target didn't match the CI runner's architecture, so it was the actual CI run, on the actual
  runner, that found it. One more instance of section 6's distinction: reading the script found nothing
  wrong with it; running it did.

**Verification.** Full solution: 1,124 tests, 0 failures, across 14 suites (`bash scripts/run-tests.sh`,
against a live replica-set Mongo, standalone Mongo, and Kafka broker — the same infrastructure-gated
suites section 4 describes). CI: all 7 jobs green on the commit that includes this addendum, including
`distro-binary`, the job that failed on the first push of this cycle for the reason above and is now
fixed.

**What this addendum does not claim.** Every item section 5 marked "would block a regulated
deployment" and did not name above is still open: masking is still scope-only, an unauthenticated
principal is still unnarrowed by ownership on reads, there is still no token issuance story. The
base rate in section 6 — running finds what reading missed, and reading-for-permission finds what
running missed — is not retired by this cycle either; it produced one more confirming data point
(the CI version bug) rather than a reason to stop asking the question.
