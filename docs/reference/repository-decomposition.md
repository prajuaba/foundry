# Decomposing `Repository<T>`

**This plan is finished. All four steps are done, and step 4 concluded that no fifth extraction is
warranted — see "Where it stands" and "Step 4" below before reopening it.** Two follow-ups came out
of the work and are recorded at the end: one dead collaborator still to wire up, and one
write-authorization defect, since fixed on the recommendation below.

A four-step plan, one step per commit, each CI-green before the next.

**The rule for every step: a pure move first, a behaviour change never in the same commit.** This is
the class where a mistake is a tenant-isolation failure, and its access-policy cluster has already
needed three separate security passes. A structural change combined with a semantic one in the same
diff is how the fourth gets in.

---

## Where it stands

**All four steps are done.** The access policy, the search translator and the write guard are real
collaborators, each with tests that go at the thing they guard rather than at a repository that
happens to reach it. Step 4 found no fourth thing worth extracting and did not invent one.

| | Lines | Owns |
| :--- | ---: | :--- |
| `Repository.cs` | 1,632 | Collection access, paging, and the order the steps happen in |
| `EntityAccessPolicy.cs` | 593 | What this caller may see and change |
| `EntitySearchTranslator.cs` | 263 | How a search request becomes a query |
| `EntityWriteGuard.cs` | 199 | What must be true for a write to land |
| `EntityVersioningService.cs` | 139 | Where a revision goes and how it is read back |

`Repository.cs` was 2,563 lines before step 1, 2,027 after it, 1,839 after step 2, 1,748 after step 3,
and is 1,632 now — 36% smaller, with every isolation rule it used to spell out inline now owned by
something that can be tested on its own.

**All figures in this table were measured at the close of step 4**, and the four that step 4 inherited
were all correct as written.

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

**Step 4.** No new type. `EntityVersioningService<T>` already existed and was dead; the repository now
delegates to it at 13 call sites, deleting twelve independent derivations of the history collection's
name. Ten round-trip tests cover it in **~1 s**. See below for why this was the honest step rather
than a fourth extraction.

Every stage was verified as a pure move by comparing every non-comment, non-blank line before and
after — nothing lost, the only additions being the new type's scaffolding and the delegation.

**Measure before you trust this table.** Before step 1 stage two it said 2,035/530 and 36 call sites;
the truth was 2,034/573 and 39. Before step 2 it was accurate — 2,027 and 593, both confirmed. Before
step 3, accurate again — 1,839/593/263. Before step 4, accurate a third time — 1,748/593/263/199, all
four confirmed — so the warning has now cost three `wc -l` runs and bought nothing three times.

**Keep it anyway, and note what step 4 actually caught.** The numbers were right; the *prose* was
wrong, in both of its predictions about what step 4 would find. A table that has been accurate three
times running is not evidence that the paragraph next to it is. Measure the claims too, not just the
figures — the figures are the cheap part.

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
| 4 | Nothing new — see below — DONE | The remainder *is* a real repository. What it did instead was wire up a collaborator that already existed and was dead |

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

---

## Step 4 — no fourth extraction — DONE

**The spec's own description of this step was written before any of the extractions, when nobody
knew what would be left, and it was wrong twice.** Both errors were found by measuring.

**Paging was the predicted candidate. There is nothing there.** It is already extracted:
`OffsetPaginationHelper` validates and computes skip/take, `SeekPaginationHelper` builds the seek
filter, and `EntitySearchTranslator<T>.BuildSortDefinition` builds the sort. What is left in
`GetPagedAsync` and `GetPagedItemsAsync` is the orchestration around those three — build the filter,
count, find, decrypt, audit, mask, shape the result — which is the repository's actual job. Wrapping
it would have produced a type that holds a collection and calls the same helpers in the same order,
which is a rename, not a decomposition.

