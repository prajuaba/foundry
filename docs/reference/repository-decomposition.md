# Decomposing `Repository<T>`

A four-step plan, one step per commit, each CI-green before the next. Steps 1, 2 and 3 are done;
step 4 is not.

**The rule for every step: a pure move first, a behaviour change never in the same commit.** This is
the class where a mistake is a tenant-isolation failure, and its access-policy cluster has already
needed three separate security passes. A structural change combined with a semantic one in the same
diff is how the fourth gets in.

---

## Where it stands

**Steps 1, 2 and 3 are done.** The access policy, the search translator and the write guard are real
collaborators, each with tests that go at the thing they guard rather than at a repository that
happens to reach it.

| | Lines |
| :--- | ---: |
| `Repository.cs` | 1,748 |
| `EntityAccessPolicy.cs` | 593 |
| `EntitySearchTranslator.cs` | 263 |
| `EntityWriteGuard.cs` | 199 |

`Repository.cs` was 2,563 lines before step 1, 2,027 after it, 1,839 after step 2, and is 1,748 now.

**Step 1.** Stage one moved the cluster into a `partial class` file; stage two turned it into
`EntityAccessPolicy<T>`, constructed as `(ITenantContext?, ICurrentUserContext?)`, with
`Repository<T>` delegating at 39 call sites. `Repository.AccessPolicy.cs` is gone and `Repository<T>`
is no longer `partial`. Five unit tests cover the policy in **~61 ms with no MongoDB**, which was the
point: this cluster produced three separate security defects and every one had to be caught through a
live database.

**Step 2.** `EntitySearchTranslator<T>`, constructed as `(EntityAccessPolicy<T>)` — that was the
whole dependency, and measuring it first is what made the constructor obvious. 14 call sites moved,
6 of which became delegation. Six tests cover it in **~0.7 s**.

**Step 3.** `EntityWriteGuard<T>`, constructed as
`(IMongoCollection<T>, EntityAccessPolicy<T>, ITenantContext?)` — three dependencies, because this
seam really does reach the database. 17 call sites moved. Eight tests cover it in **~0.8 s**.

Every stage was verified as a pure move by comparing every non-comment, non-blank line before and
after — nothing lost, the only additions being the new type's scaffolding and the delegation.

**Measure before you trust this table.** Before step 1 stage two it said 2,035/530 and 36 call sites;
the truth was 2,034/573 and 39. Before step 2 it was accurate — 2,027 and 593, both confirmed. Before
step 3 it was accurate again — 1,839/593/263, all three confirmed — so the warning has now cost two
`wc -l` runs and bought nothing twice. Keep it anyway. The figures are correct exactly until a commit
lands without updating them, and nothing about reading the table tells you which of those two states
you are in. Measure, report what you measured, and correct the table when you are done.

## Step 1, stage two — `EntityAccessPolicy<T>` — DONE

The seam is cleaner than it looks. **The moved cluster touches exactly two pieces of private state:**
`_tenantContext` (9 references) and `_userContext` (7). No `_collection`, no database, no session.
That is the whole constructor:

```csharp
internal sealed class EntityAccessPolicy<T> where T : class, IEntity<ObjectId>
{
    public EntityAccessPolicy(ITenantContext? tenantContext, ICurrentUserContext? userContext);
}
```

Which is why this is worth doing: **every member below is a pure function of those two contexts and
the entity type**, so all of it becomes unit-testable without MongoDB. Today it can only be tested
through a live database, and that is not incidental — the three defects found in this cluster were
each caught by a different kind of test, and none by the obvious one.

### Members to move

`ApplyReadFilters` (both overloads) · `ScopeToOwner` · `ScopeToTenant` · `ApplyIsolationTo` ·
`TryGetOwnerScope` · `TryGetOwnerScopeFor` · `ElementName` · `StampOwner` ·
`EnsureCriteriaAreFilterable` · `MaxCriteriaCount` · `CurrentOwnerId` · `CallerIdentities` ·
`HoldsAnyRole` · `IsOwnerExempt` · `IsOwnerReadExempt` · and the statics `OwnerExemptRoles`,
`OwnerReadExemptRoles`, `IsOwnerScoped`, `IsShareable`.

### Two that stayed behind, deliberately

`CallerMaySeeRecordAsync` and `ShouldMask` are still in `Repository.cs`, outside the moved ranges.
`CallerMaySeeRecordAsync` queries the collection, so it belongs to the repository and should *call*
the policy. `ShouldMask` is used by masking as well as by criteria entitlement — move it, and have
both call it, so the two cannot drift. That drift is exactly the shape of the filter-oracle defect.

### Sequence

