using System.Text.Json;
using Clout.Shared.Abstractions;

namespace Clout.Host.Functions;

public sealed class FunctionExecutor
{
    private readonly IBlobStorage _storage;
    private readonly ILogger<FunctionExecutor> _logger;

    public FunctionExecutor(IBlobStorage storage, ILogger<FunctionExecutor> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    public async Task ExecuteAsync(string blobId, string functionName, JsonDocument? payload, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await _storage.OpenReadAsync(blobId, cancellationToken).ConfigureAwait(false);
            if (stream is null)
            {
                _logger.LogWarning("Blob {BlobId} not found for function {Function}", blobId, functionName);
                return;
            }

            var temp = Path.Combine(Path.GetTempPath(), $"clout_fn_{blobId}_{Guid.NewGuid():N}.dll");
            await using (var fs = File.Create(temp))
            {
                await stream.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
            }

            try
            {
                await FunctionRunner.RunAsync(temp, functionName, payload, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Function {Function} completed for blob {BlobId}", functionName, blobId);
            }
            finally
            {
                FunctionRegistrationHelper.TryDeleteTempFile(temp, _logger);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Function {Function} failed for blob {BlobId}", functionName, blobId);
        }
        finally
        {
            payload?.Dispose();
        }
    }
}
