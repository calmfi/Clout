using System.Diagnostics;

namespace Clout.Host.Functions;

/// <summary>
/// Background service that periodically cleans up orphaned temporary files created by function execution.
/// </summary>
public sealed class TempFileCleanupService : BackgroundService
{
    private readonly ILogger<TempFileCleanupService> _logger;
    private readonly TimeSpan _cleanupInterval;
    private readonly TimeSpan _fileAgeThreshold;

    /// <summary>
    /// Initializes a new instance of the TempFileCleanupService.
    /// </summary>
    /// <param name="logger">Logger for cleanup operations.</param>
    /// <param name="cleanupInterval">Interval between cleanup runs (default: 5 minutes).</param>
    /// <param name="fileAgeThreshold">Minimum age of file before cleanup (default: 10 minutes).</param>
    public TempFileCleanupService(
        ILogger<TempFileCleanupService> logger,
        TimeSpan? cleanupInterval = null,
        TimeSpan? fileAgeThreshold = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _cleanupInterval = cleanupInterval ?? TimeSpan.FromMinutes(5);
        _fileAgeThreshold = fileAgeThreshold ?? TimeSpan.FromMinutes(10);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TempFileCleanupService started with cleanup interval {Interval} and file age threshold {Threshold}",
            _cleanupInterval.TotalSeconds, _fileAgeThreshold.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_cleanupInterval, stoppingToken).ConfigureAwait(false);
                await CleanupOrphanedFilesAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when service is stopping
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during temporary file cleanup");
            }
        }

        _logger.LogInformation("TempFileCleanupService stopped");
    }

    /// <summary>
    /// Cleans up orphaned temporary files matching the Clout naming pattern.
    /// </summary>
    private async Task CleanupOrphanedFilesAsync(CancellationToken cancellationToken)
    {
        var tempPath = Path.GetTempPath();
        var cutoffTime = DateTime.UtcNow.Subtract(_fileAgeThreshold);
        var cleanupCount = 0;
        var failureCount = 0;
        long totalBytesFreed = 0;

        try
        {
            var cloutFiles = Directory.EnumerateFiles(tempPath, "clout_fn_*.dll");
            
            foreach (var file in cloutFiles)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var fileInfo = new FileInfo(file);
                    if (fileInfo.LastWriteTimeUtc < cutoffTime)
                    {
                        totalBytesFreed += fileInfo.Length;
                        File.Delete(file);
                        cleanupCount++;
                        _logger.LogDebug("Cleaned up orphaned function assembly at {FilePath}", file);
                    }
                }
                catch (Exception ex)
                {
                    failureCount++;
                    _logger.LogWarning(ex, "Failed to delete orphaned function assembly at {FilePath}", file);
                }
            }

            if (cleanupCount > 0 || failureCount > 0)
            {
                _logger.LogInformation(
                    "Temp file cleanup completed: {DeletedCount} files deleted ({BytesFreed} bytes), {FailureCount} failures",
                    cleanupCount, totalBytesFreed, failureCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Critical error during temp file cleanup scan");
        }
    }
}
