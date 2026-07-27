# Engineering Assessment — 2026-07-26

An honest read of where Foundry stands, written from a full working session *inside* the codebase
rather than from reading it. It is deliberately blunt: the point is to be useful for planning the next
cycle, not to market the project.

**Summary.** The architecture is strong and the differentiation is real. The implementation was
demo-grade, and it is now *verified at its edges* — the repository builds from a clean clone, CI passes
for the first time, a generated application is proven to boot and persist, and every module has tests.
That is a floor, not a finish. The single most important finding is not any individual bug: it is that
the codebase had a consistent disposition toward **silent failure**, which is the worst possible default
for a code generator, and that disposition is only partly corrected.

---

## 1. Where it genuinely stands

The runtime layer — `foundry-mongo`, 4,775 lines across 27 files covering tenant filter injection,
envelope encryption, optimistic concurrency, seek pagination and hot/cold partitioning — is real
senior-level work and held up under inspection. (An earlier draft of this document said 11.5k lines;
that figure was inherited and never checked. Measured, `foundry-mongo/src` is 4,775 lines and the whole
module including tests and samples is 6,909.)

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
| `Build and test` | 689 C# tests across 12 suites |
| `Studio tests and typecheck` | 28 TypeScript tests, plus the bundle builds |
| `Schema gates` | Sample schemas validate; the AI skill bundle regenerates and its golden examples validate |
| `Runtime smoke test` | Two scaffolded apps boot and are driven over HTTP with real JWTs: **authentication, declared roles and row-level ownership**, the CRUD contract (create, read, update, delete, filter, validate, optimistic concurrency, restart) and **tenant isolation** |

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

## 4. Coverage and what covering it found

Every module has tests:

| Suite | Tests |
| ----- | ----: |
| `foundry-schema` | 173 |
| `foundry-integration-tests` | 75 |
| `foundry-rules` | 73 |
| `foundry-file-io` | 63 |
| `foundry-core` | 52 |
| `foundry-kafka` | 34 |
| `foundry-mongo` | 61 |
| `foundry-connectors` | 31 |
| `foundry-studio` | 28 |
| `foundry-realtime` | 26 |
| `foundry-testing` | 26 |
| `foundry-api` | 54 |
| `foundry-cli` | 21 |

**Nine modules went from zero tests to a suite each, and seven of the nine yielded five defects.** The
count did not decline as the work went on, which is the strongest available evidence that the pattern
was systemic rather than local. Around fifty defects were fixed in total: three repository-level, six in
the scaffold-to-running-application path, roughly thirty-six across the nine modules, and the rest in
the workflow orchestrator and the Studio duplications.

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

Stated plainly, because a document that only lists wins is not useful for planning.

**Coverage is a floor, not a finish.** The suites target each module's highest-consequence surface, not
its whole surface area. `ExecuteActionAsync`'s HTTP path, SOAP envelope parsing, and the Excel
end-to-end path are still uncovered.

**One mirrored implementation remains, deliberately.** `crudRouteFor` in Studio duplicates the
compiler's route derivation so the designer and playground can show a route without a request per
keystroke. It is pinned to the compiler by a test table shared with `ApiManifestGeneratorTests`, which is
a weaker guarantee than a single implementation. It is the only copy left, and it is a trade rather than
an oversight.

**A flake was mitigated, not diagnosed.** Two `FoundryMongo.Tests` audit tests failed once in a
solution-wide run and were **never reproduced** across ~27 subsequent runs (isolated, under CPU load,
and solution-wide). The suites that share process-global state now run serially and the MongoDB
serialization configuration is registered from a module initializer, which removes a real class of
nondeterminism — xUnit runs classes in parallel, MongoDB freezes a class map on first use, and
NSubstitute queues matchers per thread. This suite had already produced one confirmed bug of that shape.
But the specific failure remains unexplained: the original assertion message was not captured, and
several candidate mechanisms were eliminated by reading the code. **If it recurs, capture the assertion
message before anything else.**

