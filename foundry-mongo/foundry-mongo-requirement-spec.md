# Functional Specification: High-Performance Generic MongoDB Data Access Layer (DAL) with Advanced Cross-Collection Search and Field-Level Auditing

## 1. Goal & Philosophy
Design and implement a reusable, zero-boilerplate, highly efficient, and enterprise-grade C# class library for a MongoDB Data Access Layer. This library must serve as a foundational "lego brick" component. Developers should only need to define their domain entities; the DAL must dynamically handle all CRUD operations, high-performance pagination, cross-collection search, and standardized multi-channel logging without requiring per-entity data access code. It must seamlessly plug into a future Service Layer via Dependency Injection.

## 2. Technical Stack & Constraints
- **Language/Framework:** C# (utilizing modern paradigms like Primary Constructors, Records, Pattern Matching, and Native AOT friendliness).
- **Database Driver:** Official `MongoDB.Driver` (v3.x or latest stable).
- **Asynchrony:** 100% async/await pipeline (`Task`-based) from the driver up to the interface.
- **Performance:** Zero unnecessary allocations, efficient memory management, and leveraging MongoDB aggregation pipelines or native cursors for heavy operations.

## 3. Core Generic Repository Architecture (`IBaseRepository<T>`)

### A. Generic Interface & Implementation
Create a generic repository interface and implementation where `T` is constrained to an `IEntity` contract (enforcing a strongly-typed identifier like `ObjectId` or `string`).
- **CRUD Operations:**
  - `GetByIdAsync(TId id, CancellationToken ct)`
  - `InsertAsync(T entity, CancellationToken ct)`
  - `UpdateAsync(T entity, CancellationToken ct)` (Support full replace and partial/delta updates via Expressions)
  - `DeleteAsync(TId id, CancellationToken ct)` (Support Hard Delete and Soft Delete via a global query filter flag checking for `ISoftDelete`)
  - `BulkInsertAsync(IEnumerable<T> entities, CancellationToken ct)`
  - `BulkUpdateAsync(IEnumerable<T> entities, CancellationToken ct)`

### B. High-Performance Server-Side Paging (`PagedResult<T>`)
Implement an optimized server-side pagination model. The signature must look like:
`Task<PagedResult<T>> GetPagedAsync(PagedRequest request, Expression<Func<T, bool>> filter, CancellationToken ct)`
- **Offset Pagination:** Handled efficiently via `CountDocumentsAsync` and optimized pipelines, containing a configurable maximum depth cap to prevent performance degradation on deep pages.
- **Cursor/Seek Pagination (Preferred):** Support passing a continuation token (e.g., last seen ID/Timestamp and sort order) for ultra-fast $O(1)$ performance on large collections.
- **Return Metadata:** Must return items along with `TotalRecords`, `PageNumber`, `PageSize`, `TotalPages`, and `HasNextPage`.

## 4. Dynamic Cross-Collection Search Engine

### A. Dynamic Criteria Resolution
- Provide a `SearchCriterion` object model that accepts an array of rules consisting of: `Field`, `Operator` (Equals, Contains, StartsWith, GreaterThan, In), and `Value`.
- The DAL must dynamically compile these runtime criteria rules into strongly typed `Expression<Func<T, bool>>` filters using expression tree building.

### B. Cross-Collection Polymorphic Projection
- Implement a `CrossCollectionSearchAsync` orchestrator or specialized repository target.
- **Pipeline Execution:** Leverage the MongoDB Aggregation Pipeline (`$lookup`, `$match`, `$project`) to perform high-performance joins across multiple collections at the database level.
- **Unified Output Model:** Project heterogeneous document schemas into a singular, flattened `UnifiedSearchResultDTO` utilizing optimized driver projections or LINQ statements. 
- Must support server-side pagination (`PagedRequest`) directly at the end of the combined aggregation pipeline before materialization.

## 5. Enterprise Compliance & Field-Level Audit Trail

### A. Inline Diff Interception Engine
Every mutating data access activity (Insert, Update, Delete) must produce an unalterable audit trail automatically, without polluting the business code layer.
- **Pre-Image State Capture:** Before committing an update, the repository must fetch the existing document state tracking its current property allocations.
- **Post-Image Delta Calculation:** Upon applying the update, calculate a high-performance property-by-property delta diff.
- **Execution Delivery:** The generated entry must be emitted asynchronously via an `IAuditSink`. The primary mutation must fail if the audit execution engine is improperly configured.

### B. The Audit Schema (`AuditLogEntry`)
The audit log model must capture full context detailing:
- **Who:** `OperatorId` / `Username` (Injected dynamically via an ambient `ICurrentUserContext`).
- **When:** `Timestamp` (UTC standard format).
- **What:** `EntityId`, `CollectionName`, and the list of specific mutations matching:
  ```csharp
  public record PropertyDiff(string PropertyName, object? OldValue, object? NewValue);