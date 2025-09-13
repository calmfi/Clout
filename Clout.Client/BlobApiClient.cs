using System.Net.Http.Headers;
using System.Net.Http.Json;
using Cloud.Shared;

namespace Clout.Client;

/// <summary>
/// Minimal client for interacting with the Local Cloud API.
/// Cancellation: see AGENTS.md > "Cancellation & Async".
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
        var items = await _http.GetFromJsonAsync<List<BlobInfo>>("/api/blobs", cancellationToken);
        return items ?? new List<BlobInfo>();
    }

    /// <summary>
    /// Gets metadata for a blob.
    /// </summary>
    /// <param name="id">Blob identifier.</param>
    public async Task<BlobInfo?> GetInfoAsync(string id, CancellationToken cancellationToken = default)
    {
        return await _http.GetFromJsonAsync<BlobInfo>($"/api/blobs/{id}/info", cancellationToken);
    }

    /// <summary>
    /// Uploads a file as a new blob.
    /// </summary>
    /// <param name="filePath">Path to the file on disk.</param>
    /// <param name="contentType">Optional content type. Defaults to application/octet-stream.</param>
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
    public async Task<BlobInfo> SetMetadataAsync(string id, IEnumerable<Cloud.Shared.BlobMetadata> metadata, CancellationToken cancellationToken = default)
    {
        var response = await _http.PutAsJsonAsync($"/api/blobs/{id}/metadata", metadata, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<BlobInfo>(cancellationToken: cancellationToken);
        return result!;
    }

    /// <summary>
    /// Registers a function by uploading its .NET entrypoint DLL and metadata.
    /// </summary>
    /// <param name="dllPath">Path to the function .dll.</param>
    /// <param name="name">Function name. Assembly must contain a public method with this name.</param>
    /// <param name="runtime">Runtime identifier (default: "dotnet").</param>
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
        var result = await response.Content.ReadFromJsonAsync<BlobInfo>(cancellationToken: cancellationToken);
        return result!;
    }
}

