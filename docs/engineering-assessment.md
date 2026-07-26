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

Three modules have tests, plus the integration suite:

| Has tests | No tests at all |
| --------- | --------------- |
| `foundry-schema` (131) · `foundry-integration-tests` (75) · `foundry-core` (52) · `foundry-rules` (49) · `foundry-mongo` (29) · `foundry-api` (23) | `foundry-kafka` · `foundry-realtime` · `foundry-connectors` · `foundry-file-io` · `foundry-testing` · `foundry-cli` · `foundry-studio` |

The still-untested list includes the **Kafka outbox** and ~7.5k lines of Studio TypeScript
verified only by `tsc`.

**The prediction held in both modules covered on 2026-07-26.** `foundry-rules` yielded five
defects, four in guard-condition evaluation, each failing by *silently blocking a transition*.
`foundry-core` yielded five more: three in pagination metadata (a page count that saturated to
`long.MaxValue` on a zero page size, an untrimmed seek sentinel returning `PageSize + 1` items,
and `Map` dropping the cursor so a mapped result reported itself as the last page) and two in the
audit trail (an insert rendered as `(no change → <String>)` — misdescribing the change *and*
printing the type instead of the value).

Two areas came back clean, which is worth recording as evidence rather than left unsaid:
**ambient tenant propagation** held under interleaved concurrency, and `AddFoundryRules`
produced a valid container under `ValidateOnBuild`/`ValidateScopes`. Tenant isolation is the
highest-consequence surface in the framework, so a negative result there is a real finding.

Seven modules remain, and nothing yet contradicts the assumption that they carry comparable
defect density.

All 359 tests pass and there are zero vulnerable NuGet packages. CI
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
