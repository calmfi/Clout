using Microsoft.Extensions.Options;

namespace Clout.Host.Configuration;

/// <summary>
/// Validates Clout configuration options at startup.
/// </summary>
public sealed class CloutConfigurationValidator : IValidateOptions<BlobStorageOptions>,
    IValidateOptions<FunctionExecutionOptions>,
    IValidateOptions<TempFileCleanupOptions>,
    IValidateOptions<DiagnosticsOptions>
{
    private readonly ILogger<CloutConfigurationValidator> _logger;

    public CloutConfigurationValidator(ILogger<CloutConfigurationValidator> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public ValidateOptionsResult Validate(string? name, BlobStorageOptions options)
    {
        if (options is null)
            return ValidateOptionsResult.Fail("BlobStorageOptions cannot be null");

        if (string.IsNullOrWhiteSpace(options.RootPath))
            return ValidateOptionsResult.Fail("Blob storage root path cannot be empty");

        if (options.MaxBlobBytes < 1024)
            return ValidateOptionsResult.Fail("MaxBlobBytes must be at least 1 KB (1024 bytes)");

        _logger.LogInformation("BlobStorageOptions validated: MaxBlobBytes={MaxBytes}", options.MaxBlobBytes);
        return ValidateOptionsResult.Success;
    }

    public ValidateOptionsResult Validate(string? name, FunctionExecutionOptions options)
    {
        if (options is null)
            return ValidateOptionsResult.Fail("FunctionExecutionOptions cannot be null");

        if (options.ExecutionTimeoutSeconds < 1 || options.ExecutionTimeoutSeconds > 3600)
            return ValidateOptionsResult.Fail("ExecutionTimeoutSeconds must be between 1 and 3600");

        if (options.MaxConcurrentExecutions < 1 || options.MaxConcurrentExecutions > 100)
            return ValidateOptionsResult.Fail("MaxConcurrentExecutions must be between 1 and 100");

        _logger.LogInformation("FunctionExecutionOptions validated: Timeout={Timeout}s, MaxConcurrent={MaxConcurrent}",
            options.ExecutionTimeoutSeconds, options.MaxConcurrentExecutions);
        return ValidateOptionsResult.Success;
    }

    public ValidateOptionsResult Validate(string? name, TempFileCleanupOptions options)
    {
        if (options is null)
            return ValidateOptionsResult.Fail("TempFileCleanupOptions cannot be null");

        if (options.CleanupIntervalSeconds < 60 || options.CleanupIntervalSeconds > 3600)
            return ValidateOptionsResult.Fail("CleanupIntervalSeconds must be between 60 and 3600");

        if (options.FileAgeThresholdSeconds < 60 || options.FileAgeThresholdSeconds > 3600)
            return ValidateOptionsResult.Fail("FileAgeThresholdSeconds must be between 60 and 3600");

        _logger.LogInformation("TempFileCleanupOptions validated: Interval={Interval}s, FileAge={FileAge}s",
            options.CleanupIntervalSeconds, options.FileAgeThresholdSeconds);
        return ValidateOptionsResult.Success;
    }

    public ValidateOptionsResult Validate(string? name, DiagnosticsOptions options)
    {
        if (options is null)
            return ValidateOptionsResult.Fail("DiagnosticsOptions cannot be null");

        var enabledFeatures = new List<string>();
        if (options.EnableDetailedLogging) enabledFeatures.Add("DetailedLogging");
        if (options.EnableMetrics) enabledFeatures.Add("Metrics");
        if (options.EnableTracing) enabledFeatures.Add("Tracing");

        _logger.LogInformation("DiagnosticsOptions validated: Enabled features: {Features}",
            string.Join(", ", enabledFeatures));
        return ValidateOptionsResult.Success;
    }
}
