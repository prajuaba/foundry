# 🚀 Foundry Developer Quick Start Guide

This guide walks you through setting up your environment, compiling the C# solution, launching the **Foundry Studio IDE**, installing the **VS Code Extension**, and running tests.

---

## 📋 Prerequisites

1. **.NET 10 SDK** (v10.0.301 or higher)
   ```bash
   dotnet --version
   ```
2. **Node.js** (v20+ and npm)
3. **Docker & Docker Compose** (for MongoDB & Kafka test services)
4. **Visual Studio Code** (v1.85+)

---

## 🛠️ 1. Building the C# Solution

From the repository root:

```bash
# Build all .NET projects in the solution
dotnet build

# Run the schema compiler's unit tests
$HOME/.dotnet/dotnet test foundry-schema/tests/Foundry.Schema.Compiler.Tests/Foundry.Schema.Compiler.Tests.csproj

# Run the integration tests (requires the Docker infrastructure below)
$HOME/.dotnet/dotnet test foundry-integration-tests/Foundry.IntegrationTests.csproj

# Or run every suite, which is what CI does
bash scripts/run-tests.sh
```

Test counts are deliberately not quoted here. They were, and they drifted by an order of
magnitude before anyone noticed — the same failure mode as the hardcoded version string
`foundry version` used to print. Run the suites and read the number they report.

---

## 🐳 2. Starting Local Infrastructure

Boot MongoDB, Mongo Express, Kafka, and Kafka UI:

```bash
docker compose up -d
```

| Service | Local Address | Credentials / Usage |
| :--- | :--- | :--- |
| **MongoDB** | `localhost:27017` | Transactional data store |
| **Mongo Express** | `http://localhost:8081` | Web UI database inspector |
| **Kafka Broker** | `localhost:9092` | Event streaming broker |
| **Kafka UI** | `http://localhost:8080` | Web UI topic inspector |
| **OTel Collector** | `localhost:4317` (gRPC), `4318` (HTTP) | Receives traces and metrics from a running app. Development aid only — not part of any deployment. |

---

## 🎨 3. Running Foundry Studio IDE

### Option A: Inside VS Code (Recommended)
1. Install the VSIX extension package:
   ```bash
   # from the repository root, after building the extension (see foundry-vscode/)
   code --install-extension foundry-vscode/foundry-vscode-1.0.0.vsix --force
   ```
2. Open VS Code.
3. Press `Cmd + Shift + P` ➔ **`Foundry: Create New Schema Manifest`** *(or open any `.foundry.json` file)*.

### Option B: Standalone Web Application
1. Launch the Vite dev server:
   ```bash
   cd foundry-studio
   npm run dev
   ```
2. Open your browser to `http://localhost:5173`.

---

## 📦 4. What `foundry new` Gives You

A scaffolded application arrives wired for operation, not just for CRUD:

| Capability | How to see it |
| :--- | :--- |
| **Health endpoint** | `curl localhost:5000/api/health` — unauthenticated by design, since an orchestrator's probe carries no token. Returns `200 Healthy`, or `503 Unhealthy` when MongoDB is unreachable. |
| **Durable audit trail** | Every mutation writes an entry to the `audit_log` collection, attributed to the caller's token subject. Inspect it in Mongo Express. |
| **Traces and metrics** | Set `OTEL_EXPORTER_OTLP_ENDPOINT=http://localhost:4317` and watch spans arrive with `docker logs foundry-otel-collector`. Without that variable the app collects telemetry and exports nothing, deliberately. |
| **Container image** | `dotnet publish -c Release -o publish && docker build -t myapp .` — the generated Dockerfile packages the published output. Its header explains why it does not build from source. |

Tokens for local use come from the CLI, which signs with the same key the app validates against:

```bash
foundry token mint --signing-key "$(...)" --issuer MyApp --audience MyApp
```

---

## 🧪 5. Running the End-to-End Showcase

Run the comprehensive E2E application demonstrating all Foundry framework layers:

```bash
dotnet run --project samples/Foundry.E2E.Showcase/Foundry.E2E.Showcase.csproj
```

This application demonstrates:
- Domain schema compilation and POCO generation
- KMS Envelope Encryption (AES-256 field protection)
- MongoDB OCC (Optimistic Concurrency Control) & dynamic seek pagination
- MediatR pipeline behaviors (RBAC, Caching, Audit, Validation)
- Real-time SignalR / WebSockets notification hub dispatch
