using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Driver;

namespace Foundry.Mongo.Infrastructure;

/// <summary>
/// Utility to execute database operations wrapped with a transient fault retry policy using exponential backoff.
/// </summary>
public static class RetryPolicyHelper
{
    private const int MaxRetryAttempts = 3;
    private const int InitialDelayMs = 100;

    /// <summary>
    /// Executes the specified asynchronous function with retries.
    /// </summary>
    public static async Task<TResult> ExecuteWithRetryAsync<TResult>(Func<Task<TResult>> action, CancellationToken ct = default)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                return await action();
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < MaxRetryAttempts)
            {
                attempt++;
                int delay = InitialDelayMs * (int)Math.Pow(2, attempt);
                await Task.Delay(delay, ct);
            }
        }
    }

    /// <summary>
    /// Executes the specified asynchronous action with retries.
    /// </summary>
    public static async Task ExecuteWithRetryAsync(Func<Task> action, CancellationToken ct = default)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                await action();
                return;
            }
            catch (Exception ex) when (IsTransient(ex) && attempt < MaxRetryAttempts)
            {
                attempt++;
                int delay = InitialDelayMs * (int)Math.Pow(2, attempt);
                await Task.Delay(delay, ct);
            }
        }
    }

    private static bool IsTransient(Exception ex)
    {
        return ex is MongoConnectionException ||
               ex is TimeoutException ||
               ex is IOException ||
               ex.InnerException is SocketException;
    }
}
