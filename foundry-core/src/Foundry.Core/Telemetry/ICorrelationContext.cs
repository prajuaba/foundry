namespace Foundry.Core.Telemetry;

/// <summary>
/// Provides ambient access to the current request or messaging transaction correlation ID.
/// </summary>
public interface ICorrelationContext
{
    /// <summary>
    /// Gets the current correlation ID.
    /// </summary>
    string CorrelationId { get; }

    /// <summary>
    /// Sets the current correlation ID for the ambient execution scope.
    /// </summary>
    void SetCorrelationId(string correlationId);
}
