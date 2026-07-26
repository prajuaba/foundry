# 🧪 Foundry.Testing

`Foundry.Testing` is an autonomous multi-protocol testing engine for the Foundry framework. It reads visual domain schemas or compiled C# assemblies to generate synthetic mock data, xUnit test suites across all 7 supported protocols, and interactive execution reports.

---

## 🌟 Supported Protocols & Coverage

1. **REST API CRUD Endpoints**: Verifies `GET`, `POST`, `PUT`, `DELETE`, and soft-delete filtering.
2. **GraphQL Gateway**: Verifies query execution, nested object projections, and mutations.
3. **Kafka Event Outbox**: Validates domain mutation event capture and header trace propagation.
4. **Real-Time WebSockets & SSE**: Tests mutation broadcasting to connected clients.
5. **FileIO Pipelines**: Validates file streaming upload, signature checking, and path sanitization.
6. **MediatR Business Rules**: Tests FluentValidation rules and exception throwing contracts.
7. **Workflow State Machines**: Tests state transition guards, effective dates, and workflow rules.

---

## 🚀 CLI Usage

```bash
# Run automated test generation and execution via CLI
foundry test schema.json --output-dir tests/ --samples 50

# Output Artifacts:
# ├── GeneratedTests/
# │   ├── RestApiTests.cs
# │   ├── GraphQlTests.cs
# │   ├── KafkaOutboxTests.cs
# │   ├── RealTimeNotificationTests.cs
# │   ├── FileIoPipelineTests.cs
# │   ├── BusinessRulesTests.cs
# │   └── WorkflowStateMachineTests.cs
# ├── test-report.html (Interactive HTML report with charts & latency breakdown)
# └── test-report.md (Markdown summary report)
```
