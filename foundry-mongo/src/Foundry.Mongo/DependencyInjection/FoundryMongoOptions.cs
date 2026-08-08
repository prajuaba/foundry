using System;

namespace Foundry.Mongo.DependencyInjection;

/// <summary>
/// Configurations options used to initialize the Foundry.Mongo data access layer.
/// </summary>
public sealed class FoundryMongoOptions
{
    /// <summary>The MongoDB connection string (e.g. "mongodb://localhost:27017").</summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>The name of the target database.</summary>
    public string DatabaseName { get; set; } = string.Empty;

    /// <summary>Optional 256-bit (32 bytes) Base64 encoded key used for field-level AES encryption.</summary>
    public string? EncryptionKey { get; set; }

    /// <summary>Optional Base64 encoded Data Encryption Key (DEK) encrypted by KMS for envelope encryption.</summary>
    public string? EncryptedEncryptionKey { get; set; }

    /// <summary>Set to true to transparently wrap all IRepository resolutions with CachedRepository.</summary>
    public bool EnableCaching { get; set; } = false;

    /// <summary>The default Time-To-Live (TTL) for cached lookups (defaults to 5 minutes if null).</summary>
    public TimeSpan? DefaultCacheTtl { get; set; }

    /// <summary>Minimum connection pool size for MongoDB client.</summary>
    public int MinConnectionPoolSize { get; set; } = 10;

    /// <summary>Maximum connection pool size for MongoDB client.</summary>
    public int MaxConnectionPoolSize { get; set; } = 100;

    /// <summary>
    /// When true, a repository with no authenticated caller reads every row of an owner-scoped entity,
    /// exactly as it always has. Off by default: an unauthenticated caller now reads zero rows of an
    /// owner-scoped entity instead of every row. Name the trust boundary explicitly here -- only set this
    /// where something in front of the repository genuinely needs an unauthenticated, unscoped read (a
    /// background job constructed without a caller, for example) -- rather than assume it.
    /// </summary>
    public bool AllowUnauthenticatedFullReads { get; set; } = false;
}
