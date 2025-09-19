using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Collections.Generic;
using Clout.Shared.Models;
using Quartz;
using System.Text;
using System.Text.Json;

namespace Clout.Shared;

/// <summary>
/// Minimal client for interacting with the Local Cloud API.
/// Cancellation: see AGENTS.md section "Cancellation and Async".
/// </summary>
public sealed class ApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    /// <summary>
    /// Initializes a new client with the specified API base address.
    /// </summary>
    /// <param name="baseAddress">Base URL of the API (e.g., http://localhost:5000).</param>
    public ApiClient(string baseAddress)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseAddress) };
        _ownsHttpClient = true;
    }

    /// <summary>
    /// Initializes a new client using an externally managed HttpClient.
    /// The HttpClient's BaseAddress should be set by the caller.
    /// </summary>
    /// <param name="http">Configured HttpClient instance.</param>
    public ApiClient(HttpClient http)
    {
        _http = http;
        _ownsHttpClient = false;
    }
    /// <summary>
    /// Disposes underlying HTTP resources.
    /// </summary>
    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    // -------------------- Queue API --------------------
    /// <summary>
    /// Lists queues with stats.
    /// </summary>
    public async Task<List<QueueStats>> ListQueuesAsync(CancellationToken cancellationToken = default)
    {
        var items = await _http
            .GetFromJsonAsync(new Uri("/amqp/queues", UriKind.Relative), AppJsonContext.Default.ListQueueStats, cancellationToken)
            .ConfigureAwait(false);
        return items ?? new List<QueueStats>();
    }

    /// <summary>
    /// Creates a queue if it does not exist.
    /// </summary>
    public async Task CreateQueueAsync(string name, CancellationToken cancellationToken = default)
    {
        using var resp = await _http.PostAsync(new Uri($"/amqp/queues/{Uri.EscapeDataString(name)}", UriKind.Relative), content: null, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Purges all messages from a queue.
    /// </summary>
    public async Task PurgeQueueAsync(string name, CancellationToken cancellationToken = default)
    {
        using var resp = await _http.PostAsync(new Uri($"/amqp/queues/{Uri.EscapeDataString(name)}/purge", UriKind.Relative), content: null, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Enqueues a JSON message given as a JsonElement.
    /// </summary>
    public async Task EnqueueJsonAsync(string name, JsonElement message, CancellationToken cancellationToken = default)
    {
        using var resp = await _http.PostAsJsonAsync(new Uri($"/amqp/queues/{Uri.EscapeDataString(name)}/messages", UriKind.Relative), message, AppJsonContext.Default.JsonElement, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Enqueues a message from a string. If <paramref name="asJson"/> is true, the string should be a JSON literal/object/array.
    /// Otherwise it is sent as a JSON string value.
    /// </summary>
    public async Task EnqueueStringAsync(string name, string value, bool asJson = false, CancellationToken cancellationToken = default)
    {
        if (asJson)
        {
            using var doc = JsonDocument.Parse(value);
            await EnqueueJsonAsync(name, doc.RootElement, cancellationToken).ConfigureAwait(false);
            return;
        }
        // Send as JSON string
        using var content = new StringContent(JsonSerializer.Serialize(value, AppJsonContext.Default.JsonElement.Options), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(new Uri($"/amqp/queues/{Uri.EscapeDataString(name)}/messages", UriKind.Relative), content, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Enqueues a file. If content type is application/json, the file content is parsed and sent as JSON.
    /// Otherwise the file is wrapped into a JSON envelope with base64 data and provided contentType.
    /// </summary>
    public async Task EnqueueFileAsync(string name, string filePath, string contentType, CancellationToken cancellationToken = default)
    {
        if (string.Equals(contentType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            using var fs = File.OpenRead(filePath);
            using var doc = await JsonDocument.ParseAsync(fs, cancellationToken: cancellationToken).ConfigureAwait(false);
            await EnqueueJsonAsync(name, doc.RootElement, cancellationToken).ConfigureAwait(false);
            return;
        }

        var bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        var payload = new
        {
            contentType,
            fileName = Path.GetFileName(filePath),
            data = Convert.ToBase64String(bytes)
        };
        using var resp = await _http.PostAsJsonAsync(new Uri($"/amqp/queues/{Uri.EscapeDataString(name)}/messages", UriKind.Relative), payload, cancellationToken).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Dequeues a message, optionally with a timeout in milliseconds. Returns null if no content.
    /// </summary>
    public async Task<JsonElement?> DequeueAsync(string name, int? timeoutMs = null, CancellationToken cancellationToken = default)
    {
        var suffix = timeoutMs is > 0 ? $"?timeoutMs={timeoutMs.Value}" : string.Empty;
        using var resp = await _http.PostAsync(new Uri($"/amqp/queues/{Uri.EscapeDataString(name)}/dequeue{suffix}", UriKind.Relative), content: null, cancellationToken).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NoContent)
        {
            return null;
        }
        resp.EnsureSuccessStatusCode();
        var doc = await resp.Content.ReadFromJsonAsync(AppJsonContext.Default.JsonElement, cancellationToken).ConfigureAwait(false);
        return doc;
    }

    /// <summary>
    /// Lists all blobs with metadata.
    /// </summary>
    public async Task<List<BlobInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var items = await _http
            .GetFromJsonAsync(new Uri("/api/blobs", UriKind.Relative), AppJsonContext.Default.ListBlobInfo, cancellationToken)
            .ConfigureAwait(false);
        return items ?? new List<BlobInfo>();
    }

    /// <summary>
    /// Lists all registered functions (blobs with function metadata).
    /// </summary>
    public async Task<List<BlobInfo>> ListFunctionsAsync(CancellationToken cancellationToken = default)
    {
        var items = await _http
            .GetFromJsonAsync(new Uri("/api/functions", UriKind.Relative), AppJsonContext.Default.ListBlobInfo, cancellationToken)
            .ConfigureAwait(false);
        return items ?? new List<BlobInfo>();
    }

    /// <summary>
    /// Gets metadata for a blob.
    /// </summary>
    /// <param name="id">Blob identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<BlobInfo?> GetInfoAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _http
            .GetFromJsonAsync(new Uri($"/api/blobs/{id}/info", UriKind.Relative), AppJsonContext.Default.BlobInfo, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Uploads a file as a new blob.
    /// </summary>
    /// <param name="filePath">Path to the file on disk.</param>
    /// <param name="contentType">Optional content type. Defaults to application/octet-stream.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<BlobInfo> UploadAsync(string filePath, string? contentType = null, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        using var fs = File.OpenRead(filePath);
        using var streamContent = new StreamContent(fs);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "application/octet-stream");
        form.Add(streamContent, "file", Path.GetFileName(filePath));
        using var response = await _http.PostAsync(new Uri("/api/blobs", UriKind.Relative), form, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<BlobInfo>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return result!;
    }

    /// <summary>
    /// Downloads a blob's content to a file path.
    /// </summary>
    /// <param name="id">Blob identifier.</param>
    /// <param name="destinationPath">Target path for the downloaded file.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task DownloadAsync(string id, string destinationPath, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(new Uri($"/api/blobs/{id}", UriKind.Relative), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var fs = File.Create(destinationPath);
        await response.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Deletes a blob.
    /// </summary>
    /// <param name="id">Blob identifier.</param>
    /// <returns>True if deleted; false if not found.</returns>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync(new Uri($"/api/blobs/{id}", UriKind.Relative), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return false;
        response.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>
    /// Replaces the metadata list for a blob.
    /// </summary>
    /// <param name="id">Blob identifier.</param>
    /// <param name="metadata">Metadata entries to set (name, content type, value).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated <see cref="BlobInfo"/>.</returns>
    public async Task<BlobInfo> SetMetadataAsync(string id, IEnumerable<BlobMetadata> metadata, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PutAsJsonAsync(new Uri($"/api/blobs/{id}/metadata", UriKind.Relative), metadata, AppJsonContext.Default.IEnumerableBlobMetadata, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.BlobInfo, cancellationToken).ConfigureAwait(false);
        return result!;
    }

    /// <summary>
    /// Registers a function by uploading its .NET entrypoint DLL and metadata.
    /// </summary>
    /// <param name="dllPath">Path to the function .dll.</param>
    /// <param name="name">Function name. Assembly must contain a public method with this name.</param>
    /// <param name="runtime">Runtime identifier (default: "dotnet").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<BlobInfo> RegisterFunctionAsync(string dllPath, string name, string runtime = "dotnet", CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        using var fs = File.OpenRead(dllPath);
        using var file = new StreamContent(fs);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", Path.GetFileName(dllPath));
        using var sc1 = new StringContent(name);
        using var sc2 = new StringContent(runtime);
        form.Add(sc1, "name");
        form.Add(sc2, "runtime");

        using var response = await _http.PostAsync(new Uri("/api/functions/register", UriKind.Relative), form, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.BlobInfo, cancellationToken).ConfigureAwait(false);
        return result!;
    }

    /// <summary>
    /// Registers multiple functions from the same .NET entrypoint DLL.
    /// </summary>
    public async Task<List<BlobInfo>> RegisterFunctionsAsync(string dllPath, IEnumerable<string> names, string runtime = "dotnet", string? cron = null, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        using var fs = File.OpenRead(dllPath);
        using var file = new StreamContent(fs);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", Path.GetFileName(dllPath));
        using var scNames = new StringContent(string.Join(",", names ?? Array.Empty<string>()));
        using var scRuntime = new StringContent(runtime);
        form.Add(scNames, "names");
        form.Add(scRuntime, "runtime");
        if (!string.IsNullOrWhiteSpace(cron)) { using var scCron = new StringContent(cron); form.Add(scCron, "cron"); }

        using var response = await _http.PostAsync(new Uri("/api/functions/register-many", UriKind.Relative), form, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.ListBlobInfo, cancellationToken).ConfigureAwait(false);
        return result ?? new List<BlobInfo>();
    }

    /// <summary>
    /// Registers multiple functions from a provided stream containing the .NET assembly.
    /// </summary>
    public async Task<List<BlobInfo>> RegisterFunctionsAsync(Stream dllStream, string fileName, IEnumerable<string> names, string runtime = "dotnet", string? cron = null, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        using var file2 = new StreamContent(dllStream);
        file2.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file2, "file", fileName);
        using var scNames2 = new StringContent(string.Join(",", names ?? Array.Empty<string>()));
        using var scRuntime2 = new StringContent(runtime);
        form.Add(scNames2, "names");
        form.Add(scRuntime2, "runtime");
        if (!string.IsNullOrWhiteSpace(cron)) { using var scCron2 = new StringContent(cron); form.Add(scCron2, "cron"); }

        using var response2 = await _http.PostAsync(new Uri("/api/functions/register-many", UriKind.Relative), form, cancellationToken).ConfigureAwait(false);
        response2.EnsureSuccessStatusCode();
        var result = await response2.Content.ReadFromJsonAsync(AppJsonContext.Default.ListBlobInfo, cancellationToken).ConfigureAwait(false);
        return result ?? new List<BlobInfo>();
    }

    /// <summary>
    /// Registers a function by referencing an existing DLL blob id.
    /// </summary>
    public async Task<BlobInfo> RegisterFunctionFromExistingAsync(string dllBlobId, string name, string runtime = "dotnet", CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, string>
        {
            ["name"] = name,
            ["runtime"] = runtime,
        };
        using var response3 = await _http.PostAsJsonAsync(new Uri($"/api/functions/register-from/{dllBlobId}", UriKind.Relative), payload, AppJsonContext.Default.DictionaryStringString, cancellationToken).ConfigureAwait(false);
        response3.EnsureSuccessStatusCode();
        var result = await response3.Content.ReadFromJsonAsync(AppJsonContext.Default.BlobInfo, cancellationToken).ConfigureAwait(false);
        return result!;
    }

    /// <summary>
    /// Registers multiple functions by referencing an existing DLL blob id.
    /// </summary>
    public async Task<List<BlobInfo>> RegisterFunctionsFromExistingAsync(string dllBlobId, IEnumerable<string> names, string runtime = "dotnet", string? cron = null, CancellationToken cancellationToken = default)
    {
        var payload = new RegisterMany { Names = names?.ToArray() ?? Array.Empty<string>(), Runtime = runtime, Cron = cron };
        using var response4 = await _http.PostAsJsonAsync(new Uri($"/api/functions/register-many-from/{dllBlobId}", UriKind.Relative), payload, cancellationToken).ConfigureAwait(false);
        response4.EnsureSuccessStatusCode();
        var result = await response4.Content.ReadFromJsonAsync(AppJsonContext.Default.ListBlobInfo, cancellationToken).ConfigureAwait(false);
        return result ?? new List<BlobInfo>();
    }



    /// <summary>
    /// Registers a function and schedules it with a Quartz cron TimerTrigger in one call.
    /// </summary>
    /// <param name="dllPath">Path to the function .dll.</param>
    /// <param name="name">Function name. Assembly must contain a public method with this name.</param>
    /// <param name="cron">Cron expression (Quartz; 5 or 6 fields) for TimerTrigger.</param>
    /// <param name="runtime">Runtime identifier (default: "dotnet").</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated BlobInfo after scheduling.</returns>
    public async Task<BlobInfo> RegisterFunctionWithScheduleAsync(string dllPath, string name, string cron, string runtime = "dotnet", CancellationToken cancellationToken = default)
    {
        // Validate cron locally against Quartz format (5 or 6 fields)
        if (!TryParseSchedule(cron, out _))
            throw new ArgumentException("Invalid cron expression (Quartz format expected).", nameof(cron));

        var info = await RegisterFunctionAsync(dllPath, name, runtime, cancellationToken).ConfigureAwait(false);
        var updated = await SetTimerTriggerAsync(info.Id, cron, cancellationToken).ConfigureAwait(false);
        return updated;
    }

    /// <summary>
    /// Sets or updates the TimerTrigger cron expression on a function blob's metadata (Quartz).
    /// Preserves other existing metadata entries.
    /// </summary>
    public async Task<BlobInfo> SetTimerTriggerAsync(string id, string cron, CancellationToken cancellationToken = default)
    {
        // Validate locally (Quartz cron, support 5- or 6-field)
        if (!TryParseSchedule(cron, out _))
            throw new ArgumentException("Invalid cron expression (Quartz format expected).", nameof(cron));

        var payload = new Dictionary<string, string> { ["expression"] = cron };
        using var response = await _http.PostAsJsonAsync(new Uri($"/api/functions/{id}/schedule", UriKind.Relative), payload, AppJsonContext.Default.DictionaryStringString, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.BlobInfo, cancellationToken).ConfigureAwait(false);
        return result!;
    }

    private static bool TryParseSchedule(string expr, out CronExpression? schedule)
    {
        var normalized = NormalizeCron(expr);
        if (CronExpression.IsValidExpression(normalized))
        {
            schedule = new CronExpression(normalized);
            return true;
        }
        schedule = null;
        return false;
    }

    private static readonly char[] SplitWhitespace = new[] { ' ', '\t' };

    private static string NormalizeCron(string expr)
    {
        var parts = expr.Split(SplitWhitespace, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 5)
        {
            return $"0 {expr}"; // add seconds for Quartz
        }
        return expr;
    }

    /// <summary>
    /// Validates whether the provided cron expression is syntactically valid (Quartz)
    /// (with or without seconds). Returns false on invalid expressions.
    /// </summary>
    public static bool IsValidCron(string expr) => TryParseSchedule(expr, out _);

    /// <summary>
    /// Removes the TimerTrigger metadata entry from the blob, if present.
    /// Preserves other existing metadata entries.
    /// </summary>
    public async Task<BlobInfo> ClearTimerTriggerAsync(string id, CancellationToken cancellationToken = default)
    {
        using var response = await _http.DeleteAsync(new Uri($"/api/functions/{id}/schedule", UriKind.Relative), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.BlobInfo, cancellationToken).ConfigureAwait(false);
        return result!;
    }

    /// <summary>
    /// Sets the QueueTrigger queue name for a function blob.
    /// </summary>
    /// <param name="id">Function blob identifier.</param>
    /// <param name="queue">Queue name to bind.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated blob metadata.</returns>
    public async Task<BlobInfo> SetQueueTriggerAsync(string id, string queue, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Value cannot be null or whitespace.", nameof(id));
        if (string.IsNullOrWhiteSpace(queue)) throw new ArgumentException("Value cannot be null or whitespace.", nameof(queue));

        var payload = new Dictionary<string, string> { ["queue"] = queue };
        using var response = await _http.PostAsJsonAsync(new Uri($"/api/functions/{Uri.EscapeDataString(id)}/queue-trigger", UriKind.Relative), payload, AppJsonContext.Default.DictionaryStringString, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.BlobInfo, cancellationToken).ConfigureAwait(false);
        return result!;
    }

    /// <summary>
    /// Removes the QueueTrigger metadata entry from the blob, if present.
    /// </summary>
    /// <param name="id">Function blob identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Updated blob metadata.</returns>
    public async Task<BlobInfo> ClearQueueTriggerAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Value cannot be null or whitespace.", nameof(id));

        using var response = await _http.DeleteAsync(new Uri($"/api/functions/{Uri.EscapeDataString(id)}/queue-trigger", UriKind.Relative), cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.BlobInfo, cancellationToken).ConfigureAwait(false);
        return result!;
    }


    /// <summary>
    /// Gets next occurrences for a cron expression from the server.
    /// </summary>
    public async Task<List<string>> CronNextAsync(string expr, int count = 5, CancellationToken cancellationToken = default)
    {
        var url = new Uri($"/api/functions/cron-next?expr={Uri.EscapeDataString(expr)}&count={count}", UriKind.Relative);
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var list = await response.Content.ReadFromJsonAsync<List<string>>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return list ?? new List<string>();
    }

    /// <summary>
    /// Schedules all functions derived from the given source blob id using the provided NCRONTAB expression.
    /// </summary>
    /// <param name="sourceId">The blob id used as the source for functions.</param>
    /// <param name="cron">NCRONTAB expression (5- or 6-field). Seconds are supported.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of functions scheduled.</returns>
    public async Task<int> ScheduleAllAsync(string sourceId, string cron, CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, string> { ["sourceId"] = sourceId, ["cron"] = cron };
        using var response = await _http.PostAsJsonAsync(new Uri("/api/functions/schedule-all", UriKind.Relative), payload, AppJsonContext.Default.DictionaryStringString, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var dict = await response.Content.ReadFromJsonAsync<Dictionary<string, int>>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return dict != null && dict.TryGetValue("count", out var c) ? c : 0;
    }

    /// <summary>
    /// Removes all scheduled timers for functions derived from the given source blob id.
    /// </summary>
    /// <param name="sourceId">The blob id whose derived functions should be unscheduled.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of functions unscheduled.</returns>
    public async Task<int> UnscheduleAllAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, string> { ["sourceId"] = sourceId };
        using var response = await _http.PostAsJsonAsync(new Uri("/api/functions/unschedule-all", UriKind.Relative), payload, AppJsonContext.Default.DictionaryStringString, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var dict = await response.Content.ReadFromJsonAsync<Dictionary<string, int>>(cancellationToken: cancellationToken).ConfigureAwait(false);
        return dict != null && dict.TryGetValue("count", out var c) ? c : 0;
    }
}