**Workflow transitions are not reachable from a scaffolded application, so the smoke test cannot cover
them.** This was meant to be part of extending it, and the attempt found the reason it could not be:
`ApiManifestGenerator` emits `Namespace`, `Endpoints` and `CustomEndpoints` and **never emits
`Workflows`**, though both the IR and `ApiManifest` carry them. `ApiManifestWorkflowDefinitionProvider`
therefore always finds an empty list. The scaffolder also never calls `AddFoundryWorkflows`, and no HTTP
route triggers a transition. The orchestrator itself is well covered by unit tests — but a workflow
declared in a schema does not reach a running application at all. That is a feature gap, not a test gap,
and it is now the clearest one.

**The Kafka outbox is still unexercised end to end.** The CI job provisions MongoDB only, so a real
broker round trip has nowhere to run. Worth doing as a separate job rather than by weakening the
assertion to something a missing broker would still pass.

**A multi-tenant write with no tenant returns 500.** Deliberate — an application that declares
multi-tenant entities and cannot resolve a tenant is misconfigured, and refusing is much better than
writing a row that belongs to nobody. The caller is authenticated and their token simply carries no
tenant, which is a deployment mistake rather than a bad request. The smoke test asserts the exact status
so a later decision to make it a 4xx has to be made deliberately.

**`TenantContextMiddleware` still falls back to the `X-Tenant-ID` header and a query parameter.** The
signed claim now wins whenever the caller is authenticated, which closes the override. The fallback
remains for callers a token cannot describe — a gateway that has already terminated authentication, a
background job, local development — and in those deployments the tenant is caller-asserted. Issue
tenants in the token where clients reach the service directly.

**No token issuance, and no refresh or revocation story.** The framework validates tokens; it does not
mint them. That is the right split — an identity provider's job is not a code generator's — but a team
adopting this still has to stand one up, and the scaffolded project does not say so beyond the
configuration it leaves empty.

**Ownership is one relationship, not an authorization language.** `ownerScoped` answers "this row
belongs to one caller", which covers the common case and nothing beyond it. Sharing, delegation,
team or hierarchy scoping, and field-level restrictions all have nowhere to be expressed. A schema
needing those still writes them by hand in a business rule, where nothing checks that the rule was
applied to every path.

**Exempt roles are per entity, not per operation.** A `Supervisor` exempted from the owner filter on
an entity is exempted for reads, updates and deletes alike. Read-only oversight — the common shape
for an auditor — cannot be expressed, and combining `ownerExemptRoles` with `apiRoles` on DELETE is
the closest approximation.

**Scaffolded projects pull in `MessagePack` 2.5.187, which carries known high-severity advisories**
(transitively, via SignalR). The framework's own dependencies are clean; the generated application's are
not, and a scaffolded project prints those warnings on its first build.

**MongoDB is still the only data provider.** That is the commercial ceiling for enterprise .NET shops.

---

## 6. Recommended priority for the next cycle

The catch-site audit, the smoke test extension, endpoint authorization and row-level ownership are all
done. None of the last two was on the list before this cycle — they were found by asking whether the
framework was production-ready and then checking rather than answering from memory, which remains the
more useful lesson than any item below.

1. **Make workflows reach a running application.** Emit `Workflows` into `api-manifest.json`, have the
   scaffolder register `AddFoundryWorkflows`, and expose a route that triggers a transition. Today a
   workflow declared in a schema is compiled, validated and then goes nowhere — the same shape as the
   defect class in section 2, at feature scale. Once it runs, the smoke test can cover it in a few lines.
2. **Deepen coverage on the paths that talk to the outside world** — the connectors' HTTP and SOAP
   paths, the workflow engine's `ExecuteActionAsync`, the Excel import end to end. These are where
   untrusted input meets the framework and where the remaining silent failures most likely live.
3. **Add a Kafka job to CI** and take the outbox round trip end to end.
4. **Remove the last mirrored implementation** by having the designer and playground read routes from a
   cached manifest rather than deriving them.
5. **Then** a second data provider. The repository abstraction exists, so it is plausible rather than a
   rewrite — but it doubles the surface, and it should follow the verification work rather than precede
   it.

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

Two general lessons, both cheap and neither followed here until late:

1. **A guarantee is only as good as the request that tests it.** Both of these were verified by
   assertion-in-documentation, and both survived years of green builds.
2. **Ask the blunt question and then check.** "Is this production-ready?" was answered by reading the
   code rather than by recalling the design — which is the only reason the authorization gap was found
   at all.

What has changed is not that the code is now correct, but that it can now tell you when it is not. That
is the prerequisite for everything else.
