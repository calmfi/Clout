using Clout.Host.Queue;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Clout.Host.Health;

/// <summary>
/// Health check for queue saturation levels.
/// </summary>
public sealed class QueueSaturationHealthCheck : IHealthCheck
{
    private readonly IAmqpQueueServer _queueServer;
    private readonly ILogger<QueueSaturationHealthCheck> _logger;

    /// <summary>
    /// Saturation threshold (0-1). If exceeded, returns Degraded status.
    /// </summary>
    private const double SaturationWarningThreshold = 0.8; // 80%
    private const double SaturationCriticalThreshold = 0.95; // 95%
    private const long MaxQueueBytesDefault = 1024 * 1024 * 1024; // 1 GB default

    public QueueSaturationHealthCheck(IAmqpQueueServer queueServer, ILogger<QueueSaturationHealthCheck> logger)
    {
        _queueServer = queueServer ?? throw new ArgumentNullException(nameof(queueServer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var stats = _queueServer.GetStats();
            
            if (stats.Count == 0)
            {
                return Task.FromResult(HealthCheckResult.Healthy("No queues configured"));
            }

            var degradedQueues = new List<string>();
            var criticalQueues = new List<string>();
            var details = new Dictionary<string, object>();

            foreach (var stat in stats)
            {
                var saturation = (double)stat.TotalBytes / MaxQueueBytesDefault;
                details[$"queue_{stat.Name}"] = new
                {
                    messageCount = stat.MessageCount,
                    totalBytes = stat.TotalBytes,
                    saturationPercent = Math.Round(saturation * 100, 2)
                };

                if (saturation >= SaturationCriticalThreshold)
                    criticalQueues.Add(stat.Name);
                else if (saturation >= SaturationWarningThreshold)
                    degradedQueues.Add(stat.Name);
            }

            if (criticalQueues.Count > 0)
            {
                var message = $"Queue(s) at critical saturation: {string.Join(", ", criticalQueues)}";
                _logger.LogWarning("Queue saturation critical: {Queues}", criticalQueues);
                return Task.FromResult(HealthCheckResult.Unhealthy(message, data: details));
            }

            if (degradedQueues.Count > 0)
            {
                var message = $"Queue(s) at warning saturation: {string.Join(", ", degradedQueues)}";
                _logger.LogWarning("Queue saturation degraded: {Queues}", degradedQueues);
                return Task.FromResult(HealthCheckResult.Degraded(message, data: details));
            }

            _logger.LogDebug("Queue saturation healthy across {QueueCount} queues", stats.Count);
            return Task.FromResult(HealthCheckResult.Healthy("All queues within acceptable saturation levels", details));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking queue saturation health");
            return Task.FromResult(HealthCheckResult.Unhealthy("Failed to check queue saturation", ex));
        }
    }
}

/// <summary>
/// Health check for system resource availability.
/// </summary>
public sealed class ResourceHealthCheck : IHealthCheck
{
    private readonly ILogger<ResourceHealthCheck> _logger;

    /// <summary>
    /// Available disk space threshold (bytes). Warning if below this.
    /// </summary>
    private const long DiskSpaceWarningThreshold = 100 * 1024 * 1024; // 100 MB

    public ResourceHealthCheck(ILogger<ResourceHealthCheck> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var details = new Dictionary<string, object>();

            // Check disk space
            var tempPath = Path.GetTempPath();
            var drive = new DriveInfo(Path.GetPathRoot(tempPath)!);
            details["diskAvailableBytes"] = drive.AvailableFreeSpace;
            details["diskTotalBytes"] = drive.TotalSize;

            if (drive.AvailableFreeSpace < DiskSpaceWarningThreshold)
            {
                var message = $"Low disk space: {FormatBytes(drive.AvailableFreeSpace)} available";
                _logger.LogWarning("Disk space low: {AvailableSpace}", FormatBytes(drive.AvailableFreeSpace));
                return Task.FromResult(HealthCheckResult.Degraded(message, data: details));
            }

            // Check temp directory exists and is writable
            try
            {
                var testFile = Path.Combine(tempPath, $"clout_health_check_{Guid.NewGuid():N}");
                File.WriteAllText(testFile, "test");
                File.Delete(testFile);
            }
            catch (Exception ex)
            {
                var message = "Temp directory write test failed";
                _logger.LogWarning(ex, "Temp directory not writable");
                return Task.FromResult(HealthCheckResult.Unhealthy(message, ex, details));
            }

            _logger.LogDebug("Resource health check passed. Available disk: {DiskSpace}", FormatBytes(drive.AvailableFreeSpace));
            return Task.FromResult(HealthCheckResult.Healthy("System resources available", details));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking system resources");
            return Task.FromResult(HealthCheckResult.Unhealthy("Failed to check system resources", ex));
        }
    }

    private static string FormatBytes(long bytes) =>
        bytes switch
        {
            < 1024 => $"{bytes} B",
            < 1024 * 1024 => $"{bytes / 1024.0:F2} KB",
            < 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F2} MB",
            _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
        };
}

/// <summary>
/// Extension methods for health check registration.
/// </summary>
public static class HealthCheckExtensions
{
    /// <summary>
    /// Adds Clout-specific health checks.
    /// </summary>
    public static IHealthChecksBuilder AddCloutHealthChecks(this IHealthChecksBuilder builder)
    {
        return builder
            .AddCheck<QueueSaturationHealthCheck>("queue-saturation", HealthStatus.Degraded)
            .AddCheck<ResourceHealthCheck>("resources", HealthStatus.Degraded);
    }
}
