using Cloud.Shared;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NCrontab;

namespace Clout.Api.Functions;

/// <summary>
/// Background service that discovers function blobs with a TimerTrigger NCRONTAB expression
/// and invokes their function method at scheduled times.
/// </summary>
internal sealed class FunctionTimerService : BackgroundService
{
    private readonly IBlobStorage _storage;
    private readonly ILogger<FunctionTimerService> _logger;

    // Tracks next scheduled run per blob id
    private readonly Dictionary<string, DateTimeOffset> _nextRuns = new(StringComparer.OrdinalIgnoreCase);

    public FunctionTimerService(IBlobStorage storage, ILogger<FunctionTimerService> logger)
    {
        _storage = storage;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Polling loop — simple and robust. Could be replaced with per-function timers later.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ScanAndRunAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FunctionTimerService tick failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task ScanAndRunAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var blobs = await _storage.ListAsync(ct);

        foreach (var blob in blobs)
        {
            ct.ThrowIfCancellationRequested();

            // Must be a registered function with a TimerTrigger
            var meta = blob.Metadata;
            var verified = meta.FirstOrDefault(m => string.Equals(m.Name, "function.verified", StringComparison.OrdinalIgnoreCase))?.Value;
            var funcName = meta.FirstOrDefault(m => string.Equals(m.Name, "function.name", StringComparison.OrdinalIgnoreCase))?.Value;
            var cronExpr = meta.FirstOrDefault(m => string.Equals(m.Name, "TimerTrigger", StringComparison.OrdinalIgnoreCase))?.Value;
            if (!string.Equals(verified, "true", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(funcName) || string.IsNullOrWhiteSpace(cronExpr))
            {
                continue;
            }

            // Parse NCRONTAB expression
            CrontabSchedule? schedule;
            try
            {
                schedule = TryParseSchedule(cronExpr);
                if (schedule is null) throw new FormatException("Invalid cron format");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Invalid TimerTrigger for blob {Id}: '{Expr}'", blob.Id, cronExpr);
                continue;
            }

            // Determine next run
            if (!_nextRuns.TryGetValue(blob.Id, out var next))
            {
                var n = schedule.GetNextOccurrence(now.UtcDateTime);
                next = new DateTimeOffset(n, TimeSpan.Zero);
                _nextRuns[blob.Id] = next;
            }

            if (now >= next)
            {
                _ = RunOnceAsync(blob.Id, funcName, ct);

                // schedule subsequent run
                var n = schedule.GetNextOccurrence(now.UtcDateTime.AddSeconds(1));
                _nextRuns[blob.Id] = new DateTimeOffset(n, TimeSpan.Zero);
            }
        }
    }

    private async Task RunOnceAsync(string blobId, string functionName, CancellationToken ct)
    {
        try
        {
            _logger.LogInformation("Invoking function {Function} from blob {BlobId}", functionName, blobId);
            await using var stream = await _storage.OpenReadAsync(blobId, ct);
            if (stream is null)
            {
                _logger.LogWarning("Blob {BlobId} not found when trying to invoke function {Function}", blobId, functionName);
                return;
            }

            var temp = Path.Combine(Path.GetTempPath(), $"clout_fn_{blobId}_{Guid.NewGuid():N}.dll");
            await using (var fs = File.Create(temp))
            {
                await stream.CopyToAsync(fs, ct);
            }

            try
            {
                await FunctionRunner.RunAsync(temp, functionName, ct);
                _logger.LogInformation("Function {Function} completed", functionName);
            }
            finally
            {
                try { File.Delete(temp); } catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Function {Function} failed", functionName);
        }
    }

    private static CrontabSchedule? TryParseSchedule(string expr)
    {
        // Try seconds-resolution first, then classic 5-field
        try { return CrontabSchedule.Parse(expr, new CrontabSchedule.ParseOptions { IncludingSeconds = true }); }
        catch { /* fall through */ }
        try { return CrontabSchedule.Parse(expr, new CrontabSchedule.ParseOptions { IncludingSeconds = false }); }
        catch { return null; }
    }
}
