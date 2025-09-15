namespace Clout.Shared.Models;

/// <summary>
/// Queue statistics snapshot.
/// </summary>
public sealed record QueueStats(string Name, int MessageCount, long TotalBytes);

