using Clout.Shared;
using Clout.Shared.Abstractions;
using Clout.Shared.Models;
using Quartz;

namespace Clout.Host.Functions;

internal static class FunctionRegistrationHelper
{
    private static readonly string[] AllowedRuntimes = ["dotnet", ".net", ".net core", "dotnetcore", "netcore", "net"];

    /// <summary>
    /// Checks whether the runtime string is a recognized .NET runtime variant.
    /// </summary>
    public static bool IsValidRuntime(string runtime)
    {
        var rt = runtime.Trim().ToLowerInvariant();
        return AllowedRuntimes.Contains(rt);
    }

    /// <summary>
    /// Copies a stream to a temporary .dll file and returns the path.
    /// </summary>
    public static async Task<string> CopyToTempFileAsync(Stream source, CancellationToken ct)
    {
        var tempPath = Path.Combine(Path.GetTempPath(), $"clout_{Guid.NewGuid():N}.dll");
        await using var outStream = File.Create(tempPath);
        await source.CopyToAsync(outStream, ct).ConfigureAwait(false);
        return tempPath;
    }

    /// <summary>
    /// Safely deletes a temp file, logging a warning on failure instead of silently swallowing.
    /// </summary>
    public static void TryDeleteTempFile(string path, ILogger? logger = null)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to delete temp file: {Path}", path);
        }
    }

    /// <summary>
    /// Builds the standard function metadata list.
    /// </summary>
    public static List<BlobMetadata> BuildFunctionMetadata(
        string name, string entrypoint, string? declaringType,
        string? sourceId = null, string? cron = null)
    {
        var metadata = new List<BlobMetadata>
        {
            new(MetadataKeys.FunctionName, "text/plain", name),
            new(MetadataKeys.FunctionRuntime, "text/plain", ".net core"),
            new(MetadataKeys.FunctionEntrypoint, "text/plain", entrypoint),
            new(MetadataKeys.FunctionDeclaringType, "text/plain", declaringType ?? string.Empty),
            new(MetadataKeys.FunctionVerified, "text/plain", "true")
        };

        if (!string.IsNullOrWhiteSpace(sourceId))
            metadata.Add(new BlobMetadata(MetadataKeys.FunctionSourceId, "text/plain", sourceId));

        if (!string.IsNullOrWhiteSpace(cron))
            metadata.Add(new BlobMetadata(MetadataKeys.TimerTrigger, "text/plain", cron));

        return metadata;
    }

    /// <summary>
    /// Validates the assembly, saves the blob (or an empty placeholder if <paramref name="saveContent"/> is false),
    /// attaches metadata, and optionally schedules the function.
    /// </summary>
    /// <returns>The registered BlobInfo, or null with an error message if validation fails.</returns>
    public static async Task<(BlobInfo? Result, string? Error)> RegisterSingleAsync(
        string tempDllPath,
        string functionName,
        string fileName,
        string contentType,
        IBlobStorage storage,
        IScheduler? scheduler,
        ILogger? logger,
        string? sourceId = null,
        string? cron = null,
        bool saveContent = true,
        CancellationToken ct = default)
    {
        if (!FunctionAssemblyInspector.ContainsPublicMethod(tempDllPath, functionName, out var declaringType))
            return (null, $"Validation failed: could not find a public method named '{functionName}' in the provided assembly.");

        BlobInfo saved;
        if (saveContent)
        {
            await using var dllStream = File.OpenRead(tempDllPath);
            saved = await storage.SaveAsync(fileName, dllStream, contentType, ct).ConfigureAwait(false);
        }
        else
        {
            await using var empty = new MemoryStream(Array.Empty<byte>());
            saved = await storage.SaveAsync(fileName, empty, contentType, ct).ConfigureAwait(false);
        }

        var metadata = BuildFunctionMetadata(functionName, fileName, declaringType, sourceId, cron);
        var updated = await storage.SetMetadataAsync(saved.Id, metadata, ct).ConfigureAwait(false);
        var result = updated ?? saved;

        if (scheduler is not null && !string.IsNullOrWhiteSpace(cron))
        {
            await ScheduleFunctionAsync(scheduler, result.Id, functionName, cron, logger).ConfigureAwait(false);
        }

        return (result, null);
    }

    /// <summary>
    /// Schedules a function with Quartz using the given cron expression.
    /// </summary>
    public static async Task ScheduleFunctionAsync(IScheduler scheduler, string blobId, string functionName, string cron, ILogger? logger = null)
    {
        var jobKey = new JobKey($"function-{blobId}");
        var job = JobBuilder.Create<FunctionInvocationJob>()
            .WithIdentity(jobKey)
            .UsingJobData("blobId", blobId)
            .UsingJobData("functionName", functionName)
            .Build();

        var trigger = TriggerBuilder.Create()
            .WithIdentity($"trigger-{blobId}")
            .ForJob(jobKey)
            .WithCronSchedule(CronHelper.ToQuartzCron(cron))
            .Build();

        await scheduler.ScheduleJob(job, new HashSet<ITrigger> { trigger }, replace: true).ConfigureAwait(false);

        var triggers = await scheduler.GetTriggersOfJob(jobKey).ConfigureAwait(false);
        var next = triggers.FirstOrDefault()?.GetNextFireTimeUtc();
        if (next.HasValue)
            logger?.LogInformation("Scheduled function {Function} ({Id}) next at {Next}", functionName, blobId, next);
    }

    /// <summary>
    /// Unschedules a function job from Quartz.
    /// </summary>
    public static async Task UnscheduleFunctionAsync(IScheduler scheduler, string blobId)
    {
        var jobKey = new JobKey($"function-{blobId}");
        await scheduler.DeleteJob(jobKey).ConfigureAwait(false);
    }
}
