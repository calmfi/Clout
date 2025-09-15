using System.Net.Http.Headers;
using System.Net.Http.Json;
using Quartz;

namespace Cloud.Shared;

/// <summary>
/// Minimal client for interacting with the Local Cloud API.
/// Cancellation: see AGENTS.md section "Cancellation and Async".
/// </summary>
public sealed class BlobApiClient
{
    private readonly HttpClient _http;
    /// <summary>
    /// Initializes a new client with the specified API base address.
    /// </summary>
    /// <param name="baseAddress">Base URL of the API (e.g., http://localhost:5000).</param>
    public BlobApiClient(string baseAddress)
    {
        _http = new HttpClient { BaseAddress = new Uri(baseAddress) };
    }

    /// <summary>
    /// Lists all blobs with metadata.
    /// </summary>
    public async Task<List<BlobInfo>> ListAsync(CancellationToken cancellationToken = default)
    {
        var items = await _http.GetFromJsonAsync("/api/blobs", AppJsonContext.Default.ListBlobInfo, cancellationToken);
        return items ?? new List<BlobInfo>();
    }

    /// <summary>
    /// Lists all registered functions (blobs with function metadata).
    /// </summary>
    public async Task<List<BlobInfo>> ListFunctionsAsync(CancellationToken cancellationToken = default)
    {
        var items = await _http.GetFromJsonAsync("/api/functions", AppJsonContext.Default.ListBlobInfo, cancellationToken);
        return items ?? new List<BlobInfo>();
    }

    /// <summary>
    /// Gets metadata for a blob.
    /// </summary>
    /// <param name="id">Blob identifier.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<BlobInfo?> GetInfoAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _http.GetFromJsonAsync($"/api/blobs/{id}/info", AppJsonContext.Default.BlobInfo, cancellationToken);
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
        await using var fs = File.OpenRead(filePath);
        var streamContent = new StreamContent(fs);
        streamContent.Headers.ContentType = new MediaTypeHeaderValue(contentType ?? "application/octet-stream");
        form.Add(streamContent, "file", Path.GetFileName(filePath));
        var response = await _http.PostAsync("/api/blobs", form, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<BlobInfo>(cancellationToken: cancellationToken);
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
        using var response = await _http.GetAsync($"/api/blobs/{id}", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var fs = File.Create(destinationPath);
        await response.Content.CopyToAsync(fs, cancellationToken);
    }

    /// <summary>
    /// Deletes a blob.
    /// </summary>
    /// <param name="id">Blob identifier.</param>
    /// <returns>True if deleted; false if not found.</returns>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var response = await _http.DeleteAsync($"/api/blobs/{id}", cancellationToken);
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
        var response = await _http.PutAsJsonAsync($"/api/blobs/{id}/metadata", metadata, AppJsonContext.Default.IEnumerableBlobMetadata, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.BlobInfo, cancellationToken);
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
        await using var fs = File.OpenRead(dllPath);
        var file = new StreamContent(fs);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", Path.GetFileName(dllPath));
        form.Add(new StringContent(name), "name");
        form.Add(new StringContent(runtime), "runtime");

        var response = await _http.PostAsync("/api/functions/register", form, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.BlobInfo, cancellationToken);
        return result!;
    }

    /// <summary>
    /// Registers multiple functions from the same .NET entrypoint DLL.
    /// </summary>
    public async Task<List<BlobInfo>> RegisterFunctionsAsync(string dllPath, IEnumerable<string> names, string runtime = "dotnet", string? cron = null, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        await using var fs = File.OpenRead(dllPath);
        var file = new StreamContent(fs);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", Path.GetFileName(dllPath));
        form.Add(new StringContent(string.Join(",", names ?? Array.Empty<string>())), "names");
        form.Add(new StringContent(runtime), "runtime");
        if (!string.IsNullOrWhiteSpace(cron)) form.Add(new StringContent(cron), "cron");

        var response = await _http.PostAsync("/api/functions/register-many", form, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.ListBlobInfo, cancellationToken);
        return result ?? new List<BlobInfo>();
    }

