using Quartz;

namespace Clout.Host.Functions;

public sealed class FunctionInvocationJob : IJob
{
    private readonly FunctionExecutor _executor;
    private readonly ILogger<FunctionInvocationJob> _logger;

    public FunctionInvocationJob(FunctionExecutor executor, ILogger<FunctionInvocationJob> logger)
    {
        _executor = executor;
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
            await _executor.ExecuteAsync(blobId!, functionName!, null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cancellation was requested, no-op.
        }
    }
}
