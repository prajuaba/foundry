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
| `FDY4001` | The name cannot be emitted as C#. Use letters, digits and underscores, starting with a letter or underscore. |
| `FDY4002` | The name is a reserved C# keyword. Choose a different name. |
| `FDY4003` | The namespace must be dot-separated valid C# identifiers, e.g. 'Acme.Billing'. |
| `FDY4004` | The attribute argument contains quote, backslash or brace characters that cannot be safely emitted. |
