# Foundry.E2E.Showcase — Technical Reference

> **Auto-generated from IR schema. Do not hand-edit.**
> Source: e2e-schema.ir.json | Version: 2.0.0 | SHA256: 63b184963bd6eead94fa1702889780dc9d32a81aca5bf0ab774983e2a5b0df53
> Manifest: api-manifest.json | SHA256: 8a5318ff231f4f682847ffd4d31c0afc14c2dd97359a3d61323c37ed3560b352

## 1. Scope

### Topics Covered

This reference documents the data model, authorization rules, workflows, API surface, and external integrations derived from the Foundry IR schema. It is the authoritative source for:

- Entity definitions and relationships
- Property types, constraints, and protection levels
- Role-based access control (CRUD and resource-level)
- Workflow state machines and transitions
- Custom endpoint routes, methods, roles, and filters
- Persistence (indexes, caching, archival policies)
- Real-time and event-driven capabilities
- External system integrations

### Topics Not Covered

The following require hand-authored documentation:

| Topic | Why Not Derivable |
| --- | --- |
| Business Rule Bodies | Validation logic is hand-authored and not derivable from rule names alone. |
| Custom Endpoint Request Bodies | JSON/XML structure and field semantics of request payloads are not in the IR. |
| Deployment Topology | Infrastructure, regions, and scaling strategy are external to the schema. |
| Event Contracts | Message field names, types, and consumer implementations are not in the schema. |
| Non-functional Requirements | Performance targets, availability SLOs, and capacity thresholds are not schema-encoded. |
| Rationale | Architectural decisions and design philosophy are not expressed in the entity model. |
| Runbooks and Disaster Recovery | Operational procedures and DR plans cannot be inferred from data structures. |

## 2. System Overview

| Metric | Count |
| --- | --- |
| Entities | 5 |
| Enums | 3 |
| DTOs | 1 |
| Custom Endpoints | 3 |
| Workflows | 1 |
| Connectors | 3 |
| Total Properties | 30 |
| Total Indexes | 3 |
| Caching Configurations | 3 |
| Distinct Roles | 6 |
| Kafka Topics | 2 |
| Real-Time Entities | 1 |
| Distinct Business Rules | 2 |
| Endpoints with Assignments | 1 |
| Endpoints with Rules | 1 |

## 3. Domain Model

### 3.1 Customer

**Flags**: multi-tenant, soft-delete, auditable, GraphQL

| Property | Type | Constraints | Notes |
| --- | --- | --- | --- |
| Id | ObjectId | — | [Key] |
| TenantId | string | — | [Tenant Key] |
| Email | string | Unique, Required, MaskEmail | — |
| FullName | string | Required, MaxLength(120) | — |
| PhoneNumber | string | Mask, Phone | [Sensitive: contact] |
| CreditLimit | decimal | Range(0, 1000000) | — |
| Tier | CustomerTier | — | [Enum] |

### 3.2 Product

**Flags**: soft-delete, GraphQL, real-time, file-IO

| Property | Type | Constraints | Notes |
| --- | --- | --- | --- |
| Id | ObjectId | — | [Key] |
| Sku | string | Unique, Required | — |
| Name | string | Required, TextIndex | — |
| Description | string | TextIndex, MaxLength(2000) | — |
| UnitPrice | decimal | — | — |
| StockQuantity | int | — | — |

### 3.3 Order

**Flags**: soft-delete, auditable, GraphQL, Kafka outbox

| Property | Type | Constraints | Notes |
| --- | --- | --- | --- |
| Id | ObjectId | — | [Key] |
| CustomerId | ObjectId | Indexed | — |
| OrderNumber | string | Unique, Required | — |
| TotalAmount | decimal | — | — |
| PaymentCardNumber | string | Mask, PiiCreditCard | [Sensitive: financial] |
| Status | OrderStatus | Indexed | [Enum] |
| Shipment | ShipmentMethod | — | [Enum] |
| OrderDate | DateTime | Indexed | — |

### 3.4 CustomerNote

**Flags**: soft-delete, auditable, owner-scoped

| Property | Type | Constraints | Notes |
| --- | --- | --- | --- |
| Id | ObjectId | — | [Key] |
| OwnerId | string | — | [Owner Key] |
| SharedWith | List<string> | — | — |
| Body | string | Required, Encrypt, MaxLength(4000) | — |

### 3.5 LedgerEntry

**Flags**: multi-tenant, auditable, partitioned, archive-after-2-years

