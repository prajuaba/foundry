# Foundry.Api - Dynamic Web API Engine

**Foundry.Api** is a high-performance, container-ready Web API engine for C#/.NET 10. It compiles database schemas and JSON endpoint mappings into dynamic REST Minimal APIs and GraphQL interfaces using Roslyn Source Generators and compile-time code emit, providing **zero-reflection route dispatching** compatible with Native AOT compilation.

---

## Key Features

- **Roslyn Source Generators**: Compile-time route map generation from `api-manifest.json` files with zero runtime overhead or reflection.
- **Dynamic Endpoint Mapper**: Serves RESTful CRUD routes (GET, POST, PUT, DELETE) dynamically based on entity configurations.
- **Unified Caching Pipeline**: High-performance L1 (In-Memory) and L2 (Distributed) cache layer invalidating automatically on mutation events.
- **MediatR CQRS Architecture**: Out-of-the-box behaviors for:
  - **RBAC Security**: Role-based access validation checking caller claims before execution.
  - **Fluent Validation**: Structural parameter validation.
  - **Audit Logging**: Structured auditing through customizable sinks.
- **Dynamic GraphQL**: Auto-configures a HotChocolate GraphQL gateway reflecting available database models.
- **Database Migrations Engine**: Startup schema migrations checking versions against a MongoDB `SchemaHistory` collection with robust connection timeout fallback guards.
- **Structured Auditing**: Production-ready console log formatter (`ConsoleAuditSink`) outputting JSON-compatible mutation events.
- **Docker Ready**: Includes a multi-stage production `Dockerfile` target optimized for container hosting.

---

## Project Structure

```
├── samples
│   └── Foundry.Api.Sample        # Sample Minimal Web API service & Program.cs
├── src
│   ├── Foundry.Api               # MediatR Behaviors, GraphQL Gateway, & Caching
│   └── Foundry.Api.SourceGenerators   # Roslyn Source Generator for manifest routes
└── tests
    └── Foundry.Api.Tests         # Integration & dynamic routing test suites
```

---

## How to Get Started

### Prerequisites
- .NET 10 SDK
- MongoDB (Running locally or configured via Environment Secrets)

### Running the Sample API
To run the sample application:
```bash
dotnet run --project samples/Foundry.Api.Sample/Foundry.Api.Sample.csproj
```

### Running Tests
To run the integration and endpoint validation tests:
```bash
dotnet test tests/Foundry.Api.Tests/Foundry.Api.Tests.csproj
```

---

## Production Deployment & Settings

Configurable variables resolve from environment variables in production setups:
- `MONGODB_CONNECTION`: Connection string (defaults to `mongodb://localhost:27017`).
- `MONGODB_DATABASE`: Collection database name (defaults to `OrderingDb`).
- `MONGODB_ENCRYPTION_KEY`: 32-byte key base string used for field-level encryption.
- `REDIS_CONNECTION`: Redis connection string for L2 Distributed cache setups.
