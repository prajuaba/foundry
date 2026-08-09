# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

### Added
- `MongoAuditSink`, a durable MongoDB-backed `IAuditSink` writing to an `audit_log` collection, with
  batch validation before insertion so one invalid entry cannot leave a partial write. Opt-in via
  `AddFoundryMongoAuditSink()`.
- `MongoHealthCheck` and `AddFoundryMongoHealthCheck()`, registered with failure status `Unhealthy`
  so an unreachable database answers a readiness probe with 503 rather than 200.
- OTLP and console exporters for `AddFoundryTelemetry`. The endpoint comes from
  `FoundryTelemetryOptions.OtlpEndpoint` or the standard `OTEL_EXPORTER_OTLP_ENDPOINT`; with neither
  set, telemetry stays in-process, which is now a documented choice rather than an accident.
- Generated applications wire the audit sink, the health check and telemetry, map `/api/health`
  unauthenticated, and register the request-telemetry behavior first so one span wraps the pipeline.
- `foundry new` emits a `Dockerfile` and `.dockerignore`. The image packages a published output
  rather than building from source, because the project references the framework by relative path.
- `ScaffoldedAppWiringTests`: fails the build when a public `AddFoundry*`/`MapFoundry*` registration
  is wired into no generated application and carries no documented exemption.
- An `otel-collector` service in `docker-compose.yml` for verifying the telemetry path locally.

### Changed
- `AuditBehavior` renamed to `RequestTelemetryBehavior`. It never wrote an audit entry — it opens the
  OpenTelemetry activity and records metrics. Three documents claiming it emitted audit entries are
  corrected.
- `FoundryTelemetryOptions.ServiceVersion` reads the entry assembly's informational version with
  build metadata stripped, instead of a hardcoded `"1.0.0"`.
- The quick-start guide no longer quotes test counts, which had drifted by an order of magnitude.

### Fixed
- Both MongoDB services in `docker-compose.yml` now set `nofile` to 64000. Without it `mongod`
  exhausts file descriptors during a long run and WiredTiger panics, which presents as unrelated
  connection-refused failures across every suite still running.

### Removed
- `templates/Foundry.Api.Template`, a second scaffolding path that had stopped compiling, was absent
  from `Foundry.slnx` so no CI job built it, and registered a stub `ICurrentUserContext` that
  silently disabled ownership filtering and masking. `foundry new` is now the only path.

## [1.0.0] - 2026-08-08

### Added
- LICENSE (Apache-2.0) and license metadata across packages.
- Configurable group-claim type names for JWT authentication (`FoundryAuthenticationOptions.GroupClaimTypes`).
- A schema-validation warning when a workflow transition or state declares no roles.
- Bounded (one-level) recursive redaction of sensitive nested workflow payload properties.
- A `WorkflowCommandTypeRegistry` replacing assembly-wide reflection scanning for `InternalApi` workflow actions.
- `foundry token mint`: mints a self-issued HS256 JWT from the same `Authentication__Jwt__SigningKey` a deployment already configures, for local dev, CI, and service-to-service use.
- Role-based masking entitlement: `[SensitiveData]` and `[PiiData]` both gained a `Roles` property, so a caller can unmask a category by role as well as by `view:{category}` scope.
- `FoundryMongoOptions.AllowUnauthenticatedFullReads`, the named opt-in that restores pre-1.0 read behavior for a deployment with a genuine no-caller use case.

### Changed
- Bulk-write optimistic-concurrency filters (`BulkUpdateManyAsync`, `BulkUpdateAsync`) now scope by tenant and owner, matching every single-document write path.
- The archival sweep now processes documents in bounded batches instead of loading a whole entity type into memory and archiving a full year in one transaction.
- `foundry version` now reports the actual assembly version instead of a hardcoded string.
- An unauthenticated caller reading an owner-scoped entity now reads zero rows by default, not every row. See `AllowUnauthenticatedFullReads` above for the opt-out.

### Fixed
- `foundry version` and `foundry doctor` could report different version numbers for the same build; they now share one source of truth.
- The `distro-binary` CI job could fail with `'<hash>' is not a valid version string` when built from an untagged commit, because `git describe --tags --always` never fails and silently returned a bare commit hash instead of a version.
