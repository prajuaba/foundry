# ⚙️ Foundry Schema Compiler Module Documentation

**Foundry.Schema.Compiler** is a pure **.NET 10 C# Compiler** that reads domain schema JSON manifests and generates strongly-typed C# POCO classes, MongoDB Mappings, and ASP.NET Core API controllers.

---

## 💡 Capabilities

1. **JSON AST Parser**: Deserializes domain entity models, property definitions, enum specs, and custom endpoint declarations.
2. **C# Code Generator**:
   - Generates C# entity POCOs implementing `IEntity<TId>`, `ISoftDelete`, `IVersionable`, `IAuditable`.
   - Generates property attributes: `[BsonId]`, `[BsonElement]`, `[Encrypt]`, `[Mask]`, `[Required]`.
   - Generates repository interfaces (`IOrderRepository`).
   - Generates API Controllers and DTO records.
3. **Command Line Interface (CLI)**:
   ```bash
   dotnet run --project foundry-schema/compiler/Foundry.Schema.Compiler.csproj -- "<input-schema.json>" "<output-directory>"
   ```

---

## 📁 Directory Location

```text
foundry-schema/
├── compiler/
│   ├── Foundry.Schema.Compiler.csproj
│   ├── Program.cs
│   └── Generators/
│       ├── PocoGenerator.cs
│       ├── RepositoryGenerator.cs
│       └── ControllerGenerator.cs
├── backend/
│   └── Foundry.Schema.Backend.csproj
└── tests/
    └── Foundry.Schema.Compiler.Tests/   # 81 Unit Tests
```

---

## ⚙️ Testing Commands

```bash
$HOME/.dotnet/dotnet test foundry-schema/tests/Foundry.Schema.Compiler.Tests/Foundry.Schema.Compiler.Tests.csproj
```
*(81 / 81 Tests Verified Passing)*.
