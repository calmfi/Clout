using Polly;
using Polly.CircuitBreaker;

namespace Clout.Host.Resilience;

/// <summary>
/// Factory for creating Polly resilience policies for Clout operations.
/// </summary>
public static class ResiliencePolicies
{
    /// <summary>
    /// Creates a retry policy for transient I/O failures.
    /// Retries up to 3 times with exponential backoff.
    /// </summary>
    public static IAsyncPolicy<T> CreateFileOperationRetryPolicy<T>(ILogger? logger = null)
    {
        return Policy<T>
            .Handle<IOException>()
            .Or<UnauthorizedAccessException>()
            .OrInner<TimeoutException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt => TimeSpan.FromMilliseconds(Math.Pow(2, attempt) * 100),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    logger?.LogWarning(
                        "File operation failed, retrying in {Delay}ms (Attempt {RetryCount}/3): {Exception}",
                        timespan.TotalMilliseconds,
                        retryCount,
                        outcome.Exception?.Message);
                });
    }

    /// <summary>
    /// Creates a retry policy specifically for blob read operations.
    /// </summary>
    public static IAsyncPolicy<T> CreateBlobReadRetryPolicy<T>(ILogger? logger = null)
    {
        return CreateFileOperationRetryPolicy<T>(logger);
    }

    /// <summary>
    /// Creates a retry policy specifically for blob write operations.
    /// </summary>
    public static IAsyncPolicy<T> CreateBlobWriteRetryPolicy<T>(ILogger? logger = null)
    {
        return CreateFileOperationRetryPolicy<T>(logger);
    }

    /// <summary>
    /// Creates a circuit breaker policy for queue operations.
    /// Opens after 3 consecutive failures for 10 seconds.
    /// </summary>
    public static IAsyncPolicy<T> CreateQueueCircuitBreakerPolicy<T>(ILogger? logger = null)
    {
        return Policy
            .Handle<InvalidOperationException>()
            .Or<IOException>()
            .OrResult<T>(r => r == null)
            .CircuitBreakerAsync<T>(
                handledEventsAllowedBeforeBreaking: 3,
                durationOfBreak: TimeSpan.FromSeconds(10),
                onBreak: (outcome, timespan) =>
                {
                    logger?.LogError(
                        "Queue circuit breaker opened for {Duration}ms due to: {Exception}",
                        timespan.TotalMilliseconds,
                        outcome.Exception?.Message ?? "null result");
                },
                onReset: () =>
                {
                    logger?.LogInformation("Queue circuit breaker reset");
                },
                onHalfOpen: () =>
                {
                    logger?.LogInformation("Queue circuit breaker half-open, testing recovery");
                });
    }

    /// <summary>
    /// Creates a timeout policy for long-running operations.
    /// Timeout is set to 30 seconds.
    /// </summary>
    public static IAsyncPolicy<T> CreateTimeoutPolicy<T>(ILogger? logger = null, TimeSpan? timeout = null)
    {
        var timeoutDuration = timeout ?? TimeSpan.FromSeconds(30);
        return Policy
            .TimeoutAsync<T>(
                timeoutDuration,
                onTimeoutAsync: (context, timespan, task) =>
                {
                    logger?.LogWarning("Operation timed out after {Timeout}ms", timespan.TotalMilliseconds);
                    return Task.CompletedTask;
                });
    }

    /// <summary>
    /// Creates a combined policy with retry, circuit breaker, and timeout.
    /// </summary>
    public static IAsyncPolicy<T> CreateCombinedPolicy<T>(ILogger? logger = null)
    {
        var retryPolicy = CreateFileOperationRetryPolicy<T>(logger);
        var circuitBreakerPolicy = CreateQueueCircuitBreakerPolicy<T>(logger);
        var timeoutPolicy = CreateTimeoutPolicy<T>(logger);

        // Wrap policies: timeout -> circuit breaker -> retry
        return Policy.WrapAsync(timeoutPolicy, circuitBreakerPolicy, retryPolicy);
    }
}

/// <summary>
/// Resilience context utilities for tracking and logging policy decisions.
/// </summary>
public static class ResilienceContext
{
    /// <summary>
    /// Creates a context with operation metadata for logging.
    /// </summary>
    public static Polly.Context CreateOperationContext(string operationName, string? blobId = null, string? queueName = null)
    {
        var context = new Polly.Context
        {
            { "operation", operationName }
        };

        if (!string.IsNullOrEmpty(blobId))
            context["blobId"] = blobId;

        if (!string.IsNullOrEmpty(queueName))
            context["queueName"] = queueName;

        return context;
    }
}
