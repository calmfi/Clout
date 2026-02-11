using System.ComponentModel.DataAnnotations;

namespace Clout.Host.Configuration;

/// <summary>
/// Configuration for blob storage settings.
/// </summary>
public sealed class BlobStorageOptions
{
    /// <summary>
    /// Root directory for storing blobs. If relative, resolved under AppContext.BaseDirectory.
    /// </summary>
    [Required(ErrorMessage = "Blob storage root path is required")]
    public string? RootPath { get; set; } = "storage";

    /// <summary>
    /// Maximum size (in bytes) for a single blob. Default: 100 MB.
    /// </summary>
    [Range(1024, long.MaxValue, ErrorMessage = "Max blob size must be at least 1 KB")]
    public long MaxBlobBytes { get; set; } = 100 * 1024 * 1024; // 100 MB

    /// <summary>
    /// Enable compression for stored blobs.
    /// </summary>
    public bool EnableCompression { get; set; }
}

/// <summary>
/// Configuration for function execution settings.
/// </summary>
public sealed class FunctionExecutionOptions
{
    /// <summary>
    /// Maximum timeout (in seconds) for a single function execution.
    /// </summary>
    [Range(1, 3600, ErrorMessage = "Function timeout must be between 1 and 3600 seconds")]
    public int ExecutionTimeoutSeconds { get; set; } = 300; // 5 minutes

    /// <summary>
    /// Enable parallel function execution.
    /// </summary>
    public bool EnableParallelExecution { get; set; } = true;

    /// <summary>
    /// Maximum number of concurrent function executions.
    /// </summary>
    [Range(1, 100, ErrorMessage = "Max concurrent functions must be between 1 and 100")]
    public int MaxConcurrentExecutions { get; set; } = 5;
}

/// <summary>
/// Configuration for temporary file cleanup.
/// </summary>
public sealed class TempFileCleanupOptions
{
    /// <summary>
    /// Interval (in seconds) between cleanup runs.
    /// </summary>
    [Range(60, 3600, ErrorMessage = "Cleanup interval must be between 60 and 3600 seconds")]
    public int CleanupIntervalSeconds { get; set; } = 300; // 5 minutes

    /// <summary>
    /// Minimum age (in seconds) for a file to be considered for cleanup.
    /// </summary>
    [Range(60, 3600, ErrorMessage = "File age threshold must be between 60 and 3600 seconds")]
    public int FileAgeThresholdSeconds { get; set; } = 600; // 10 minutes

    /// <summary>
    /// Enable automatic cleanup of orphaned temp files.
    /// </summary>
    public bool EnableAutoCleanup { get; set; } = true;
}

/// <summary>
/// Configuration for logging and diagnostics.
/// </summary>
public sealed class DiagnosticsOptions
{
    /// <summary>
    /// Enable detailed request/response logging.
    /// </summary>
    public bool EnableDetailedLogging { get; set; }

    /// <summary>
    /// Enable performance metrics collection.
    /// </summary>
    public bool EnableMetrics { get; set; } = true;

    /// <summary>
    /// Enable distributed tracing.
    /// </summary>
    public bool EnableTracing { get; set; } = true;

    /// <summary>
    /// Log sensitive data (use with caution in production).
    /// </summary>
    public bool LogSensitiveData { get; set; }
}
