using System.Text.Json;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi.Models;
using Cloud.Shared;
using Clout.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Local Cloud API",
        Version = "v1",
        Description = "Simple local blob storage API for creating, listing, downloading, updating, and deleting file blobs."
    });
});

var storageRoot = Path.Combine(AppContext.BaseDirectory, "storage");
Directory.CreateDirectory(storageRoot);
builder.Services.AddSingleton<IBlobStorage>(_ => new FileBlobStorage(storageRoot));

builder.WebHost.UseUrls("http://localhost:5000");
var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

// Cancellation: see AGENTS.md > "Cancellation & Async"
app.MapGet("/api/blobs", async (IBlobStorage storage, CancellationToken ct) =>
    {
        var items = await storage.ListAsync(ct);
        var json = JsonSerializer.Serialize(items, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Results.Content(json, "application/json");
    })
    .WithName("ListBlobs")
    .WithTags("Blobs")
    .WithDescription("List all blobs with metadata.")
    .WithOpenApi();

// Cancellation: see AGENTS.md > "Cancellation & Async"
app.MapGet("/api/blobs/{id}", async (string id, IBlobStorage storage, CancellationToken ct) =>
    {
        var stream = await storage.OpenReadAsync(id, ct);
        if (stream is null) return Results.NotFound();
        var info = await storage.GetInfoAsync(id, ct);
        var fileName = info?.FileName ?? $"{id}.bin";
        var contentType = info?.ContentType ?? "application/octet-stream";
        return Results.File(stream, contentType, fileName);
    })
    .WithName("DownloadBlob")
    .WithTags("Blobs")
    .WithDescription("Download blob content by identifier.")
    .WithOpenApi();

// Cancellation: see AGENTS.md > "Cancellation & Async"
app.MapGet("/api/blobs/{id}/info", async (string id, IBlobStorage storage, CancellationToken ct) =>
    {
        var info = await storage.GetInfoAsync(id, ct);
        if (info is null) return Results.NotFound();
        var json = JsonSerializer.Serialize(info, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Results.Content(json, "application/json");
    })
    .WithName("GetBlobInfo")
    .WithTags("Blobs")
    .WithDescription("Get metadata for a blob.")
    .WithOpenApi();

// Cancellation: see AGENTS.md > "Cancellation & Async"
app.MapPost("/api/blobs", async (HttpRequest request, IBlobStorage storage, CancellationToken ct) =>
    {
        if (!request.HasFormContentType)
            return Results.BadRequest("Expected multipart/form-data with a 'file' field.");

        var form = await request.ReadFormAsync(ct);
        var file = form.Files["file"];
        if (file is null) return Results.BadRequest("Missing 'file' field.");

        var info = await storage.SaveAsync(file.FileName, file.OpenReadStream(), file.ContentType, ct);
        var json = JsonSerializer.Serialize(info, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Results.Content(json, "application/json", System.Text.Encoding.UTF8, StatusCodes.Status201Created);
    })
    .WithName("UploadBlob")
    .WithTags("Blobs")
    .WithDescription("Upload a new blob via multipart/form-data.")
    .WithOpenApi();

// Cancellation: see AGENTS.md > "Cancellation & Async"
app.MapPut("/api/blobs/{id}", async (string id, HttpRequest request, IBlobStorage storage, CancellationToken ct) =>
    {
        if (!request.HasFormContentType)
            return Results.BadRequest("Expected multipart/form-data with a 'file' field.");

        var form = await request.ReadFormAsync(ct);
        var file = form.Files["file"];
        if (file is null) return Results.BadRequest("Missing 'file' field.");

        var info = await storage.ReplaceAsync(id, file.OpenReadStream(), file.ContentType, file.FileName, ct);
        if (info is null) return Results.NotFound();
        var json = JsonSerializer.Serialize(info, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Results.Content(json, "application/json");
    })
    .WithName("UpdateBlob")
    .WithTags("Blobs")
    .WithDescription("Replace the content (and filename) of an existing blob.")
    .WithOpenApi();

// Cancellation: see AGENTS.md > "Cancellation & Async"
app.MapPut("/api/blobs/{id}/metadata", async (string id, HttpRequest request, IBlobStorage storage, CancellationToken ct) =>
    {
        var meta = await request.ReadFromJsonAsync<List<BlobMetadata>>(cancellationToken: ct);
        if (meta is null) return Results.BadRequest("Invalid or missing JSON body.");
        var updated = await storage.SetMetadataAsync(id, meta, ct);
        if (updated is null) return Results.NotFound();
        var json = JsonSerializer.Serialize(updated, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Results.Content(json, "application/json");
    })
    .WithName("SetBlobMetadata")
    .WithTags("Blobs")
    .WithDescription("Replace metadata entries for the blob with the provided list.")
    .WithOpenApi();

// Cancellation: see AGENTS.md > "Cancellation & Async"
app.MapDelete("/api/blobs/{id}", async (string id, IBlobStorage storage, CancellationToken ct) =>
    {
        var deleted = await storage.DeleteAsync(id, ct);
        return deleted ? Results.NoContent() : Results.NotFound();
    })
    .WithName("DeleteBlob")
    .WithTags("Blobs")
    .WithDescription("Delete a blob by identifier.")
    .WithOpenApi();

app.Run();

public partial class Program { }

// Types moved to separate files under the Clout.Api namespace.
