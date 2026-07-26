# Engineering Assessment — 2026-07-26

An honest read of where Foundry actually stands, written after a full working session
*inside* the codebase rather than from reading it. It is deliberately blunt: the point of
this document is to be useful for planning the next cycle, not to market the project.

**Summary:** strong architecture, demo-grade implementation, hardened only where it has
been touched. The single most important finding is not any individual bug — it is that the
codebase has a consistent disposition toward *silent failure*, which is the worst possible
default for a code generator.

---

## 1. Where it genuinely stands

The runtime layer — `foundry-mongo`, roughly 11.5k lines covering tenant filter injection,
envelope encryption, optimistic concurrency, seek pagination and hot/cold partitioning — is
real senior-level work and held up under inspection.

The AI thesis is now **validated rather than aspirational**. The local model never writes
C#; it writes IR (the domain schema), and the compiler writes C#. Measured with
`qwen3-coder:30b`:

| Band | Cases | Domain accuracy | Schema-valid IR |
| ---- | ----- | --------------- | --------------- |
| Core | 30    | 100%            | 100%            |
| Hard | 10    | 40%             | 100%            |

The load-bearing number is the last column. At 40% hard-domain accuracy the model's
*judgement* is degrading while output *validity* never does — which is precisely the
behaviour the design predicts. Best-practice output is guaranteed by construction, because
the model does not author the layer where a missing tenant filter or an N+1 query would live.

An awkward consequence worth stating plainly: **the AI layer is currently the most mature
part of the system.** It generates onto a runtime that is less verified than it is.

---

## 2. The systemic defect pattern

Nearly every bug found during the session had the same shape: **success reported, nothing done.**

- The compiler printed `Success` having emitted zero files.
- An index was declared in the schema and never created.
- Studio config was emitted and silently dropped by the compiler.
- An endpoint was declared and silently skipped.
- A client-supplied id was accepted, then silently replaced by the driver.
- An enum was declared and silently never used.
- `foundry api` slept 100 ms and returned success.

This is a design disposition, not bad luck. The codebase consistently prefers *carry on*
over *fail loudly*. For a code generator that is the most damaging default available: the
output looks plausible, passes review, and the failure surfaces in production.

**Treat "returns success without doing the thing" as a bug class, not a list of individual
defects.** Fixes should change the default, not patch instances.

A corollary discovered the hard way: the generated code had apparently **never been built
and run**. The generator emitted calls to `AddAsync` and `FindAsync`, neither of which
exists on its own repository interface. Nothing caught it, because no test had ever
compiled the output. `GeneratedCodeCompilesTests` now does, and found four bugs in under an
hour by exercising reality instead of asserting on strings.

### The clearest example: the repository itself could not be built

Found the same day this document was first written, and worth recording because it is the
pattern operating at the largest possible scale.

The root repository recorded its seven sibling modules as **gitlinks** (mode `160000`) with
no `.gitmodules` file. Git therefore had no path-to-URL mapping for them. The consequences:

- `git clone` produced seven **empty** directories.
- `git submodule update --init` failed outright — *"No url found for submodule path"*.
- `dotnet restore` could not find ~15 of the projects referenced by `Foundry.slnx`.
- **CI had never passed. Not once, on any commit, since the workflow was added.**

Every `git push` reported success. The local working tree built and tested clean, because
the seven directories happened to be populated on that one machine. Nothing anywhere
reported a problem, and the published repository was unusable by every person and system
other than its author.

It was found by cloning the repo from GitHub and building *that*, rather than reasoning
about it — which immediately exposed a second hidden dependency underneath: `Foundry.Cli`
embedded `foundry-studio/dist/index.html`, a correctly-gitignored build artifact, so a
clean clone failed with CS1566 until someone ran an undocumented `npm run build`. And
underneath *that*, a third: `dotnet test Foundry.slnx` tried to run the `Foundry.Testing`
helper library as a suite and exited 1 while all 258 tests passed — invisible for as long
as the four suites were run individually rather than solution-wide.

Three defects, stacked, each masked by the one above it. The generalisable lessons:

1. **Verify against a fresh clone, not your working tree.** A working tree accumulates
   state that the repository does not contain.
2. **Run the command CI runs.** "All suites green" and `dotnet test Foundry.slnx` were not
   the same statement, and only one of them was the one that mattered.

---

## 3. Coverage reality

Eleven modules have tests, plus the integration suite:

| Has tests | No tests at all |
| --------- | --------------- |
| `foundry-schema` (131) · `foundry-integration-tests` (75) · `foundry-file-io` (63) · `foundry-core` (52) · `foundry-rules` (49) · `foundry-kafka` (34) · `foundry-mongo` (29) · `foundry-connectors` (28) · `foundry-realtime` (26) · `foundry-api` (23) · `foundry-testing` (26) · `foundry-cli` (21) | `foundry-studio` |

