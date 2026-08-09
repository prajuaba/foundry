# 🌐 Foundry API Engine Documentation

**Foundry.Api** is an ASP.NET Core API gateway engine incorporating MediatR pipeline behaviors for security, caching, business rules, and auditing.

---

## 💡 MediatR Pipeline Architecture

```text
Inbound HTTP Request
  │
  ├── 1. RequestTelemetryBehavior (OpenTelemetry span, metrics, logging)
  ├── 2. SecurityBehavior (RBAC claim checks)
  ├── 3. ValidationBehavior (FluentValidation)
  ├── 4. BusinessRulesBehavior (Microsoft.RulesEngine evaluation)
  ├── 5. IdempotencyBehavior (409 Conflict deduplication)
  ├── 6. CachingBehavior (MemoryCache / DistributedCache)
  │
  └── Handler Execution (Repository Invocation → IAuditSink writes audit trail)
```

---

## 💡 Capabilities

1. **RBAC Security Decorator**: Inspects `ClaimsPrincipal` against entity API roles (`GET`, `POST`, `PUT`, `DELETE`).
2. **Microsoft.RulesEngine Sandboxing**: Dynamic JSON business rules evaluation.
3. **Idempotency Guard**: Rejects duplicate command submissions.
4. **Real-time Event Broker Integration**: Emits audit logs to SignalR & WebSockets.
