using System;

namespace Foundry.Api.Middleware;

/// <summary>
/// Thrown when a request with a duplicate idempotency key is intercepted in the pipeline.
/// </summary>
public class IdempotencyException : Exception
{
    /// <summary>Gets the duplicate idempotency key.</summary>
    public string IdempotencyKey { get; }

    public IdempotencyException(string idempotencyKey, string message) : base(message)
    {
        IdempotencyKey = idempotencyKey;
    }
}