| Property | Type | Constraints | Notes |
| --- | --- | --- | --- |
| Id | ObjectId | — | [Key] |
| TenantId | string | — | [Tenant Key] |
| Reference | string | Required, Indexed | — |
| Amount | decimal | — | — |
| PostedAt | DateTime | Indexed | — |

## 4. Authorization Matrix

### CRUD Authorization

> Roles shown are those **enforced** by the runtime, read from api-manifest.json.

| Entity | GET | GET_BY_ID | POST | PUT | DELETE |
| --- | --- | --- | --- | --- | --- |
| Customer | Admin, Sales | Admin, Sales | Admin | Admin | Admin |
| Product | Admin, Sales, Warehouse | Admin, Sales, Warehouse | Admin | Admin, Warehouse | — |
| Order | Admin, Sales | Admin, Sales, Customer | Admin, Customer | Admin | Admin |
| CustomerNote | Sales, Supervisor, Auditor | Sales, Supervisor, Auditor | Sales | Sales, Supervisor | Supervisor |
| LedgerEntry | Admin, Auditor | Admin, Auditor | Admin | — | — |

### Role-Based Access

> Roles shown are those **enforced** by the runtime, read from api-manifest.json.

| Role | Readable Entities | Writable Entities |
| --- | --- | --- |
| Admin | Customer, Product, Order, LedgerEntry | Customer, Product, Order, LedgerEntry |
| Auditor | CustomerNote, LedgerEntry | — |
| Customer | — | Order |
| Sales | Customer, Product, Order, CustomerNote | CustomerNote |
| Supervisor | CustomerNote | CustomerNote |
| Warehouse | Product | Product |

### Owner-Scoped Access Control

**CustomerNote**:
  - Write exempt roles: Supervisor
  - Read exempt roles: Auditor

## 5. Data Protection Register

| Entity | Property | Category | Protection | Effect |
| --- | --- | --- | --- | --- |
| Customer | PhoneNumber | contact | Mask, Phone | redacted in responses unless the caller holds view:contact or an entitled role |
| Order | PaymentCardNumber | financial | Mask | redacted in responses unless the caller holds view:financial or an entitled role |
| CustomerNote | Body | — | Encrypt | stored as ciphertext; unreadable if the key is rotated |

### Multi-Tenancy & Isolation

2 entities are multi-tenant.
Tenant property names used: TenantId

## 6. Workflow Specifications

### 6.1 order-fulfilment

Entity: Order | Version: 2.0.0 | Active: Yes

```mermaid
stateDiagram-v2
    [*] --> Pending
    state value-gate <<choice>>
    Pending --> value-gate : SubmitOrderForFulfilment
    Review --> Approved : ApproveReviewedOrder
    Approved --> Shipped : ShipOrder
    Review --> Cancelled : CancelReviewedOrder
    value-gate --> Review : TotalAmount GreaterThan 10000 (entity)
    value-gate --> Cancelled : PaymentCardNumber Equals (empty string) (entity)
    value-gate --> Approved : default
```

#### Transitions

| Trigger | From | To | Required Roles | Guards |
| --- | --- | --- | --- | --- |
| SubmitOrderForFulfilment | Pending | value-gate | Customer, Sales, Admin | TotalAmount GreaterThan 0 (entity) |
| ApproveReviewedOrder | Review | Approved | Admin | — |
| ShipOrder | Approved | Shipped | Warehouse, Admin | — |
| CancelReviewedOrder | Review | Cancelled | Admin | — |

#### Choice Nodes

| Node ID | Branches | Default |
| --- | --- | --- |
| value-gate | TotalAmount GreaterThan 10000 (entity) → Review; PaymentCardNumber Equals (empty string) (entity) → Cancelled | Approved |

## 7. API Surface

### Generated CRUD Routes

| Entity | Route | Methods |
| --- | --- | --- |
| Customer | /api/customers | GET, GET_BY_ID, POST, PUT, DELETE |
| Product | /api/products | GET, GET_BY_ID, POST, PUT |
| Order | /api/orders | GET, GET_BY_ID, POST, PUT, DELETE |
| CustomerNote | /api/customernotes | GET, GET_BY_ID, POST, PUT, DELETE |
| LedgerEntry | /api/ledgerentries | GET, GET_BY_ID, POST |

_Note: append `/{id}` to route for GET_BY_ID, PUT, DELETE methods._

### Custom Endpoints

> Roles shown are those **enforced** by the runtime, read from api-manifest.json.

