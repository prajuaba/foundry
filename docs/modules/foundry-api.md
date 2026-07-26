# 🌐 Foundry API Engine Documentation

**Foundry.Api** is an ASP.NET Core API gateway engine incorporating MediatR pipeline behaviors for security, caching, business rules, and auditing.

---

## 💡 MediatR Pipeline Architecture

```text
Inbound HTTP Request
  │
  ├── 1. LoggingBehavior (Trace ID, W3C headers)
  ├── 2. IdempotencyBehavior (409 Conflict deduplication)
  ├── 3. SecurityBehavior (RBAC claim checks)
  ├── 4. ValidationBehavior (FluentValidation)
  ├── 5. BusinessRulesBehavior (Microsoft.RulesEngine evaluation)
  ├── 6. CachingBehavior (MemoryCache / DistributedCache)
  ├── 7. AuditBehavior (Emits audit events to RealTime/Outbox)
  │
  └── Handler Execution (Repository Invocation)
```

---

## 💡 Capabilities

1. **RBAC Security Decorator**: Inspects `ClaimsPrincipal` against entity API roles (`GET`, `POST`, `PUT`, `DELETE`).
2. **Microsoft.RulesEngine Sandboxing**: Dynamic JSON business rules evaluation.
3. **Idempotency Guard**: Rejects duplicate command submissions.
4. **Real-time Event Broker Integration**: Emits audit logs to SignalR & WebSockets.
