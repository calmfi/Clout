using Cloud.Shared.Abstractions;
using Quartz;

namespace Clout.Host.Functions;

internal sealed class FunctionInvocationJob : IJob
{
    private readonly IBlobStorage _storage;
    private readonly ILogger<FunctionInvocationJob> _logger;

    public FunctionInvocationJob(IBlobStorage storage, ILogger<FunctionInvocationJob> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        var data = context.MergedJobDataMap;
        var blobId = data.GetString("blobId");
        var functionName = data.GetString("functionName");
        if (string.IsNullOrWhiteSpace(blobId) || string.IsNullOrWhiteSpace(functionName))
        {
            _logger.LogWarning("FunctionInvocationJob missing parameters: blobId={BlobId}, functionName={Function}", blobId, functionName);
            return;
        }

        try
        {
            await using var stream = await _storage.OpenReadAsync(blobId, ct);
            if (stream is null)
            {
                _logger.LogWarning("Blob {BlobId} not found for function {Function}", blobId, functionName);
                return;
            }

            var temp = Path.Combine(Path.GetTempPath(), $"clout_fn_{blobId}_{Guid.NewGuid():N}.dll");
            await using (var fs = File.Create(temp))
            {
                await stream.CopyToAsync(fs, ct);
            }

            try
            {
                await FunctionRunner.RunAsync(temp, functionName, ct);
                _logger.LogInformation("Function {Function} completed for blob {BlobId}", functionName, blobId);
            }
            finally
            {
                try { File.Delete(temp); } catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Function {Function} failed for blob {BlobId}", functionName, blobId);
        }
    }
}
