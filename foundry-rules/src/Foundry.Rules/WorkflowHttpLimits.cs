namespace Foundry.Rules;

/// <summary>
/// Bounds on what an external workflow action may bring back.
/// </summary>
/// <remarks>
/// Two different limits, because they protect two different things. The buffer limit protects this
/// process from reading an unbounded reply into memory; the stored limit protects the activity log,
/// which is a MongoDB document with a 16 MB ceiling and is read by people rather than machines. A
/// response can legitimately be larger than anything worth recording.
/// </remarks>
public static class WorkflowHttpLimits
{
    /// <summary>Client used for methods HTTP defines as safe to repeat.</summary>
    public const string RetryingClientName = "FoundryWorkflow";

    /// <summary>Client used for methods that must not be repeated, such as POST and PATCH.</summary>
    public const string SingleAttemptClientName = "FoundryWorkflow.NoRetry";

    /// <summary>
    /// Whether HTTP says this method may be repeated without changing the outcome.
    /// </summary>
    /// <remarks>
    /// PUT and DELETE are idempotent by definition even though they mutate; POST and PATCH are not.
    /// An unrecognised method is treated as unsafe, so a custom verb does not get retries by accident.
    /// </remarks>
    public static bool IsSafeToRepeat(string? method)
        => method?.ToUpperInvariant() switch
        {
            "GET" or "HEAD" or "OPTIONS" or "TRACE" or "PUT" or "DELETE" => true,
            _ => false
        };

    /// <summary>The client an action using <paramref name="method"/> should be sent on.</summary>
    public static string ClientNameFor(string? method)
        => IsSafeToRepeat(method) ? RetryingClientName : SingleAttemptClientName;

    /// <summary>Largest response body the workflow client will read at all.</summary>
    public const long MaxResponseBytes = 1024 * 1024;

    /// <summary>Largest response body kept in the activity log.</summary>
    public const int MaxRecordedResponseCharacters = 8 * 1024;

    /// <summary>
    /// Trims a response body to what is worth recording, saying so when it trims.
    /// </summary>
    /// <remarks>
    /// The marker matters more than the trimming: a silently truncated body reads as a complete one,
    /// and someone diagnosing a failed action would draw conclusions from a reply that is missing its
    /// end.
    /// </remarks>
    public static string ForRecording(string? body)
    {
        if (string.IsNullOrEmpty(body)) return string.Empty;
        if (body.Length <= MaxRecordedResponseCharacters) return body;

        var omitted = body.Length - MaxRecordedResponseCharacters;
        return body[..MaxRecordedResponseCharacters]
            + $"\n… [truncated for the activity log: {omitted} more character(s) received]";
    }
}
