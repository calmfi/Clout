namespace Clout.UI.Services;

using System.Net.Http.Json;
using Cloud.Shared;

public sealed class CloutApiClient(HttpClient http) : ICloutApiClient
{
    private readonly HttpClient _http = http;

    public async Task<IReadOnlyList<BlobInfo>> GetBlobsAsync(CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync("/api/blobs", ct);
        resp.EnsureSuccessStatusCode();
        var items = await resp.Content.ReadFromJsonAsync<List<BlobInfo>>(cancellationToken: ct)
                    ?? new List<BlobInfo>();
        return items;
    }

    public async Task<IReadOnlyList<BlobInfo>> GetFunctionsAsync(CancellationToken ct = default)
    {
        var blobs = await GetBlobsAsync(ct);
        // Consider a blob a function if it has function.* metadata
        var functions = blobs
            .Where(b => b.Metadata.Any(m => m.Name.StartsWith("function.", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        return functions;
    }

    public async Task SetFunctionScheduleAsync(string id, string expression, CancellationToken ct = default)
    {
        using var resp = await _http.PostAsJsonAsync($"/api/functions/{id}/schedule", new { expression }, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task ClearFunctionScheduleAsync(string id, CancellationToken ct = default)
    {
        using var resp = await _http.DeleteAsync($"/api/functions/{id}/schedule", ct);
        resp.EnsureSuccessStatusCode();
    }
}
