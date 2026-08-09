# Foundry diagnostics

> Generated from `DiagnosticCatalog`. Codes are stable and safe to match on.

Ranges: `FDY1xxx` document structure, `FDY2xxx` cross-reference integrity,
`FDY3xxx` configuration coherence, `FDY4xxx` identifier safety.

| Code | Meaning |
| :--- | :--- |
| `FDY1001` | The IR document must set a non-empty 'namespace'. |
| `FDY1002` | The IR document declares no entities; nothing would be generated. |
| `FDY1003` | Every entity must have a non-empty 'name'. |
| `FDY1004` | An entity with no properties generates an empty record. |
| `FDY1005` | Every entity must have exactly one property with 'isKey': true. |
| `FDY1006` | An entity must not declare more than one 'isKey' property. |
| `FDY1007` | Every property must have a non-empty 'name'. |
| `FDY1008` | Every property must have a non-empty 'type'. |
| `FDY1009` | An enum must declare at least one value. |
| `FDY1010` | This document is in Studio canvas format ('nodes'/'edges'). The compiler consumes the normative IR format ('entities'/'enums'/'dtos'). Convert it with 'foundry migrate'. |
| `FDY1011` | The key property must be of type 'objectid'. The MongoDB data layer constrains IRepository<T> to IEntity<ObjectId>, so an entity keyed on any other type generates code that compiles but has no resolvable repository -- it can never be persisted or served. |
| `FDY2001` | Entity names must be unique; duplicates would generate colliding C# types. |
| `FDY2002` | Property names must be unique within an entity. |
| `FDY2003` | A workflow transition references a state not present in the workflow's 'states'. |
| `FDY2004` | A workflow's 'entity' must name a declared entity. |
| `FDY2005` | A workflow must mark exactly one state 'isInitial'. |
| `FDY2006` | A workflow should mark at least one state 'isFinal' or it can never complete. |
| `FDY2007` | An index field must name a property declared on the same entity. |
| `FDY2008` | A property marked 'isEnum' must have a type matching a declared enum name. |
| `FDY2009` | A custom endpoint's 'targetEntity' must name a declared entity. |
| `FDY2010` | A DTO property's 'sourceEntity' must name a declared entity. |
| `FDY2011` | A DTO property's 'sourceProperty' must name a property on its 'sourceEntity'. |
| `FDY2012` | A transition target must be a declared state or choice node. |
| `FDY2013` | Transition 'trigger' names must be unique; duplicates generate colliding command types. |
| `FDY2014` | An entity, enum and DTO all become C# types in one namespace and are written to one file per name, so their names must not collide. A collision silently discards one of them. |
| `FDY3001` | A property marked 'isTenantKey' requires the entity to set 'multiTenant': true. |
| `FDY3002` | An entity with 'multiTenant': true must mark one property 'isTenantKey' or set 'tenantProperty'. |
| `FDY3003` | Setting 'kafkaTopic' without 'enableKafkaOutbox': true has no effect. |
| `FDY3004` | Setting 'fileIOAllowedExtensions' without 'enableFileIO': true has no effect. |
| `FDY3005` | The attribute is not in the supported vocabulary and will be ignored. |
| `FDY3006` | The type is not in the supported vocabulary and will be emitted verbatim as a C# type name. |
| `FDY3007` | A partitioned entity's 'archiveThresholdYears' must be greater than zero. |
| `FDY3008` | A custom endpoint 'method' must be one of GET, POST, PUT, PATCH, DELETE. |
| `FDY3009` | A custom endpoint 'route' must begin with '/'. |
| `FDY3010` | An enum is declared but no property is typed with it. Usually the property that should use it was left as a plain scalar, which loses type safety and leaves the enum dead. |
| `FDY3011` | The tenant key property must be named 'TenantId'. The data layer builds its tenant filter against the stored field by that name, so any other name compiles to an entity that does not satisfy IMultiTenant -- and, if it did, would filter on a field no document has. |
| `FDY3012` | A property marked 'isOwnerKey' requires the entity to set 'ownerScoped': true. |
| `FDY3013` | An entity with 'ownerScoped': true must mark one property 'isOwnerKey'. |
| `FDY3014` | The owner key property must be named 'OwnerId', for the same reason the tenant key must be named 'TenantId': the data layer filters on the stored field by name. |
| `FDY3015` | Setting 'ownerExemptRoles' without 'ownerScoped': true has no effect; there is no owner filter for those roles to be exempt from. |
| `FDY3016` | Marking a property 'isSharedWithKey' without 'ownerScoped': true has no effect; a grant widens an owner filter, and there is none to widen. |
| `FDY3017` | The grant set must be a property named 'SharedWith' of type 'List<string>', for the same reason the owner key must be named 'OwnerId': the data layer filters on the stored field by name. |
| `FDY3018` | A role listed in both 'ownerExemptRoles' and 'ownerReadExemptRoles' is fully exempt; the read-only listing has no effect and reads as a restriction that is not applied. |
| `FDY3019` | A decision gate whose branches are all conditional declares no 'defaultState', so a transition reaching it when no branch matches is refused at runtime. |
| `FDY3021` | A workflow state or transition with no roles is reachable by any authenticated caller. If this is intentional, no action is needed. |
| `FDY3022` | A workflow action's 'type' must be either 'InternalApi' or 'ExternalApi'. Other values like 'Webhook' or 'Command' are not supported by the workflow engine and will fail at runtime. |
| `FDY3023` | An InternalApi action requires a 'requestType' field naming the command to dispatch internally. |
| `FDY3024` | An ExternalApi action requires a 'url' field for the external HTTP endpoint. |
| `FDY3025` | A property cannot be both encrypted and masked. ProtectionType is a single value, not a set, so the compiler honours Encrypt and drops the mask -- the value is then returned in full to every caller entitled to read the entity, which is the opposite of what declaring a mask asks for. |
| `FDY4001` | The name cannot be emitted as C#. Use letters, digits and underscores, starting with a letter or underscore. |
| `FDY4002` | The name is a reserved C# keyword. Choose a different name. |
| `FDY4003` | The namespace must be dot-separated valid C# identifiers, e.g. 'Acme.Billing'. |
| `FDY4004` | The attribute argument contains quote, backslash or brace characters that cannot be safely emitted. |
