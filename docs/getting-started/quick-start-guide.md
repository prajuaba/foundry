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

# Run all 81 Schema Compiler Unit Tests
$HOME/.dotnet/dotnet test foundry-schema/tests/Foundry.Schema.Compiler.Tests/Foundry.Schema.Compiler.Tests.csproj

# Run all 75 Integration Tests (Requires Docker MongoDB running)
$HOME/.dotnet/dotnet test foundry-integration-tests/Foundry.IntegrationTests.csproj
```

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

---

## 🎨 3. Running Foundry Studio IDE

### Option A: Inside VS Code (Recommended)
1. Install the VSIX extension package:
   ```bash
   code --install-extension /Users/prajuab/Workspace/foundry/foundry-vscode/foundry-vscode-1.0.0.vsix --force
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

## 🧪 4. Running the End-to-End Showcase

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
