# Changelog

All notable changes to this project are documented here.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/).

## [Unreleased]

### Added
- LICENSE (Apache-2.0) and license metadata across packages.
- Configurable group-claim type names for JWT authentication (`FoundryAuthenticationOptions.GroupClaimTypes`).
- A schema-validation warning when a workflow transition or state declares no roles.
- Bounded (one-level) recursive redaction of sensitive nested workflow payload properties.
- A `WorkflowCommandTypeRegistry` replacing assembly-wide reflection scanning for `InternalApi` workflow actions.

### Changed
- Bulk-write optimistic-concurrency filters (`BulkUpdateManyAsync`, `BulkUpdateAsync`) now scope by tenant and owner, matching every single-document write path.
- The archival sweep now processes documents in bounded batches instead of loading a whole entity type into memory and archiving a full year in one transaction.
- `foundry version` now reports the actual assembly version instead of a hardcoded string.

### Fixed
- `foundry version` and `foundry doctor` could report different version numbers for the same build; they now share one source of truth.
