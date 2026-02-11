using System.Text.Json;
using Clout.Host.Resilience;
using Clout.Shared.Abstractions;
using Clout.Shared.Exceptions;
using Clout.Shared.Validation;

namespace Clout.Host.Functions;

/// <summary>
/// Executes function assemblies loaded from blob storage with comprehensive error handling and cleanup.
/// Includes retry policies for transient I/O failures.
/// </summary>
public sealed class FunctionExecutor
{
    private readonly IBlobStorage _storage;
    private readonly ILogger<FunctionExecutor> _logger;

    public FunctionExecutor(IBlobStorage storage, ILogger<FunctionExecutor> logger)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Executes an async function from a blob assembly.
    /// </summary>
    /// <param name="blobId">Unique identifier for the blob containing the function assembly.</param>
    /// <param name="functionName">Name of the function to execute.</param>
    /// <param name="payload">Optional JSON payload to pass to the function.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <exception cref="BlobNotFoundException">Thrown when the blob is not found.</exception>
    /// <exception cref="FunctionExecutionException">Thrown when function execution fails.</exception>
    public async Task ExecuteAsync(string blobId, string functionName, JsonDocument? payload, CancellationToken cancellationToken)
    {
        CloutValidation.ValidateBlobId(blobId);
        CloutValidation.ValidateFunctionName(functionName);

        var tempFile = string.Empty;

        try
        {
            // Step 1: Open blob stream
            await using var stream = await _storage.OpenReadAsync(blobId, cancellationToken).ConfigureAwait(false);
            if (stream is null)
            {
                _logger.LogWarning("Blob {BlobId} not found for function {Function}", blobId, functionName);
                throw new BlobNotFoundException(blobId);
            }

            // Step 2: Extract to temp file with retry policy
            tempFile = Path.Combine(Path.GetTempPath(), $"clout_fn_{blobId}_{Guid.NewGuid():N}.dll");
            
            try
            {
                await RetryPolicies.ExecuteWithStreamRetryAsync(async () =>
                {
                    await using (var fs = File.Create(tempFile))
                    {
                        await stream.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
                    }
                    return true;
                }, _logger);
                _logger.LogDebug("Extracted function assembly to {TempPath}", tempFile);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to extract function assembly to {TempPath}", tempFile);
                throw new FunctionExecutionException(functionName, blobId, "Failed to extract function assembly from blob.", ex);
            }

            // Step 3: Execute function
            try
            {
                _logger.LogInformation("Executing function {Function} from blob {BlobId}", functionName, blobId);
                await FunctionRunner.RunAsync(tempFile, functionName, payload, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Function {Function} completed successfully for blob {BlobId}", functionName, blobId);
            }
            catch (OperationCanceledException ex)
            {
                _logger.LogWarning(ex, "Function {Function} was cancelled for blob {BlobId}", functionName, blobId);
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Function {Function} execution failed for blob {BlobId}", functionName, blobId);
                throw new FunctionExecutionException(functionName, blobId, $"Function execution failed: {ex.Message}", ex);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (CloutException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error executing function {Function} for blob {BlobId}", functionName, blobId);
            throw new FunctionExecutionException(functionName, blobId, "An unexpected error occurred during function execution.", ex);
        }
        finally
        {
            // Cleanup resources with retry policy
            if (!string.IsNullOrEmpty(tempFile))
            {
                FunctionRegistrationHelper.TryDeleteTempFileWithRetry(tempFile, _logger);
            }
            payload?.Dispose();
        }
    }
}
