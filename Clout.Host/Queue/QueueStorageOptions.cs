namespace Clout.Host.Queue;

public class QueueStorageOptions
{
    // If relative, it is resolved against AppContext.BaseDirectory.
    public string? BasePath { get; set; }

    // Per-queue quotas
    public long? MaxQueueBytes { get; set; }
    public int? MaxQueueMessages { get; set; }
    public int? MaxMessageBytes { get; set; }

    // How to behave when an enqueue would exceed quotas
    public OverflowPolicy Overflow { get; set; } = OverflowPolicy.Reject;

    // Clean up stray *.bin files not referenced by state.json
    public bool CleanupOrphansOnLoad { get; set; } = true;
}

public enum OverflowPolicy
{
    Reject,
    DropOldest
}