| Route | Method | Type | Entity | Request | Roles | Filter | Assignments | Rules |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| /api/v1/orders/submit | POST | Insert | Order | SubmitOrderCommand | Customer, Admin | — | — | SubmitOrderRule |
| /api/v1/orders/{id}/cancel | PUT | Update | Order | CancelOrderCommand | Admin, Sales | — | Status <- NewStatus | — |
| /api/v1/products/in-stock | GET | Query | Product | InStockProductsQuery | Admin, Sales, Warehouse | StockQuantity GreaterThan MinimumStock | — | — |

### Schema/Manifest Divergence & Enforcement Verification

**No divergences detected.** Comparison verified:
- 5 CRUD endpoint(s) compared
- 3 custom endpoint(s) compared
- 4 workflow transition endpoint(s) excluded (compiler-derived)

## 8. Event & Real-Time Catalog

### Kafka Outbox

| Source | Topic |
| --- | --- |
| Order (entity) | order-events |
| OrderSummary (DTO) | order-summaries |

> **Not derivable from the schema.** Document the event contract (field names, types, transformations) and consumer implementation details.

### Real-Time Subscriptions

| Entity | Roles |
| --- | --- |
| Product | Admin, Warehouse |

## 9. Persistence & Performance

### Indexes

| Entity | Index Name | Fields | Unique |
| --- | --- | --- | --- |
| Customer | ux_customer_email | Email | Yes |
| Product | — | Sku | Yes |
| Order | ix_order_customer_date | CustomerId, OrderDate | No |

### Caching

| Entity | Method | TTL (seconds) |
| --- | --- | --- |
| Customer | GET | 60 |
| Customer | GET_BY_ID | 120 |
| Product | GET | 30 |

### Archival & Partitioning

| Entity | Archived After | Partitioned |
| --- | --- | --- |
| LedgerEntry | 2 years | Yes |

## 10. External Dependencies

| Name | Type | Base URL | Auth | Timeout | Retries | Credential Source | Literals Present |
| --- | --- | --- | --- | --- | --- | --- | --- |
| PaymentGateway | REST | https://payments.example.com | Bearer | 15 | 3 | ${PAYMENT_GATEWAY_TOKEN} | — |
| LegacyInventory | SOAP | https://legacy.example.com/inventory.asmx | Basic | 30 | 2 | ${LEGACY_INVENTORY_PASSWORD} | username |
| PartnerCatalog | GraphQL | https://partners.example.com/graphql | ApiKey | 20 | 1 | ${PARTNER_CATALOG_KEY} | — |

> **Note**: Some credential fields contain values committed into the schema rather than resolved from environment variables. A security reviewer should confirm each field is intended to be non-secret.

### Connector Details

**LegacyInventory**:
- SOAP Action: `http://legacy.example.com/CheckStock`

**PartnerCatalog**:
- API Key Header: `X-Partner-Key`

## 11. Extension Points

### Custom Endpoint Request Types

| Request Type | Kind | Endpoint | Implementation | Authored |
| --- | --- | --- | --- | --- |
| CancelOrderCommand | Update | /api/v1/orders/{id}/cancel | Generated/Commands/CancelOrderCommand.cs | Auto-generated |
| InStockProductsQuery | Query | /api/v1/products/in-stock | Generated/Commands/InStockProductsQuery.cs | Auto-generated |
| SubmitOrderCommand | Insert | /api/v1/orders/submit | Generated/Commands/SubmitOrderCommand.cs | Auto-generated |

### Business Rules

| Rule Name | Kind | Bound via | Implementation | Authored |
| --- | --- | --- | --- | --- |
| OrderCreditLimitRule | Validation | entity Order POST | Generated/Rules/OrderCreditLimitRule.cs | Scaffold (hand-written) |
| SubmitOrderRule | Validation | endpoint /api/v1/orders/submit | Generated/Rules/SubmitOrderRule.cs | Scaffold (hand-written) |

> **Not derivable from the schema.** Business rule bodies are hand-written and their validation logic cannot be inferred from the schema.

## 12. Gaps for the Author

The following topics cannot be derived from the schema and require hand-authored documentation:

- **Section 7. Custom Endpoint Request Bodies**: JSON/XML structure and field semantics of request payloads.
- **Section 8. Event Contracts**: Message field names, types, and consumer implementations.
- **Section 11. Business Rule Bodies**: Validation logic for rule implementations.
- **Document-level — Deployment Topology**: Infrastructure, regions, and scaling strategy.
- **Document-level — Non-functional Requirements**: Performance targets, availability SLOs, and capacity thresholds.
- **Document-level — Rationale**: Architectural decisions and design philosophy.
- **Document-level — Runbooks and Disaster Recovery**: Operational procedures and DR plans.