**The other prediction — "what remains is a real repository" — was right about the code and wrong
about the file.** `EntityVersioningService<T>` was already there: 139 lines, fully written,
constructed in the repository's constructor, and never once called. `EntityAuditService<T>` is in the
same state. Someone had performed this decomposition once already, as services, and never wired the
result up. Nothing in the file said so, and the constructor reads as though both are in use.

Meanwhile `Repository.cs` spelled out the versioning service's contents at **twelve sites** — twelve
independent derivations of `CollectionName + "_History"`, ten revision writes, two reads. That is the
same shape as every defect this document exists because of: one rule, copied, where nobody can see
all the copies at once. Writer and reader agreed on where history lives by coincidence rather than by
construction.

**So the step was not to extract a fourth type. It was to delete twelve copies of a rule by calling
the type that already owned it.** Seven `SaveRevisionAsync`, three `SaveRevisionsAsync`, one
`GetRevisionsAsync`, two `GetRevisionByVersionAsync`.

A pure move, and an unusually clean one: the service was a character-for-character match for the code
it replaced — same filters, same sort, same `InsertOne`/`InsertMany` overloads, same `null` options
argument. Two differences, both accounted for. The two bulk sites dropped their
`if (revisions.Count > 0)` guard because `SaveRevisionsAsync` opens with the identical check, and
`BulkInsertAsync`'s copy was already unreachable behind its own empty-list early return. The service
reads `_database` where the repository read `_collection.Database`; the constructor sets `_collection`
from that same `db`, so they are the same object.

Also removed: `CloneEntity` and `GetUnifiedPropertyValue`, two private members with no callers
anywhere.

**Why the defect class needed a different oracle again.** History is a shadow collection, and the
failure is silent in the direction that hides it: a revision written where nothing reads makes
`GetRevisionsAsync` return an empty list, which is *also* the honest answer for an entity with no
history yet. Six of the ten write paths had no history coverage at all — both bulk inserts, both bulk
updates, the soft delete, the hard delete and the restore — and two more had only mock coverage.

`EntityVersioningServiceTests` is therefore ten round trips: do the operation, read the history back,
assert on what comes out. MongoDB is the oracle for a **third** distinct reason. Step 2 needed it
because a `BsonDocument` filter cannot be evaluated without something that evaluates filters; step 3
because a lost update is a fact about persisted state under two concurrent writers. Here the argument
is about the double itself: against a mock,
`GetCollection<EntityRevision>("Widgets_History")` is a name *the test supplies*, so a writer and
reader that both moved to the same wrong collection would still satisfy a suite pinned to that
literal. A fake has no opinion about which collection is which. A database does, and that is the
entire question.

The tests drive the service through `Repository<T>` rather than calling it directly, because a
revision never written and one written to the wrong place are indistinguishable from the reader's
side. Calling save-then-load on the service alone proves only that two functions agree with each
other.

**Three defects were reintroduced, and the failure modes turned out to be complementary** — which is
the useful part:

- Letting the writers resolve the shadow collection for themselves, as all twelve sites used to,
  failed **nine of ten**, every one with `Actual: []` — the original bug's signature exactly.
- Dropping the snapshot from the soft-delete path failed two, with
  `Expected: ["SoftDelete", "Insert"]  Actual: ["Insert"]`.
- Writing history unconditionally failed **only** `AnEntityThatDidNotAskForVersioningGetsNoHistory` —
  the one test the other two defects leave passing. That control is why the other nine mean
  "writes correctly" rather than "writes always".

One thing worth knowing that fell out of writing them: **a hard-deleted row's history is written but
cannot be read through the repository**, because `GetRevisionsAsync` gates on the live row still
being visible and a hard delete removes it. That is recorded in `AHardDeleteLeavesARetrievableRevision`,
which reads through the service for that reason. Not changed — just no longer invisible.

### Is the refactor finished?

