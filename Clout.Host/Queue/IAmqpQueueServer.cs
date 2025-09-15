namespace Clout.Host.Queue;

public interface IAmqpQueueServer
{
    void CreateQueue(string name);
    void PurgeQueue(string name);
    ValueTask EnqueueAsync<T>(string name, T message, CancellationToken cancellationToken = default);
    ValueTask<T?> DequeueAsync<T>(string name, CancellationToken cancellationToken = default);
    IReadOnlyList<QueueStats> GetStats();
}

public sealed record QueueStats(string Name, int MessageCount, long TotalBytes);

