# Decomposing `Repository<T>`

A four-step plan, one step per commit, each CI-green before the next. Step 1 stage one is done;
everything below is not.

**The rule for every step: a pure move first, a behaviour change never in the same commit.** This is
the class where a mistake is a tenant-isolation failure, and its access-policy cluster has already
needed three separate security passes. A structural change combined with a semantic one in the same
diff is how the fourth gets in.

---

## Where it stands

| | Lines |
| :--- | ---: |
| `Repository.cs` | 2,035 |
| `Repository.AccessPolicy.cs` | 530 |

Stage one moved the access-policy cluster into a `partial class` file — verified as a pure move:
comparing every non-comment line across the old file and the two new ones, nothing was lost or added
except the class declaration gaining `partial` and the new file's namespace and braces.

---

## Step 1, stage two — `EntityAccessPolicy<T>`

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
| 2 | `EntitySearchTranslator<T>` — `BuildBsonFilter`, `BuildExpression`, `ConvertToBsonValue`, `EscapeRegex`, cross-collection pipeline assembly | The camelCase class of defect lives entirely here, and it has produced two bugs already |
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