1. Change `private` to `internal`/`public` on the members, make the policy a real class, and have
   `Repository<T>` hold one and delegate. **36 call sites** in `Repository.cs`.
2. Build, run `bash scripts/run-tests.sh`, confirm 1,074 green. Commit.
3. Only then add the database-free unit tests — as a separate commit, so a red is unambiguous.

### What the new tests should assert

Not that a filter was *built*. `CrossCollectionSearchAsync_BuildsCorrectPipelineDefinition` asserted
stage shape, passed for years, and was corrected twice for naming defects that made it match nothing —
a test that reconstructs the code's own assumptions can only confirm them. Assert instead:

- Both `ApplyReadFilters` overloads produce equivalent predicates for the same inputs. They drifted
  once and the expression overload was missing the tenant filter for as long as it existed.
- An owner-exempt role widens within a tenant and never across one.
- A read-exempt role reads past the owner filter and still cannot write past it.
- `ElementName` returns the stored element, not the property name.
- `EnsureCriteriaAreFilterable` refuses a sensitive property and permits it under the unmasking scope.

---

## Steps 2–4

| Step | Extract | Why |
| :--- | :--- | :--- |
| 2 | `EntitySearchTranslator<T>` — see below — DONE | The camelCase class of defect lives entirely here, and it has produced two bugs already |
| 3 | `EntityWriteGuard<T>` — see below — DONE | Write-side invariants, which were interleaved with read-side ones |
| 4 | — | What remains is a real repository: collection access, paging, delegation |

---

## Why this is worth the care

Three security passes over this one cluster, and each found the same underlying problem: **one rule,
spread through a 2,500-line file, where nobody could see all of it at once to notice a copy was
missing.** The two `ApplyReadFilters` overloads drifted. Three read paths composed no isolation at
all. Criteria filtering was never entitled.

None of those was a hard problem once seen. Seeing them was the hard part, and that is a property of
the file's shape rather than of anyone's attention.

---

## Step 2 — `EntitySearchTranslator<T>` — DONE

Same shape as step 1: a pure move first, then the collaborator, then tests — separate commits.

**Members moved:** `BuildBsonFilter`, `BuildExpression`, `ConvertToBsonValue`, `BuildBsonArray`,
`EscapeRegex`, `BuildSortDefinition`, and the cross-collection pipeline assembly inside
`CrossCollectionSearchAsync`, which became `BuildCrossCollectionPipeline`.

**What the dependency turned out to be.** One thing: the access policy.

```csharp
internal sealed class EntitySearchTranslator<T> where T : class, IEntity<ObjectId>
{
    public EntitySearchTranslator(EntityAccessPolicy<T> accessPolicy);
}
```

The moved cluster touches `_accessPolicy` three times — `ApplyIsolationTo` twice while assembling the
pipeline, `EnsureCriteriaAreFilterable` once before compiling an expression — and no other private
state. No collection, no session, no database, no contexts. `ElementName` needed nothing new: it is
`static` and takes the entity type already, so the call crossed the boundary unchanged.

Two things stayed in `CrossCollectionSearchAsync`: resolving entity types to collection names, and
running the pipeline. The translator builds; the repository executes. `pageNumber` and `pageSize` are
now read a few lines earlier in that method because both sides need them — the only reordering in the
move, and both are property reads on a record.

**Why this one earns its own type.** Two defects have come out of it, both the same root cause and
both silent: a hand-built `BsonDocument` does not resolve names through the class map, so a
PascalCase field name matches nothing and errors nowhere. The soft-delete predicate in
cross-collection search never excluded a row; the criteria never matched a field. A filter that
matches nothing returns nothing, and for a *search* "no results" is a plausible answer every time.

**What the tests assert.** That a criterion **matches a row**, not that a stage was built.
`CrossCollectionSearchAsync_BuildsCorrectPipelineDefinition` asserted stage shape, passed for years,
and has been corrected twice for exactly these two defects — it checks the document the code builds
and never the one MongoDB matches against.

So `EntitySearchTranslatorTests` seeds rows, runs the translator's own pipeline, and asserts on which
rows come back. Five of the six use MongoDB deliberately: it is the oracle for "does this match", the
one question a hand-written expectation cannot answer, and the reason the shape test could be wrong
for years. That is a different argument from step 1's, where the whole point was to get *away* from a
database — the policy is a pure function and could be asked directly; a `BsonDocument` filter cannot
be evaluated without something that evaluates `BsonDocument` filters. The sixth, on criteria
entitlement, needs no database and does not open one.

Both defects were reintroduced to confirm the tests catch them. Writing the criterion field as the
property name failed four tests; writing `match["IsDeleted"]` failed
`ASoftDeletedRowIsNotMatched` with `["kept", "removed"]` against an expected `["kept"]` — the
soft-deleted row coming back, which is the original bug exactly and in the dangerous direction: a
filter that silently admits rows rather than one that silently drops them.

