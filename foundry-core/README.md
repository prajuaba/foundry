# Foundry.Core

Shared contracts library for the Foundry software factory framework.
Contains platform-agnostic models, interfaces, attributes, and helpers used across visual schema design, API generation, and MongoDB storage layers.

## Contents
- **Entities**: Core entity contracts (`IEntity`, `BaseEntity`, `IVersionable`, `ISoftDelete`)
- **Attributes**: Annotations for visual-to-POCO mapping (`[Indexed]`, `[SensitiveData]`)
- **Paging**: Pagination structures (`PagedRequest`, `PagedResult`)
- **Search**: Dynamic query parameters (`SearchCriterion`)
- **Security**: Symmetric protection interfaces (`IEncryptionProvider`)
- **Audit**: Log entry definitions (`AuditLogEntry`, `PropertyDiff`)
- **User**: Context interface for operational audit logging (`ICurrentUserContext`)
