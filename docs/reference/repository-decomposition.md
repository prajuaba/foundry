# Decomposing `Repository<T>`

A four-step plan, one step per commit, each CI-green before the next. Step 1 stage one is done;
everything below is not.

**The rule for every step: a pure move first, a behaviour change never in the same commit.** This is
the class where a mistake is a tenant-isolation failure, and its access-policy cluster has already
needed three separate security passes. A structural change combined with a semantic one in the same
diff is how the fourth gets in.

---

## Where it stands

**Step 1 is done.** The access policy is a real collaborator with tests that need no database.

| | Lines |
| :--- | ---: |
| `Repository.cs` | 2,027 |
| `EntityAccessPolicy.cs` | 593 |

Stage one moved the cluster into a `partial class` file; stage two turned it into
`EntityAccessPolicy<T>`, constructed as `(ITenantContext?, ICurrentUserContext?)`, with
`Repository<T>` delegating at 39 call sites. `Repository.AccessPolicy.cs` is gone and `Repository<T>`
is no longer `partial`. `Repository.cs` went from 2,563 lines before step 1 to 2,027.

Both stages were verified as pure moves by comparing every non-comment, non-blank line before and
after — nothing lost, the only additions being the new type's scaffolding and the delegation.

Five unit tests now cover the policy in **~61 ms with no MongoDB**, which was the point: this cluster
produced three separate security defects and every one had to be caught through a live database.

**Measure before you trust this table.** The figures above were wrong by the time stage two ran —
the spec said 2,035/530 and 36 call sites; the truth was 2,034/573 and 39. The agent doing the work
reported what it measured rather than matching the doc, which is the correct instinct. Do the same.

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
| 2 | `EntitySearchTranslator<T>` — see below | The camelCase class of defect lives entirely here, and it has produced two bugs already |
| 3 | `EntityWriteGuard<T>` — OCC on `Version`, `StampTenant`, concurrency-exception paths | Write-side invariants, currently interleaved with read-side ones |
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

## Step 2 — `EntitySearchTranslator<T>`

Same shape as step 1: a pure move first, then the collaborator, then tests — separate commits.

**Members:** `BuildBsonFilter`, `BuildExpression`, `ConvertToBsonValue`, `BuildBsonArray`,
`EscapeRegex`, `BuildSortDefinition`, and the cross-collection pipeline assembly inside
`CrossCollectionSearchAsync`.

**Dependency to check first.** `BuildBsonFilter` is `static` and calls
`EntityAccessPolicy<T>.ElementName`, so the translator needs the entity type — probably the same
per-type parameter that method already takes, rather than a constructor dependency. `BuildExpression`
calls `EnsureCriteriaAreFilterable` on the policy, so the translator holds a reference to it or the
repository sequences the two. Measure the actual references before designing the constructor; that
measurement is what made step 1 straightforward.

**Why this one earns its own type.** Two defects have come out of it, both the same root cause and
both silent: a hand-built `BsonDocument` does not resolve names through the class map, so a
PascalCase field name matches nothing and errors nowhere. The soft-delete predicate in
cross-collection search never excluded a row; the criteria never matched a field. A filter that
matches nothing returns nothing, and for a *search* "no results" is a plausible answer every time.

**What the tests must assert.** That a criterion **matches a row**, not that a stage was built.
`CrossCollectionSearchAsync_BuildsCorrectPipelineDefinition` asserted stage shape, passed for years,
and has been corrected twice for exactly these two defects — it checks the document the code builds
and never the one MongoDB matches against. Prefer assertions that would fail if the element name were
wrong.