---

## Step 3 — `EntityWriteGuard<T>` — DONE

Same shape as steps 1 and 2: a pure move first, then tests — separate commits.

**Members moved:** `StampTenant`, the optimistic-concurrency filter (3 scoped copies, now `OccFilter`),
the bulk version filter (2 unscoped copies, now `UnscopedOccFilter`), the `oldValues["Version"]` read
(3 copies, now `StoredVersion`), the zero-match conflict-or-not-found block (3 copies, now
`ThrowOnConcurrencyConflictAsync`), and the bulk shortfall check (2 copies, now
`ThrowOnBulkConcurrencyConflict`). **17 call sites.**

**Was the spec's description of this step right?** The warning above it was — this seam is messier
than the first two — but the shape it predicted came out clean, and one prediction was wrong in a
useful direction.

**What the dependency turned out to be.** Three things, and the measurement is why the constructor
looks like this rather than like the previous two:

```csharp
internal sealed class EntityWriteGuard<T> where T : class, IEntity<ObjectId>
{
    public EntityWriteGuard(
        IMongoCollection<T> collection,
        EntityAccessPolicy<T> accessPolicy,
        ITenantContext? tenantContext);
}
```

- `_tenantContext` — **2 references, both inside `StampTenant`.** So the field left `Repository<T>`
  entirely; the constructor parameter is now forwarded to two collaborators and stored in neither.
  This is the one the spec did not predict, and it is the same finding as step 1's: measuring first
  turned a guess into a deletion.
- `_accessPolicy` — 2 references, composing tenant and owner isolation into the version check.
- `_collection` — 2 references, both `CountDocumentsAsync`. This is the dependency that makes step 3
  different from steps 1 and 2, and it is irreducible: a replace that matched nothing cannot say
  whether the row moved on or was never there, so something has to ask the database.

The session handle is *not* a dependency. It is a per-call argument at every site, because it belongs
to the caller's transaction rather than to the guard.

**Where the seam was genuinely messy, and what was not forced.** The write-side invariants are
interleaved with encryption, soft-delete stamping, revision history and audit, in different orders at
each of the five write paths. A single `GuardedReplaceAsync` swallowing read-modify-write would have
been a rewrite, not a move, so it was not attempted. What moved is the five invariants; the
orchestration stayed. That is the smaller, honest extraction, and it is the right one — the file is
91 lines shorter rather than the 200 a fused method would have bought, and every one of those 91
lines was a duplicated copy of a rule.

**The asymmetry the extraction exposed.** The three single-document paths scope their version check
to tenant and owner; the two bulk paths do not, and never have. Nobody could see that before, because
the two constructions were three hundred lines apart. It is preserved exactly — hence two
differently named members — because a pure move is a pure move. It is reachable only by a concurrent
write that changed a row's tenant without bumping its version, and every write path does both
together, so it is missing depth rather than an open door. **Step 4 should decide about it
deliberately.** `TheBulkFilterIsNotTenantScoped` is a characterization test recording today's
behaviour, so closing the gap is a visible change to a stated expectation rather than a silent one.

**Why MongoDB is the oracle here, and where it is not.** Optimistic concurrency is about two writers
racing, and a broken version check shows up as a *lost update* — the loser's value silently replacing
the winner's. That is a fact about persisted state under concurrency, and no in-process double has
persisted state. A faked `IMongoCollection` returns whatever `MatchedCount` the test programmed into
it, which tests the fake. So the six race tests stage a real race and assert on **what is stored
afterwards**, not on the shape of the filter — the same reason step 2 used a database, arrived at
from a different question. The two tenant-stamping tests are the opposite case: `StampTenant` is a
pure function of the ambient context, so they ask it directly and open no database, which is step 1's
argument exactly.

`AStaleWriteIsRefusedAndTheWinnersValueSurvives` asserts the surviving balance **before** the
exception, deliberately. Asserted the other way round it fails with "no exception was thrown", which
describes the mechanism; asserted this way it fails with `Expected: 200, Actual: 999`, which is the
lost update itself.

**Both defects were reintroduced to confirm the tests catch them.** Dropping the version predicate
from `OccFilter` failed two tests, `AStaleWriteIsRefusedAndTheWinnersValueSurvives` with
`Expected: 200, Actual: 999` — the loser's write landing on top of the winner's, which is the entire
point of the class. Making `StampTenant` honour a caller-supplied tenant failed two others,
`TheAmbientTenantOverwritesWhateverTheCallerSent` with `Expected: acme, Actual: globex` — a
cross-tenant write, which is the original defect exactly.
