# Foundry.Api Developer Reference Manual

This document provides deep technical details on the architecture, pipelines, and extension patterns of the **Foundry.Api** framework.

---

## 1. Dynamic Endpoint Mapping Architecture

Dynamic APIs in Foundry are exposed through two distinct mechanisms working in tandem:

```mermaid
graph TD
    A[api-manifest.json] --> B[Roslyn Source Generator]
    B --> C[GeneratedEndpoints.g.cs]
    A --> D[DynamicEndpointRouteBuilder]
    C --> E[ASP.NET Core Minimal APIs]
    D --> E
```

### A. Compile-Time Source Generation (`Foundry.Api.SourceGenerators`)
During compilation, the `ApiRouteGenerator` analyzes the structure of `api-manifest.json`:
1. It parses the JSON representation of entities and custom routes.
2. Statically emits type-safe minimal endpoint mappings (e.g. `endpoints.MapGet("/api/v1/orders", ...)`).
3. This completely bypasses runtime reflection, resulting in fast routing speeds and compatibility with **Native AOT**.

### B. Runtime Dynamic Mapping (`DynamicEndpointRouteBuilder`)
Fallback endpoint mapping is orchestrated by `DynamicEndpointRouteBuilder` at startup. It maps endpoints using expression tree compilations:
- Maps query parameters directly to entity property search expressions using LINQ expressions.
- Compiles custom advanced filtering criteria (combining `And`/`Or`/`Not` logic dynamically).

---

## 2. MediatR Request Validation & Execution Pipeline

All dynamic routing requests dispatch through a unified MediatR CQRS pipeline. The execution flow is governed by standard pipeline behaviors:

```mermaid
sequenceDiagram
    Caller->>MediatR: Dispatch Request
    MediatR->>SecurityBehavior: Validate RBAC roles
    SecurityBehavior->>ValidationBehavior: Stage 1: Check validation rules
    ValidationBehavior->>BusinessRuleBehavior: Stage 2: Verify business rules
    BusinessRuleBehavior->>CachingBehavior: Read cache (L1/L2)?
    alt Cache Hit
        CachingBehavior-->>Caller: Return Cached Data
    else Cache Miss
        CachingBehavior->>Handler: Execute Handler
        Handler->>AuditBehavior: Record audit log delta
        AuditBehavior->>ConsoleAuditSink: Emit JSON log
        Handler-->>Caller: Return Result
    end
```

### A. Security Validation (`SecurityBehavior`)
- Resolves caller claims from the registered `ICurrentUserContext`.
- Compares claims against allowed RBAC roles configured in the manifest metadata. If unauthorized, returns a `403 Forbidden` response.

### B. Structural Validation (`ValidationBehavior`)
- Scans registered FluentValidation `IValidator<T>` rules and DataAnnotations matching request models (Stage 1 validation).
- Halts execution and returns a structured validation error payload (HTTP 400) if checks fail.

### C. Business Rules Validation (`BusinessRuleBehavior`)
- Executes asynchronous domain rules (`IBusinessRule<TRequest>`) registered for the command request (Stage 2 validation).
- Performs database-dependent validation (like uniqueness checks, state machine transition validity).
- Throws validation exceptions matching the FluentValidation format for consistent HTTP mapping.

### D. Cache Orchestration (`CachingBehavior`)
- Utilizes L1 (In-Memory) and L2 (Distributed Cache e.g. Redis) caches for query endpoints.
- Evicts keys matching affected collections upon command mutations (Insert, Update, Delete) to guarantee consistency.

### E. Audit Trail Telemetry (`AuditBehavior`)
- Post-execution wrapper capturing transaction outcomes.
- Invokes registered `IAuditSink` (e.g., `ConsoleAuditSink`) to output structured JSON mutation records.

---

## 3. Database Schema Migrations Engine

To handle structural entity alterations cleanly in production:
1. **Migrations Registry**: Subclasses of `DatabaseMigration` represent schema versions (e.g. `Migration_V1_Initial.cs`).
2. **Execution Runner**: `MigrationRunner.cs` checks the current version logged in the MongoDB `SchemaHistory` collection, executing pending migrations before the HTTP server starts.
3. **Mock/Testing Protection**: Includes a connection timeout guard, allowing integration testing setups with mock databases to skip migrations execution cleanly instead of hanging.
