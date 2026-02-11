using Polly;

namespace Clout.Host.Resilience;

/// <summary>
/// Centralized Polly retry and resilience policies for I/O operations across the Clout system.
/// </summary>
public static class RetryPolicies
{
    /// <summary>
    /// Retry policy for transient file I/O errors with exponential backoff.
    /// Retries up to 3 times with delays of 100ms, 200ms, 400ms.
    /// </summary>
    public static IAsyncPolicy<T> FileIoRetryPolicy<T>() =>
        Policy<T>
            .Handle<IOException>()
            .Or<UnauthorizedAccessException>()
            .OrInner<TimeoutException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt => TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt - 1)),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    // Logging is done at the call site for better context
                });

    /// <summary>
    /// Retry policy for JSON serialization/deserialization with exponential backoff.
    /// Retries up to 2 times with delays of 50ms, 100ms.
    /// </summary>
    public static IAsyncPolicy<T> JsonRetryPolicy<T>() =>
        Policy<T>
            .Handle<System.Text.Json.JsonException>()
            .OrInner<IOException>()
            .WaitAndRetryAsync(
                retryCount: 2,
                sleepDurationProvider: attempt => TimeSpan.FromMilliseconds(50 * Math.Pow(2, attempt - 1)),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    // Logging is done at the call site for better context
                });

    /// <summary>
    /// Retry policy for stream operations with exponential backoff.
    /// Retries up to 3 times with delays of 100ms, 200ms, 400ms.
    /// Handles common stream-related exceptions.
    /// </summary>
    public static IAsyncPolicy<T> StreamRetryPolicy<T>() =>
        Policy<T>
            .Handle<IOException>()
            .Or<ObjectDisposedException>()
            .Or<TimeoutException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt => TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt - 1)),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    // Logging is done at the call site for better context
                });

    /// <summary>
    /// Creates a retry policy for async void operations with exponential backoff.
    /// Retries up to 3 times with delays of 100ms, 200ms, 400ms.
    /// </summary>
    public static IAsyncPolicy AsyncRetryPolicy() =>
        Policy
            .Handle<IOException>()
            .Or<UnauthorizedAccessException>()
            .OrInner<TimeoutException>()
            .WaitAndRetryAsync(
                retryCount: 3,
                sleepDurationProvider: attempt => TimeSpan.FromMilliseconds(100 * Math.Pow(2, attempt - 1)),
                onRetry: (outcome, timespan, retryCount, context) =>
                {
                    // Logging is done at the call site for better context
                });

    /// <summary>
    /// Helper method to execute an async function with file I/O retry policy.
    /// </summary>
    public static async Task<T> ExecuteWithFileIoRetryAsync<T>(
        Func<Task<T>> operation,
        ILogger? logger = null)
    {
        try
        {
            return await FileIoRetryPolicy<T>().ExecuteAsync(operation);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "File I/O operation failed after retries");
            throw;
        }
    }

    /// <summary>
    /// Helper method to execute an async function with stream retry policy.
    /// </summary>
    public static async Task<T> ExecuteWithStreamRetryAsync<T>(
        Func<Task<T>> operation,
        ILogger? logger = null)
    {
        try
        {
            return await StreamRetryPolicy<T>().ExecuteAsync(operation);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Stream operation failed after retries");
            throw;
        }
    }

    /// <summary>
    /// Helper method to execute an async function with JSON retry policy.
    /// </summary>
    public static async Task<T> ExecuteWithJsonRetryAsync<T>(
        Func<Task<T>> operation,
        ILogger? logger = null)
    {
        try
        {
            return await JsonRetryPolicy<T>().ExecuteAsync(operation);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "JSON operation failed after retries");
            throw;
        }
    }

    /// <summary>
    /// Helper method to execute an async void operation with retry policy.
    /// </summary>
    public static async Task ExecuteWithRetryAsync(
        Func<Task> operation,
        ILogger? logger = null)
    {
        try
        {
            await AsyncRetryPolicy().ExecuteAsync(operation);
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Async operation failed after retries");
            throw;
        }
    }
}