The still-untested list includes ~7.5k lines of Studio TypeScript verified only by `tsc`.

**The prediction held in seven of the eight modules covered on 2026-07-26**, and the defect count per
module has not declined:

- `foundry-rules` — **five**, four in guard-condition evaluation, each failing by *silently
  blocking a transition* rather than erroring.
- `foundry-core` — **five**: three in pagination metadata (a page count saturating to
  `long.MaxValue` on a zero page size, an untrimmed seek sentinel returning `PageSize + 1`
  items, and `Map` dropping the cursor so a mapped result reported itself as the last page) and
  two in the audit trail (an insert rendered as `(no change → <String>)`, misdescribing the
  change *and* printing the type instead of the value).
- `foundry-kafka` — **five**, and these are the most serious found all day, because they
  silently break the two guarantees the transactional outbox exists to provide. Ordering was
  broken at both ends: a fresh `Guid` partition key per message spread one entity's mutations
  across partitions, and the publisher requested a sort order it never received because
  `FindManyAsync` ignores `sortOrder` when `sortBy` is null. Durability defaulted to
  `Acks=Leader`, which acknowledges before replication — so a broker failure loses a message
  the outbox has already marked processed. Plus an unvalidated `int`-to-`Acks` cast and a topic
  name that could degrade to `-events` and publish there successfully.

- `foundry-realtime` — **four**, and the first one is the most serious defect found anywhere in
  the codebase. Every mutation was sent to SignalR's `Clients.All` *in addition* to the
  subscription groups, so any connected client received every entity's changes — bypassing the
  `[RealTime(roles: ...)]` RBAC that `NotificationHub` carefully validates on subscribe, and, in
  a multi-tenant deployment, delivering one tenant's mutations to another tenant's clients. An
  `AuditLogEntry` carries `PropertyDiffs`, so what leaked was the changed *values*, not merely
  metadata. Alongside it: entity subscription groups never matched (subscribers joined under the
  simple type name, delivery targeted the assembly-qualified one) which the firehose masked;
  record subscriptions were authorised against a client-supplied entity name but keyed on the
  record id alone, so a caller could name an entity they may read and then join the record group
  of one they may not; and an unresolvable entity name failed *open*.

- `foundry-connectors` — **five**. The registration helpers keyed `ConnectorOptions` and the typed
  `HttpClient` on the *type* rather than the connector name, so a second connector of the same type
  replaced the first: an application integrating a CRM and a billing provider ended up with both
  pointing at one base URL carrying one set of credentials — sending one service's API key to the
  other's endpoint, in a perfectly well-formed request. Five of six new registration tests fail
  against the original code; the one that passes is the single-connector demo case. Also: the HTTP
  verb was chosen with `payload.Equals(default(TRequest))`, so a legitimate `0` or `false` was
  mistaken for "no payload" and silently downgraded to a GET with the body dropped;
  `EnsureSuccessStatusCode` discarded the remote body in all three connectors, which for SOAP means
  throwing away the entire fault detail; GraphQL returned `null` and dropped the `errors` array on
  the protocol's *normal* failure mode (HTTP 200 plus errors), making a rejected query
  indistinguishable from one that matched nothing; and GraphQL bound responses with case-sensitive
  defaults, so conventional camelCase fields silently produced an all-defaults object.

- `foundry-file-io` — **five**, two of them security defects in the component whose stated purpose is
  security. `SanitizeFileName` is documented as eliminating path traversal, and returned `".."`
  unchanged: `Path.GetFileName("..")` is `".."` and `'.'` is not an invalid filename character, so
  `Path.Combine(uploadDirectory, sanitised)` resolved to the parent directory. Separately, the CSV
  exporter was open to **formula injection** — a field beginning with `=`, `+`, `@` or `-` is
  evaluated when the export is opened in Excel or Sheets, so a value typed into a name field
  executes in the session of whoever opens it. Both produce valid, successful operations. Also: the
  Excel parser caught every conversion failure and left the property at its default, so a cell of
  `"1,234.00 USD"` imported as an amount of **zero** while the row count matched what the user
  expected — data corruption presented as a clean import, now loud by default with an opt-in lenient
  mode that reports what it dropped; enum columns could never convert at all; and an empty upload
  threw CsvHelper's "No header record was found" for what is an ordinary user mistake.

- `foundry-cli` — **one**, and the low count is itself informative: this is the module worked on
  most heavily earlier in the day, so most of its defects had already been found and fixed. The one
  remaining was bare `foundry` printing help and exiting **0**, which makes `foundry $COMMAND` with
  an unset variable do nothing and report success. Its 21 tests drive the built binary as a process,
  because the CLI's contract is its exit code and its stdio: CI treats a non-zero exit as a failed
  gate, and the VS Code extension speaks LSP over stdin/stdout. That also means the earlier LSP
  byte-framing fix is now **verified end to end** — a request containing `café-naïve-piñata-日本語`
  followed by a second request proves the stream does not desynchronise, which inspection alone
  could not establish.

