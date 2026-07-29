# 🍃 Foundry Mongo Data Access Layer Documentation

**Foundry.Mongo** is an enterprise-grade MongoDB Data Access Layer built on top of the official MongoDB C# Driver v3.4.

---

## 💡 Capabilities

1. **Generic Repository Abstraction (`IRepository<T, TId>`)**:
   - `GetByIdAsync`, `FindAsync`, `InsertAsync`, `UpdateAsync`, `DeleteAsync`.
2. **Optimistic Concurrency Control (OCC)**:
   - Automated version field checks via `IVersionable` returning `ConcurrencyException` on version mismatch.
3. **KMS Field-Level Encryption & Masking**:
   - `[Encrypt]`: Transparent AES-256-GCM envelope encryption.
   - `[Mask]`, `[MaskEmail]`: Automatic PII masking.
4. **Dynamic Seek Pagination**:
   - High-performance keyset cursor pagination bypassing expensive `Skip()` operations.
5. **Database Migration Runner**:
   - Idempotent migration engine executing versioned `UpAsync` operations using `ReplaceOneAsync` with `IsUpsert = true`.
6. **Hot / Cold Data Partitioning & Archival Worker**:
   - Background worker moving records past their age threshold into year-based cold collections —
     in one transaction on a replica set, and by copy-verify-delete on a standalone. Selection is by
     age alone; soft-deleted records are not archived, and the doc previously said they were.

---

## 📁 Directory Location

```text
foundry-mongo/
└── src/
    └── Foundry.Mongo/
        ├── Foundry.Mongo.csproj
        ├── Repositories/
        ├── Encryption/
        └── Pagination/
```
