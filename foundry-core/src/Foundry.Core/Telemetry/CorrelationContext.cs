using System;
using System.Threading;

namespace Foundry.Core.Telemetry;

/// <summary>
/// Default ambient AsyncLocal implementation of <see cref="ICorrelationContext"/>.
/// </summary>
public class CorrelationContext : ICorrelationContext
{
    private static readonly AsyncLocal<string?> CurrentCorrelationId = new();

    /// <inheritdoc/>
    public string CorrelationId
    {
        get => CurrentCorrelationId.Value ??= Guid.NewGuid().ToString("N");
    }

    /// <inheritdoc/>
    public void SetCorrelationId(string correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            throw new ArgumentException("Correlation ID cannot be null or empty.", nameof(correlationId));
        }

        CurrentCorrelationId.Value = correlationId;
    }
}
