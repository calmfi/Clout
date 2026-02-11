using System.Text.Json;
using Clout.Host.Queue;
using Clout.Shared;
using Clout.Shared.Abstractions;
using Clout.Shared.Models;

namespace Clout.Host.Functions;

public sealed class QueueTriggerDispatcher : BackgroundService, IQueueTriggerDispatcher
{
    private readonly IAmqpQueueServer _queueServer;
    private readonly IBlobStorage _storage;
    private readonly FunctionExecutor _executor;
    private readonly ILogger<QueueTriggerDispatcher> _logger;
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private readonly Dictionary<string, QueueWorker> _workers = new(StringComparer.OrdinalIgnoreCase);
    private CancellationToken _shutdown = CancellationToken.None;

    public QueueTriggerDispatcher(IAmqpQueueServer queueServer, IBlobStorage storage, FunctionExecutor executor, ILogger<QueueTriggerDispatcher> logger)
    {
        _queueServer = queueServer;
        _storage = storage;
        _executor = executor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _shutdown = stoppingToken;
        await InitializeExistingAsync(stoppingToken).ConfigureAwait(false);
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // host is stopping
        }
    }

    public Task ActivateAsync(string blobId, string functionName, string queueName, CancellationToken cancellationToken = default)
        => ActivateInternalAsync(blobId, functionName, queueName, cancellationToken);

    public async Task DeactivateAsync(string blobId, CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_workers.Remove(blobId, out var worker))
            {
                worker.Cancel();
                try
                {
                    await worker.WaitAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // expected during shutdown
                }
                worker.Dispose();
            }
        }
        finally
        {
            _mutex.Release();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken).ConfigureAwait(false);

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tasks = new List<Task>(_workers.Count);
            foreach (var worker in _workers.Values)
            {
                worker.Cancel();
                tasks.Add(worker.WaitSafeAsync());
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);

            foreach (var worker in _workers.Values)
            {
                worker.Dispose();
            }
            _workers.Clear();
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task InitializeExistingAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<BlobInfo> blobs;
        try
        {
            blobs = await _storage.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to enumerate blobs for queue trigger initialization.");
            return;
        }

        foreach (var blob in blobs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var queue = blob.Metadata?.FirstOrDefault(m => string.Equals(m.Name, MetadataKeys.QueueTrigger, StringComparison.OrdinalIgnoreCase))?.Value;
            if (string.IsNullOrWhiteSpace(queue)) continue;
            var functionName = blob.Metadata?.FirstOrDefault(m => string.Equals(m.Name, MetadataKeys.FunctionName, StringComparison.OrdinalIgnoreCase))?.Value;
            if (string.IsNullOrWhiteSpace(functionName)) continue;

            try
            {
                await ActivateInternalAsync(blob.Id, functionName!, queue!, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to activate queue trigger for blob {BlobId} ({Function})", blob.Id, functionName);
            }
        }
    }

    private async Task ActivateInternalAsync(string blobId, string functionName, string queueName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(functionName)) throw new ArgumentException("Function name is required.", nameof(functionName));
        if (string.IsNullOrWhiteSpace(queueName)) throw new ArgumentException("Queue name is required.", nameof(queueName));
        queueName = queueName.Trim();

        await _mutex.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_workers.Remove(blobId, out var existing))
            {
                existing.Cancel();
                await existing.WaitSafeAsync().ConfigureAwait(false);
                existing.Dispose();
            }

            _queueServer.CreateQueue(queueName);
            var worker = new QueueWorker(blobId, functionName, queueName);
            worker.Start(Task.Run(() => RunWorkerAsync(worker), CancellationToken.None));
            _workers[blobId] = worker;
            _logger.LogInformation("Queue trigger bound: {Function} ({BlobId}) <= {Queue}", functionName, blobId, queueName);
        }
        finally
        {
            _mutex.Release();
        }
    }

    private async Task RunWorkerAsync(QueueWorker worker)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(worker.Token, _shutdown);
        var token = linked.Token;

        while (!token.IsCancellationRequested)
        {
            token.ThrowIfCancellationRequested();
            JsonDocument? payload = null;
            try
            {
                payload = await _queueServer.DequeueAsync<JsonDocument>(worker.QueueName, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error while dequeuing from {Queue} for function {Function} ({BlobId})", worker.QueueName, worker.FunctionName, worker.BlobId);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                continue;
            }

            if (payload is null)
            {
                continue;
            }

            try
            {
                await _executor.ExecuteAsync(worker.BlobId, worker.FunctionName, payload, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Queue triggered function {Function} ({BlobId}) failed for payload from {Queue}", worker.FunctionName, worker.BlobId, worker.QueueName);
            }
        }
    }

    private sealed class QueueWorker : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private Task? _execution;

        public QueueWorker(string blobId, string functionName, string queueName)
        {
            BlobId = blobId;
            FunctionName = functionName;
            QueueName = queueName;
        }

        public string BlobId { get; }
        public string FunctionName { get; }
        public string QueueName { get; }
        public CancellationToken Token => _cts.Token;

        public void Start(Task task)
        {
            _execution = task;
        }

        public void Cancel() => _cts.Cancel();

        public async Task WaitAsync()
        {
            if (_execution is null) return;
            await _execution.ConfigureAwait(false);
        }

        public async Task WaitSafeAsync()
        {
            if (_execution is null) return;
            try
            {
                await _execution.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected when cancellation requested
            }
        }

        public void Dispose()
        {
            _cts.Dispose();
        }
    }
}





