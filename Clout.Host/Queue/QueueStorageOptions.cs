namespace Clout.Host.Queue;

/// <summary>
/// Options for the disk-backed queue. Bind from configuration section <c>Queue</c>.
/// </summary>
public class QueueStorageOptions
{
    /// <summary>
    /// Base directory for queue data. If relative, resolved under <see cref="AppContext.BaseDirectory"/>.
    /// </summary>
    public string? BasePath { get; set; }

    /// <summary>
    /// Optional per-queue maximum total bytes. When exceeded, <see cref="Overflow"/> policy applies.
    /// </summary>
    public long? MaxQueueBytes { get; set; }

    /// <summary>
    /// Optional per-queue maximum number of messages. When exceeded, <see cref="Overflow"/> policy applies.
    /// </summary>
    public int? MaxQueueMessages { get; set; }

    /// <summary>
    /// Optional per-message maximum size in bytes. Enqueues larger than this are rejected.
    /// </summary>
    public int? MaxMessageBytes { get; set; }

    /// <summary>
    /// Behavior when an enqueue would exceed queue-level quotas.
    /// </summary>
    public OverflowPolicy Overflow { get; set; } = OverflowPolicy.Reject;

    /// <summary>
    /// If true, removes stray <c>*.bin</c> files not referenced by <c>state.json</c> on startup.
    /// </summary>
    public bool CleanupOrphansOnLoad { get; set; } = true;
}

/// <summary>
/// Overflow handling policy when quotas are exceeded.
/// </summary>
public enum OverflowPolicy
{
    /// <summary>Reject the enqueue that would exceed quotas.</summary>
    Reject,
    /// <summary>Drop oldest messages to make space for the new one.</summary>
    DropOldest
}
