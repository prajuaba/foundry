# Foundry IR vocabulary

> Generated from `Vocabulary.cs`. This is the list the compiler actually honours.

## Scalar types

| IR type | Emitted C# type |
| :--- | :--- |
| `string` | `string` |
| `int` | `int` |
| `long` | `long` |
| `decimal` | `decimal` |
| `double` | `double` |
| `float` | `float` |
| `bool` | `bool` |
| `DateTime` | `DateTime` |
| `DateOnly` | `DateOnly` |
| `TimeOnly` | `TimeOnly` |
| `Guid` | `Guid` |
| `ObjectId` | `ObjectId` |

Recommended key types: `ObjectId`.

A property may also be typed as the name of an enum declared in `enums`;
set `"isEnum": true` in that case.

## Property attributes

| Attribute | Args | Entities | DTOs | Effect |
| :--- | :--- | :--- | :--- | :--- |
| `Required` | — | yes | yes | Emits [Required] and makes the C# property 'required', so it must be supplied. |
| `Unique` (alias: UniqueIndex) | — | yes | no | Creates a unique MongoDB index on the property. |
| `Indexed` (alias: Index) | — | yes | no | Creates a non-unique MongoDB index on the property. |
| `TextIndex` | — | yes | no | Includes the property in the entity's full-text search index. |
| `TenantKey` | — | yes | no | Marks the property as the tenant discriminator. Requires the entity to set multiTenant: true. |
| `Encrypt` | — | yes | no | Encrypts the property at rest with AES-256-GCM via the KMS envelope key. |
| `Mask` | — | yes | no | Irreversibly masks the property in logs and API responses. |
| `MaskEmail` | — | yes | no | Masks the property using email-shaped masking, e.g. j***@domain.com. |
| `PiiEmail` | — | yes | no | Tags the property as personally identifiable email data for audit reporting. |
| `PiiCreditCard` | — | yes | no | Tags the property as payment card data for audit reporting. |
| `MinLength` | yes | yes | yes | Minimum string length. Takes one integer argument. |
| `MaxLength` | yes | yes | yes | Maximum string length. Takes one integer argument. |
| `Range` | yes | yes | yes | Inclusive numeric range. Takes two numeric arguments. |
| `Regex` | yes | yes | yes | Emits a compiled [GeneratedRegex] validation pattern. Takes one quoted string argument. |
| `Email` | — | yes | yes | Validates the property as an email address. |
| `Url` | — | yes | yes | Validates the property as an absolute URL. |
| `Phone` | — | yes | yes | Validates the property as a telephone number. |

Argument values may only be numbers, booleans, or double-quoted strings with no
embedded quote, backslash or brace. Anything else is rejected as unsafe to emit
(`FDY4004`).

## Enumerated fields

- Endpoint `method`: DELETE, GET, PATCH, POST, PUT
- Endpoint `operationType`: Custom, Insert, Query, Update
- Connector `type`: GraphQL, REST, SOAP
- Connector `authType`: ApiKey, Basic, Bearer, None, OAuth2