    /// <summary>
    /// Registers multiple functions from a provided stream containing the .NET assembly.
    /// </summary>
    public async Task<List<BlobInfo>> RegisterFunctionsAsync(Stream dllStream, string fileName, IEnumerable<string> names, string runtime = "dotnet", string? cron = null, CancellationToken cancellationToken = default)
    {
        using var form = new MultipartFormDataContent();
        var file = new StreamContent(dllStream);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "file", fileName);
        form.Add(new StringContent(string.Join(",", names ?? Array.Empty<string>())), "names");
        form.Add(new StringContent(runtime), "runtime");
        if (!string.IsNullOrWhiteSpace(cron)) form.Add(new StringContent(cron), "cron");

        var response = await _http.PostAsync("/api/functions/register-many", form, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.ListBlobInfo, cancellationToken);
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
        var response = await _http.PostAsJsonAsync($"/api/functions/register-from/{dllBlobId}", payload, AppJsonContext.Default.DictionaryStringString, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.BlobInfo, cancellationToken);
        return result!;
    }

    /// <summary>
    /// Registers multiple functions by referencing an existing DLL blob id.
    /// </summary>
    public async Task<List<BlobInfo>> RegisterFunctionsFromExistingAsync(string dllBlobId, IEnumerable<string> names, string runtime = "dotnet", string? cron = null, CancellationToken cancellationToken = default)
    {
        var payload = new RegisterMany { Names = names?.ToArray() ?? Array.Empty<string>(), Runtime = runtime, Cron = cron };
        var response = await _http.PostAsJsonAsync($"/api/functions/register-many-from/{dllBlobId}", payload, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.ListBlobInfo, cancellationToken);
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

        var info = await RegisterFunctionAsync(dllPath, name, runtime, cancellationToken);
        var updated = await SetTimerTriggerAsync(info.Id, cron, cancellationToken);
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
        var response = await _http.PostAsJsonAsync($"/api/functions/{id}/schedule", payload, AppJsonContext.Default.DictionaryStringString, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.BlobInfo, cancellationToken);
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

    private static string NormalizeCron(string expr)
    {
        var parts = expr.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
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
    public static bool IsValidCron(string expr)
    {
        try { return TryParseSchedule(expr, out _); }
        catch { return false; }
    }

    /// <summary>
    /// Removes the TimerTrigger metadata entry from the blob, if present.
    /// Preserves other existing metadata entries.
    /// </summary>
    public async Task<BlobInfo> ClearTimerTriggerAsync(string id, CancellationToken cancellationToken = default)
    {
        var response = await _http.DeleteAsync($"/api/functions/{id}/schedule", cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync(AppJsonContext.Default.BlobInfo, cancellationToken);
        return result!;
    }

    /// <summary>
    /// Gets next occurrences for a cron expression from the server.
    /// </summary>
    public async Task<List<string>> CronNextAsync(string expr, int count = 5, CancellationToken cancellationToken = default)
    {
        var url = $"/api/functions/cron-next?expr={Uri.EscapeDataString(expr)}&count={count}";
        var response = await _http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        var list = await response.Content.ReadFromJsonAsync<List<string>>(cancellationToken: cancellationToken);
        return list ?? new List<string>();
    }

    public async Task<int> ScheduleAllAsync(string sourceId, string cron, CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, string> { ["sourceId"] = sourceId, ["cron"] = cron };
        var response = await _http.PostAsJsonAsync("/api/functions/schedule-all", payload, AppJsonContext.Default.DictionaryStringString, cancellationToken);
        response.EnsureSuccessStatusCode();
        var dict = await response.Content.ReadFromJsonAsync<Dictionary<string, int>>(cancellationToken: cancellationToken);
        return dict != null && dict.TryGetValue("count", out var c) ? c : 0;
    }

    public async Task<int> UnscheduleAllAsync(string sourceId, CancellationToken cancellationToken = default)
    {
        var payload = new Dictionary<string, string> { ["sourceId"] = sourceId };
        var response = await _http.PostAsJsonAsync("/api/functions/unschedule-all", payload, AppJsonContext.Default.DictionaryStringString, cancellationToken);
        response.EnsureSuccessStatusCode();
        var dict = await response.Content.ReadFromJsonAsync<Dictionary<string, int>>(cancellationToken: cancellationToken);
        return dict != null && dict.TryGetValue("count", out var c) ? c : 0;
    }
}