**Yes.** `Repository.cs` is 1,632 lines and every one of them is collection access, paging, or
sequencing calls to collaborators. The remaining length is not a hidden rule; it is five write paths
that interleave encryption, soft-delete stamping, revision history and audit in genuinely different
orders. Step 3 already declined to fuse those into a `GuardedReplaceAsync` and was right to: that is
a rewrite, and the orchestration is the part that legitimately differs per operation.

**Do not reopen this to hit a line count.** The next reader's time is better spent on the two items
below, both of which are real. Follow-up 2's defect has since been fixed; what remains of it, and all
of follow-up 1, has not.

---

## Follow-up 1 — `EntityAuditService<T>` is dead too

Same finding as step 4's, not acted on, deliberately.

213 lines, constructed in `Repository<T>`'s constructor at
`_auditService = new EntityAuditService<T>(auditSink, userContext)`, never called. It has
`CapturePropertyValues`, `ComputeDiffs`, `BuildUpdateAuditEntry` and six `Audit*Async` methods, and
`Repository.cs` inlines all of it: five copies of the `oldValues` reflection capture, four copies of
the diff loop, and around eight audit-entry constructions.

**Why step 4 left it.** It is a near-match, not an exact one. `ComputeDiffs` reads
`EntityEncryptionService<T>.GetCachedProperties()` where the repository calls
`typeof(T).GetProperties(...)` fresh — the same call memoized, so semantically identical, but
*proving* that is a different exercise from reading two blocks side by side. More importantly the
`properties` local is shared between the capture and the diff loop in five methods, so wiring it up
restructures those methods rather than substituting a call into them.

And the coverage is thin: `RepositoryAuditTests` has three tests, over insert, update and soft delete.
Rewiring the audit trail at eight sites under three tests is the "silent success" risk in its purest
form — audit stops recording and nothing goes red. That deserves its own pure-move commit and its own
round-trip tests, on the model of step 4's.

---

## Follow-up 2 — the bulk write-scope gap, which is worse than the OCC asymmetry

**Step 3 asked step 4 to decide about `OccFilter` vs `UnscopedOccFilter` deliberately. Investigating
that turned up a larger and separate defect, which is confirmed and reachable with no race at all.**
Neither was changed by the refactor — both are behaviour decisions, not refactoring.

**The defect below is now fixed, on the recommendation this section ends with; the analysis is kept
because it is the reasoning behind where the fix went. The OCC asymmetry is still open, and is now
the cheap change this section predicted it would become.** See "What was done" at the end.

### The OCC asymmetry itself: not a defect, and not the thing to fix

The three single-document paths scope their version check to tenant and owner; the two bulk paths do
not. Both bulk callers reach `UnscopedOccFilter` with an id a scoped read just returned, so the
unscoped filter is applied to a row already established as the caller's. Reaching it requires a
concurrent write that changes a row's `TenantId` or `OwnerId` **without bumping `Version`**, landing
inside the window between that read and the `BulkWriteAsync`.

No path through `IRepository<T>` does that: every write that can change the tenant bumps the version
in the same operation. What would have to be true is a writer outside the repository — a migration, an
admin tool, or anything using the publicly exposed `Repository<T>.Collection` — that violates the
version-bump invariant, *plus* the race. Step 3's reading was right: **missing depth, not an open
door.**

### The actual defect: `BulkUpdateManyAsync` picks its rows with a read filter

`BulkUpdateManyAsync` selects rows with `_accessPolicy.ApplyReadFilters(filter)`, which calls
`TryGetOwnerScope(forWrite: false)`. That flag is exactly what lifts the owner filter for
`[OwnerReadExemptRoles]` holders and what adds `AnyIn("SharedWith", grantedTo)` for grantees. Nothing
downstream re-checks write scope — the OCC filter is unscoped, and unlike `BulkUpdateAsync` there is
no second scoped read to skip rows on.

So a caller who may **read** a row but not **write** it can modify it through `BulkUpdateManyAsync`.

This contradicts two contracts stated in this codebase's own source:

- `OwnerReadExemptRolesAttribute`: *"This lifts the owner filter on reads and leaves it in place on
  writes, so the holder sees the whole tenant and can still only modify their own rows."*
- `ISharedResource`: *"**A grant confers read access only.** Updates and deletes stay with the owner
  and whoever holds an exempt role... a grant that silently conferred write access would turn 'let my
  colleague see this' into 'let my colleague delete this'."*

**Both were confirmed empirically against a real database, not inferred.** An `[OwnerReadExemptRoles]`
auditor overwrote another user's row: `Expected: alice's private note, Actual: OVERWRITTEN BY AUDITOR`.
A grantee overwrote a row shared with them read-only:
`Expected: alice's shared doc, Actual: OVERWRITTEN BY GRANTEE`. The same callers doing the same thing
through `UpdateByObjectIdAsync` are correctly refused — so the bulk path is inconsistent with every
single-document path, and the divergence needs no concurrency to reach.

`BulkUpdateAsync` is **not** affected: it reads each row through
`ScopeToOwner(ScopeToTenant(...))` and `continue`s past anything that does not match.

### Recommendation

This is **a latent defect, plainly, not a deliberate asymmetry** — no design note anywhere claims
bulk writes should be broader than single-document ones, and two attributes state the opposite in
their own documentation.

Fix it in the row selection, not in the OCC filter. `BulkUpdateManyAsync` should apply the write-side
owner scope to the rows it selects, so a row the caller may not write is never loaded as a candidate.
**Scoping `UnscopedOccFilter` instead would be the wrong fix even though it happens to block the same
writes:** a zero-match replace routes into `ThrowOnBulkConcurrencyConflict`, so an authorization
failure would be reported to the caller as a concurrency conflict, and the retry that error invites
would fail forever.

Sequencing, on this document's usual rule: land the selection fix and its tests on their own, then
decide about `UnscopedOccFilter` separately. Once `BulkUpdateManyAsync` selects only writable rows,
both bulk paths are in the same position `BulkUpdateAsync` is in today, and closing the OCC gap
becomes a cheap defence-in-depth change rather than a behaviour change with an error-reporting
problem attached.

### What was done

`EntityAccessPolicy<T>.ApplyWriteFilters` is the write-side counterpart of the expression overload of
`ApplyReadFilters`: the same soft-delete and tenant predicates, with `TryGetOwnerScope(forWrite: true)`
instead of `false`. The two now share one body and differ only by that flag, so they cannot drift the
way the two read overloads once did. `BulkUpdateManyAsync` calls it, which is a one-line change at the
only site that needed one, and the two wrapping repositories inherit it by delegation.

**The sweep this asked for came back clean.** Ten call sites reach `ApplyReadFilters`; nine are reads
and `BulkUpdateManyAsync` was the only write among them. Every other write path composes
`ScopeToOwner(ScopeToTenant(...))`. `BulkUpdateAsync` was checked rather than taken on trust and is
genuinely unaffected. `TryGetOwnerScopeFor`, the cross-collection variant, takes no `forWrite` flag at
all and is reached only from `ApplyIsolationTo`, which is search.

Six tests in `GrantTests`, against a real MongoDB and asserting on the stored body of each row, which
is the only place the harm shows up. Four fail when the one-line change is reverted; the other two are
controls in the opposite direction — `[OwnerExemptRoles]` keeps the write breadth it does grant, and
an owner can still bulk-update their own rows — and both fail if the selection is scoped by owner
unconditionally rather than through the write-side scope. One of the six pins the reason the fix is
here and not in the OCC filter: a bulk update with nothing writable in it returns an empty result and
raises no `ConcurrencyException`.

**Still open: the OCC asymmetry.** `UnscopedOccFilter` is unchanged in both bulk paths and
`EntityWriteGuardTests.TheBulkFilterIsNotTenantScoped` still records that. It is now exactly the cheap
defence-in-depth change described above, with no error-reporting problem attached to it.