- `foundry-testing` — **five**, and the first is the purest expression of the pattern in the whole
  codebase: **the test report reported success unconditionally.** Both the HTML and Markdown
  generators embedded a fixed seven-row "Protocol Coverage Matrix" in which every row read `PASSED`,
  next to strings like "100% Endpoint Coverage", "Zero Breach" and "KRaft Verified" — none measured,
  none affected by the results passed in. A run with fifty failures produced a clean bill of health.
  A zero-test run additionally rendered a "100.0%" pass rate. And one level up, `foundry test` fed it
  `generatedTests.Count * 2` as both the total *and* the passed count with zero failures and a
  hardcoded 0.45s duration — for suites it had generated and never executed. Per-protocol status is
  now rendered only when supplied; a zero-test run reads "INCONCLUSIVE"; and the command states
  plainly that it generated rather than ran. Also in the mock-data generator: keys were 32-character
  Guids where an ObjectId is 24, so every generated test that posted or fetched by id was broken
  before it ran; `MaxLength` and `Range` constraints were ignored, so fixtures violated the schema
  they came from and the API's own validation rejected them; and a shared non-thread-safe `Random`
  could degrade to returning zeros under concurrent generation.

Four areas came back clean, which is worth recording as evidence rather than left unsaid:
**ambient tenant propagation** held under 50 interleaved flows, the **serialization defaults**
were correct on every property including the deliberately-writable OCC token, `AddFoundryRules`
produced a valid container under `ValidateOnBuild`/`ValidateScopes`, and the **real-time audit
sink** correctly honours `[RealTime(false)]` while still writing audit records. Tenant isolation
is the highest-consequence surface in the framework, so a negative result there is a real finding.

**One unresolved flake.** Two tests in `FoundryMongo.Tests.RepositoryAuditTests` failed once
during a solution-wide run and could not be reproduced in eight subsequent runs (five isolated,
one paired with the integration suite, two solution-wide). Both use mocks rather than the real
database, so contention is not the obvious explanation; the leading hypothesis is order
dependence on global MongoDB driver state, which this suite has exhibited before. It is recorded
rather than closed — a suite that fails one run in ten undermines every other claim in this
document, and it must be reproduced before it is called fixed.

One module remains, and nothing yet contradicts the assumption that they carry comparable
defect density.

All 557 tests pass and there are zero vulnerable NuGet packages. CI
(`.github/workflows/ci.yml`) runs build + test against a MongoDB service, schema gates, and
a Studio typecheck; it **first went green on `bf3e227`**, after the three repository-level
defects in section 2 were fixed. The seven modules are now vendored into the root
repository, so a clone builds.

---

## 4. Recommended priority for the next cycle

Verification, not features. In order:

1. ~~**Prove a generated application actually runs.**~~ **Done 2026-07-26.**
   `scripts/runtime-smoke-test.sh` scaffolds a project with `foundry new`, boots it, exercises
   the generated REST endpoints, restarts the process and reads the record back from MongoDB.
   It runs as its own CI job. Getting there took six fixes: the scaffolder emitted absolute
   paths to one machine, produced no `api-manifest.json` (so no routes at all), the endpoint
   generator failed to compile with more than one entity, `AddFoundryRealTime` deadlocked
   startup on a circular DI registration, and `required Id` made every `POST` fail model
   binding — meaning creating an entity over HTTP had never worked.
2. **Continue module coverage.** `foundry-rules` and `foundry-core` are done; seven remain.
   Next most valuable: `foundry-kafka` (the outbox is a correctness-critical differentiator)
   and `foundry-mongo`'s untested paths.
3. **Audit the ~54 catch sites** for swallowed failures and flip the default to failing loudly.
4. **Any test at all for Studio.** One end-to-end pass — draw entity → export IR → compile —
   covers the path users actually take.
5. **Then** a second data provider beyond MongoDB. It is the commercial ceiling for
   enterprise .NET shops, but adding one to an unverified runtime just doubles the
   unverified surface.

---

## 5. Strategic read

The differentiation is real and defensible: local-model-first so the domain model never
leaves the building, IR-not-C# so generated code cannot drift from best practice, and
tenant isolation plus envelope encryption at the data layer. That aims squarely at
regulated .NET shops which OutSystems prices out and Retool cannot serve. Nothing found
during this session undermines the thesis.

The risk is execution, not strategy. The gap between what the README claimed and the actual
state of the system was wide. And the fastest way to lose a regulated-industry buyer is a
silent failure in tenant isolation or audit — which is exactly this codebase's default
disposition, and exactly what section 4 is ordered to fix.
