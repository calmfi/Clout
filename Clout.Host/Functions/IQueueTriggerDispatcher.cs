namespace Clout.Host.Functions;

public interface IQueueTriggerDispatcher
{
    Task ActivateAsync(string blobId, string functionName, string queueName, CancellationToken cancellationToken = default);
    Task DeactivateAsync(string blobId, CancellationToken cancellationToken = default);
}
