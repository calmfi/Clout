using System.Text.Json;
using Cloud.Shared;
using Clout.Api;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Quartz;

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
builder.Services.AddQuartz(q =>
{
    q.UseMicrosoftDependencyInjectionJobFactory();
});
builder.Services.AddQuartzHostedService(opt =>
{
    opt.WaitForJobsToComplete = true;
});

builder.WebHost.UseUrls("http://localhost:5000");
var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
var logger = app.Logger;

static string ToQuartzCron(string expr)
{
    var parts = expr.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 5)
    {
        return $"0 {expr}"; // add seconds
    }
    return expr; // assume already includes seconds
}

static async Task ScheduleFunctionAsync(IScheduler scheduler, string blobId, string functionName, string cron)
{
    var jobKey = new JobKey($"function-{blobId}");
    var job = JobBuilder.Create<Clout.Api.Functions.FunctionInvocationJob>()
        .WithIdentity(jobKey)
        .UsingJobData("blobId", blobId)
        .UsingJobData("functionName", functionName)
        .Build();

    var trigger = TriggerBuilder.Create()
        .WithIdentity($"trigger-{blobId}")
        .ForJob(jobKey)
        .WithCronSchedule(ToQuartzCron(cron))
        .Build();

    // Replace existing schedule if present
    await scheduler.ScheduleJob(job, new HashSet<ITrigger> { trigger }, replace: true);
}

static async Task UnscheduleFunctionAsync(IScheduler scheduler, string blobId)
{
    var jobKey = new JobKey($"function-{blobId}");
    await scheduler.DeleteJob(jobKey);
}

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
    .WithOpenApi(op =>
    {
        var example = """
        [
          {
            "id": "a1b2c3",
            "fileName": "hello.txt",
            "size": 11,
            "createdUtc": "2025-09-13T21:20:00Z",
            "contentType": "text/plain",
            "metadata": []
          }
        ]
        """;
        if (op.Responses.TryGetValue("200", out var resp) && resp.Content.TryGetValue("application/json", out var media))
        {
            media.Example = new OpenApiString(example);
        }
        return op;
    });

// Register a function from an existing DLL blob id
app.MapPost("/api/functions/register-from/{dllId}", async (string dllId, HttpRequest request, IBlobStorage storage, CancellationToken ct) =>
    {
        try
        {
            var payload = await request.ReadFromJsonAsync<Dictionary<string, string>>(cancellationToken: ct) ?? new();
            payload.TryGetValue("name", out var name);
            payload.TryGetValue("runtime", out var runtime);
            if (string.IsNullOrWhiteSpace(name)) return Results.Text("Missing 'name'.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
            runtime = string.IsNullOrWhiteSpace(runtime) ? "dotnet" : runtime;

            await using var source = await storage.OpenReadAsync(dllId, ct);
            if (source is null) return Results.NotFound();
            var info = await storage.GetInfoAsync(dllId, ct);
            var fileName = info?.FileName ?? $"{dllId}.dll";
            var contentType = info?.ContentType ?? "application/octet-stream";

            var tempPath = Path.Combine(Path.GetTempPath(), $"clout_{Guid.NewGuid():N}.dll");
            await using (var outStream = File.Create(tempPath))
            {
                await source.CopyToAsync(outStream, ct);
            }
            try
            {
                if (!FunctionAssemblyInspector.ContainsPublicMethod(tempPath, name, out var declaringType))
                    return Results.Text($"Validation failed: could not find a public method named '{name}' in the provided assembly.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);

                await using var dllStream = File.OpenRead(tempPath);
                var saved = await storage.SaveAsync(fileName, dllStream, contentType, ct);
                var metadata = new List<BlobMetadata>
                {
                    new("function.name", "text/plain", name),
                    new("function.runtime", "text/plain", ".net core"),
                    new("function.entrypoint", "text/plain", fileName),
                    new("function.declaringType", "text/plain", declaringType ?? string.Empty),
                    new("function.verified", "text/plain", "true")
                };
                var updated = await storage.SetMetadataAsync(saved.Id, metadata, ct);
                var result = updated ?? saved;
                var json = System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                return Results.Content(json, "application/json", System.Text.Encoding.UTF8, StatusCodes.Status201Created);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            return Results.Text($"Validation error: {ex.Message}", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
        }
    })
    .WithName("RegisterFunctionFromExisting")
    .WithTags("Functions")
    .WithDescription("Register a function by referencing an existing DLL blob id and function name.")
    .WithOpenApi();

// Register multiple functions from an existing DLL blob id
app.MapPost("/api/functions/register-many-from/{dllId}", async (string dllId, HttpRequest request, IBlobStorage storage, ISchedulerFactory schedFactory, CancellationToken ct) =>
    {
        try
        {
            var payload = await request.ReadFromJsonAsync<RegisterManyRequest>(cancellationToken: ct);
            if (payload is null || payload.Names is null || payload.Names.Length == 0)
                return Results.Text("Missing 'names'.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
            var runtime = string.IsNullOrWhiteSpace(payload.Runtime) ? "dotnet" : payload.Runtime;

            await using var source = await storage.OpenReadAsync(dllId, ct);
            if (source is null) return Results.NotFound();
            var info = await storage.GetInfoAsync(dllId, ct);
            var fileName = info?.FileName ?? $"{dllId}.dll";
            var contentType = info?.ContentType ?? "application/octet-stream";

            var tempPath = Path.Combine(Path.GetTempPath(), $"clout_{Guid.NewGuid():N}.dll");
            await using (var outStream = File.Create(tempPath))
            {
                await source.CopyToAsync(outStream, ct);
            }
            try
            {
                var results = new List<BlobInfo>();
                var scheduler = await schedFactory.GetScheduler();
                foreach (var name in payload.Names.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (!FunctionAssemblyInspector.ContainsPublicMethod(tempPath, name, out var declaringType))
                        return Results.Text($"Validation failed: could not find a public method named '{name}' in the provided assembly.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);

                    // Dedup: create a lightweight function entry without copying dll content
                    await using var empty = new MemoryStream(Array.Empty<byte>());
                    var saved = await storage.SaveAsync(fileName, empty, contentType, ct);
                    var metadata = new List<BlobMetadata>
                    {
                        new("function.name", "text/plain", name),
                        new("function.runtime", "text/plain", ".net core"),
                        new("function.entrypoint", "text/plain", fileName),
                        new("function.declaringType", "text/plain", declaringType ?? string.Empty),
                        new("function.verified", "text/plain", "true"),
                        new("function.sourceId", "text/plain", dllId)
                    };
                    if (!string.IsNullOrWhiteSpace(payload.Cron))
                    {
                        if (!ApiHelpers.TryParseSchedule(payload.Cron, out _))
                            return Results.Text("Invalid NCRONTAB expression.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
                        metadata.Add(new BlobMetadata("TimerTrigger", "text/plain", payload.Cron));
                        await ScheduleFunctionAsync(scheduler, saved.Id, name, payload.Cron);
                        var triggers = await scheduler.GetTriggersOfJob(new JobKey($"function-{saved.Id}"));
                        var next = triggers.FirstOrDefault()?.GetNextFireTimeUtc();
                        if (next.HasValue) logger.LogInformation("Scheduled function {Function} ({Id}) next at {Next}", name, saved.Id, next);
                    }
                    var updated = await storage.SetMetadataAsync(saved.Id, metadata, ct);
                    results.Add(updated ?? saved);
                    ct.ThrowIfCancellationRequested();
                }
                var json = System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                return Results.Content(json, "application/json", System.Text.Encoding.UTF8, StatusCodes.Status201Created);
            }
            finally { try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { } }
        }
        catch (Exception ex)
        {
            return Results.Text($"Validation error: {ex.Message}", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
        }
    })
    .WithName("RegisterFunctionsFromExisting")
    .WithTags("Functions")
    .WithDescription("Register multiple functions by referencing an existing DLL blob id and providing names.")
    .WithOpenApi();

// Cancellation: see AGENTS.md > "Cancellation & Async"
app.MapPost("/api/functions/register-many", async (HttpRequest request, IBlobStorage storage, ISchedulerFactory schedFactory, CancellationToken ct) =>
    {
        try
        {
            if (!request.HasFormContentType)
                return Results.Text("Expected multipart/form-data with fields: 'file' (dll), 'names' (comma-separated), 'runtime' (dotnet core).", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);

            var form = await request.ReadFormAsync(ct);
            var file = form.Files["file"];
            var namesRaw = form["names"].ToString();
            var runtime = form["runtime"].ToString();
            var cron = form["cron"].ToString();

            if (file is null) return Results.Text("Missing 'file' field.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
            if (string.IsNullOrWhiteSpace(namesRaw)) return Results.Text("Missing 'names' field.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
            if (string.IsNullOrWhiteSpace(runtime)) return Results.Text("Missing 'runtime' field.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);

            var rt = runtime.Trim().ToLowerInvariant();
            var allowed = new[] { "dotnet", ".net", ".net core", "dotnetcore", "netcore", "net" };
            if (!allowed.Contains(rt))
            {
                return Results.Text("Unsupported runtime. Only '.NET Core' is allowed (e.g., 'dotnet' or 'dotnetcore').", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
            }

            if (!file.FileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Text("Entrypoint must be a single .dll file.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
            }

            var names = namesRaw
                .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (names.Length == 0)
                return Results.Text("No function names provided in 'names'.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);

            bool addCron = false;
            if (!string.IsNullOrWhiteSpace(cron))
            {
                if (!ApiHelpers.TryParseSchedule(cron, out _))
                    return Results.Text("Invalid NCRONTAB expression.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
                addCron = true;
            }

            var tempPath = Path.Combine(Path.GetTempPath(), $"clout_{Guid.NewGuid():N}.dll");
            await using (var outStream = File.Create(tempPath))
            await using (var inStream = file.OpenReadStream())
            {
                await inStream.CopyToAsync(outStream, ct);
            }

            try
            {
                var results = new List<BlobInfo>();
                var scheduler = await schedFactory.GetScheduler();
                foreach (var name in names)
                {
                    if (!FunctionAssemblyInspector.ContainsPublicMethod(tempPath, name, out var declaringType))
                    {
                        return Results.Text($"Validation failed: could not find a public method named '{name}' in the provided assembly.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
                    }

                    await using var dllStream = File.OpenRead(tempPath);
                    var info = await storage.SaveAsync(file.FileName, dllStream, file.ContentType ?? "application/octet-stream", ct);
                    var metadata = new List<BlobMetadata>
                    {
                        new("function.name", "text/plain", name),
                        new("function.runtime", "text/plain", ".net core"),
                        new("function.entrypoint", "text/plain", file.FileName),
                        new("function.declaringType", "text/plain", declaringType ?? string.Empty),
                        new("function.verified", "text/plain", "true")
                    };
                    if (addCron)
                    {
                        metadata.Add(new BlobMetadata("TimerTrigger", "text/plain", cron));
                        await ScheduleFunctionAsync(scheduler, info.Id, name, cron);
                        var triggers = await scheduler.GetTriggersOfJob(new JobKey($"function-{info.Id}"));
                        var next = triggers.FirstOrDefault()?.GetNextFireTimeUtc();
                        if (next.HasValue) logger.LogInformation("Scheduled function {Function} ({Id}) next at {Next}", name, info.Id, next);
                    }
                    var updated = await storage.SetMetadataAsync(info.Id, metadata, ct);
                    results.Add(updated ?? info);
                    ct.ThrowIfCancellationRequested();
                }

                var json = System.Text.Json.JsonSerializer.Serialize(results, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                return Results.Content(json, "application/json", System.Text.Encoding.UTF8, StatusCodes.Status201Created);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            return Results.Text($"Validation error: {ex.Message}", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
        }
    })
    .WithName("RegisterFunctionsMany")
    .WithTags("Functions")
    .WithDescription("Register multiple functions from one .dll by providing a comma- or newline-separated list of method names in 'names'.")
    .WithOpenApi();

// Cancellation: see AGENTS.md > "Cancellation & Async"
app.MapGet("/api/blobs/{id}", async (string id, IBlobStorage storage, CancellationToken ct) =>
    {
        var info = await storage.GetInfoAsync(id, ct);
        var sourceId = info?.Metadata?.FirstOrDefault(m => string.Equals(m.Name, "function.sourceId", StringComparison.OrdinalIgnoreCase))?.Value;
        var readId = string.IsNullOrWhiteSpace(sourceId) ? id : sourceId;
        var stream = await storage.OpenReadAsync(readId, ct);
        if (stream is null) return Results.NotFound();
        var serveInfo = string.IsNullOrWhiteSpace(sourceId) ? info : await storage.GetInfoAsync(readId, ct);
        var fileName = serveInfo?.FileName ?? $"{readId}.bin";
        var contentType = serveInfo?.ContentType ?? "application/octet-stream";
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
    .WithOpenApi(op =>
    {
        var example = """
        {
          "id": "a1b2c3",
          "fileName": "hello.txt",
          "size": 11,
          "createdUtc": "2025-09-13T21:20:00Z",
          "contentType": "text/plain",
          "metadata": [
            { "name": "author", "contentType": "text/plain", "value": "alice" }
          ]
        }
        """;
        if (op.Responses.TryGetValue("200", out var resp) && resp.Content.TryGetValue("application/json", out var media))
        {
            media.Example = new OpenApiString(example);
        }
        return op;
    });

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

// Cancellation: see AGENTS.md > "Cancellation & Async"
app.MapPost("/api/functions/register", async (HttpRequest request, IBlobStorage storage, CancellationToken ct) =>
    {
        try
        {
            if (!request.HasFormContentType)
                return Results.Text("Expected multipart/form-data with fields: 'file' (dll), 'name' (function), 'runtime' (dotnet core).", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);

            var form = await request.ReadFormAsync(ct);
            var file = form.Files["file"];
            var name = form["name"].ToString();
            var runtime = form["runtime"].ToString();

            if (file is null) return Results.Text("Missing 'file' field.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
            if (string.IsNullOrWhiteSpace(name)) return Results.Text("Missing 'name' field.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
            if (string.IsNullOrWhiteSpace(runtime)) return Results.Text("Missing 'runtime' field.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);

            // Only .NET Core (aka .NET) is supported at the moment
            var rt = runtime.Trim().ToLowerInvariant();
            var allowed = new[] { "dotnet", ".net", ".net core", "dotnetcore", "netcore", "net" };
            if (!allowed.Contains(rt))
            {
                return Results.Text("Unsupported runtime. Only '.NET Core' is allowed (e.g., 'dotnet' or 'dotnetcore').", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
            }

            if (!file.FileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Text("Entrypoint must be a single .dll file.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
            }

            // Persist upload to a temp file for validation and then store in blob storage
            var tempPath = Path.Combine(Path.GetTempPath(), $"clout_{Guid.NewGuid():N}.dll");
            await using (var outStream = File.Create(tempPath))
            await using (var inStream = file.OpenReadStream())
            {
                await inStream.CopyToAsync(outStream, ct);
            }

            try
            {
                var validation = FunctionAssemblyInspector.ContainsPublicMethod(tempPath, name, out var declaringType);
                if (!validation)
                {
                    return Results.Text($"Validation failed: could not find a public method named '{name}' in the provided assembly.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
                }

                // Save validated DLL to blob storage
                await using var dllStream = File.OpenRead(tempPath);
                var info = await storage.SaveAsync(file.FileName, dllStream, file.ContentType ?? "application/octet-stream", ct);

                // Attach function metadata to the blob
                var metadata = new List<BlobMetadata>
                {
                    new("function.name", "text/plain", name),
                    new("function.runtime", "text/plain", ".net core"),
                    new("function.entrypoint", "text/plain", file.FileName),
                    new("function.declaringType", "text/plain", declaringType ?? string.Empty),
                    new("function.verified", "text/plain", "true")
                };

                var updated = await storage.SetMetadataAsync(info.Id, metadata, ct);
                var result = updated ?? info;
                var json = System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                return Results.Content(json, "application/json", System.Text.Encoding.UTF8, StatusCodes.Status201Created);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* ignore */ }
            }
        }
        catch (Exception ex)
        {
            return Results.Text($"Validation error: {ex.Message}", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
        }
    })
    .WithName("RegisterFunction")
    .WithTags("Functions")
    .WithDescription("Register a .NET Core function by uploading its entrypoint DLL. Validates the DLL contains a public method matching the function name.")
    .WithOpenApi(op =>
    {
        // 201 Created example payload
        var example = """
        {
          "id": "a1b2c3d4e5f6",
          "fileName": "MyFunction.dll",
          "size": 12345,
          "createdUtc": "2025-09-13T21:20:00Z",
          "contentType": "application/octet-stream",
          "metadata": [
            { "name": "function.name", "contentType": "text/plain", "value": "MyFunction" },
            { "name": "function.runtime", "contentType": "text/plain", "value": ".net core" },
            { "name": "function.entrypoint", "contentType": "text/plain", "value": "MyFunction.dll" },
            { "name": "function.declaringType", "contentType": "text/plain", "value": "SampleFunctions" },
            { "name": "function.verified", "contentType": "text/plain", "value": "true" }
          ]
        }
        """;
        if (op.Responses.TryGetValue("201", out var resp) && resp.Content.TryGetValue("application/json", out var media))
        {
            media.Example = new OpenApiString(example);
        }
        return op;
    });

// Cancellation: see AGENTS.md > "Cancellation & Async"
app.MapPost("/api/functions/{id}/schedule", async (string id, HttpRequest request, IBlobStorage storage, ISchedulerFactory schedFactory, CancellationToken ct) =>
    {
        try
        {
            // Accept JSON: { "expression": "* * * * *" } or { "ncrontab": "..." }
            var body = await request.ReadFromJsonAsync<Dictionary<string, string>>(cancellationToken: ct);
            var expr = body is not null && (body.TryGetValue("expression", out var e) || body.TryGetValue("ncrontab", out e)) ? e : null;
            if (string.IsNullOrWhiteSpace(expr))
                return Results.Text("Missing JSON body with 'expression' (or 'ncrontab') property.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);

            // Validate cron (support 5- and 6-field)
            if (!ApiHelpers.TryParseSchedule(expr!, out _))
                return Results.Text("Invalid NCRONTAB expression.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);

            var info = await storage.GetInfoAsync(id, ct);
            if (info is null) return Results.NotFound();
            var list = info.Metadata.ToList();
            list.RemoveAll(m => string.Equals(m.Name, "TimerTrigger", StringComparison.OrdinalIgnoreCase));
            list.Add(new BlobMetadata("TimerTrigger", "text/plain", expr!));
            var updated = await storage.SetMetadataAsync(id, list, ct);
            if (updated is null) return Results.NotFound();
            // Schedule with Quartz
            var scheduler = await schedFactory.GetScheduler();
            var funcName = updated.Metadata.FirstOrDefault(m => string.Equals(m.Name, "function.name", StringComparison.OrdinalIgnoreCase))?.Value;
            if (!string.IsNullOrWhiteSpace(funcName))
            {
                await ScheduleFunctionAsync(scheduler, id, funcName!, expr!);
                var triggers = await scheduler.GetTriggersOfJob(new JobKey($"function-{id}"));
                var next = triggers.FirstOrDefault()?.GetNextFireTimeUtc();
                if (next.HasValue) logger.LogInformation("Scheduled function {Function} ({Id}) next at {Next}", funcName, id, next);
            }
            var json = System.Text.Json.JsonSerializer.Serialize(updated, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            return Results.Content(json, "application/json");
        }
        catch (Exception ex)
        {
            return Results.Text($"Error: {ex.Message}", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
        }
    })
    .WithName("ScheduleFunction")
    .WithTags("Functions")
    .WithDescription("Sets the TimerTrigger NCRONTAB expression on a function blob.")
    .WithOpenApi(op =>
    {
        var example = """
        {
          "id": "a1b2c3d4e5f6",
          "fileName": "MyFunction.dll",
          "size": 12345,
          "createdUtc": "2025-09-13T21:20:00Z",
          "contentType": "application/octet-stream",
          "metadata": [
            { "name": "function.name", "contentType": "text/plain", "value": "MyFunction" },
            { "name": "function.runtime", "contentType": "text/plain", "value": ".net core" },
            { "name": "TimerTrigger", "contentType": "text/plain", "value": "* * * * *" }
          ]
        }
        """;
        if (op.Responses.TryGetValue("200", out var resp) && resp.Content.TryGetValue("application/json", out var media))
        {
            media.Example = new OpenApiString(example);
        }
        return op;
    });

// Cancellation: see AGENTS.md > "Cancellation & Async"
app.MapDelete("/api/functions/{id}/schedule", async (string id, IBlobStorage storage, ISchedulerFactory schedFactory, CancellationToken ct) =>
    {
        var info = await storage.GetInfoAsync(id, ct);
        if (info is null) return Results.NotFound();
        var list = info.Metadata.ToList();
        list.RemoveAll(m => string.Equals(m.Name, "TimerTrigger", StringComparison.OrdinalIgnoreCase));
        var updated = await storage.SetMetadataAsync(id, list, ct);
        if (updated is null) return Results.NotFound();
        var scheduler = await schedFactory.GetScheduler();
        await UnscheduleFunctionAsync(scheduler, id);
        logger.LogInformation("Unscheduled function job for {Id}", id);
        var json = System.Text.Json.JsonSerializer.Serialize(updated, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        return Results.Content(json, "application/json");
    })
    .WithName("UnscheduleFunction")
    .WithTags("Functions")
    .WithDescription("Removes the TimerTrigger NCRONTAB expression from a function blob.")
    .WithOpenApi(op =>
    {
        var example = """
        {
          "id": "a1b2c3d4e5f6",
          "fileName": "MyFunction.dll",
          "size": 12345,
          "createdUtc": "2025-09-13T21:20:00Z",
          "contentType": "application/octet-stream",
          "metadata": [
            { "name": "function.name", "contentType": "text/plain", "value": "MyFunction" },
            { "name": "function.runtime", "contentType": "text/plain", "value": ".net core" }
          ]
        }
        """;
        if (op.Responses.TryGetValue("200", out var resp) && resp.Content.TryGetValue("application/json", out var media))
        {
            media.Example = new OpenApiString(example);
        }
        return op;
    });

// Cancellation: see AGENTS.md > "Cancellation & Async"
app.MapPost("/api/functions/register/scheduled", async (HttpRequest request, IBlobStorage storage, ISchedulerFactory schedFactory, CancellationToken ct) =>
    {
        try
        {
            if (!request.HasFormContentType)
                return Results.Text("Expected multipart/form-data with fields: 'file' (dll), 'name' (function), 'runtime' (dotnet core), and 'cron' (NCRONTAB).", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);

            var form = await request.ReadFormAsync(ct);
            var file = form.Files["file"];
            var name = form["name"].ToString();
            var runtime = form["runtime"].ToString();
            var cron = form["cron"].ToString();
            if (string.IsNullOrWhiteSpace(cron))
            {
                // Try alternate keys for convenience
                cron = form["expression"].ToString();
                if (string.IsNullOrWhiteSpace(cron)) cron = form["ncrontab"].ToString();
            }

            if (file is null) return Results.Text("Missing 'file' field.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
            if (string.IsNullOrWhiteSpace(name)) return Results.Text("Missing 'name' field.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
            if (string.IsNullOrWhiteSpace(runtime)) return Results.Text("Missing 'runtime' field.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
            if (string.IsNullOrWhiteSpace(cron)) return Results.Text("Missing 'cron' (or 'expression'/'ncrontab') field.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);

            var rt = runtime.Trim().ToLowerInvariant();
            var allowed = new[] { "dotnet", ".net", ".net core", "dotnetcore", "netcore", "net" };
            if (!allowed.Contains(rt))
            {
                return Results.Text("Unsupported runtime. Only '.NET Core' is allowed (e.g., 'dotnet' or 'dotnetcore').", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
            }
            if (!file.FileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Text("Entrypoint must be a single .dll file.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
            }
            if (!ApiHelpers.TryParseSchedule(cron, out _))
            {
                return Results.Text("Invalid NCRONTAB expression.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
            }

            var tempPath = Path.Combine(Path.GetTempPath(), $"clout_{Guid.NewGuid():N}.dll");
            await using (var outStream = File.Create(tempPath))
            await using (var inStream = file.OpenReadStream())
            {
                await inStream.CopyToAsync(outStream, ct);
            }

            try
            {
                var validation = FunctionAssemblyInspector.ContainsPublicMethod(tempPath, name, out var declaringType);
                if (!validation)
                {
                    return Results.Text($"Validation failed: could not find a public method named '{name}' in the provided assembly.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
                }

                await using var dllStream = File.OpenRead(tempPath);
                var info = await storage.SaveAsync(file.FileName, dllStream, file.ContentType ?? "application/octet-stream", ct);

                var metadata = new List<BlobMetadata>
                {
                    new("function.name", "text/plain", name),
                    new("function.runtime", "text/plain", ".net core"),
                    new("function.entrypoint", "text/plain", file.FileName),
                    new("function.declaringType", "text/plain", declaringType ?? string.Empty),
                    new("function.verified", "text/plain", "true"),
                    new("TimerTrigger", "text/plain", cron)
                };
                var updated = await storage.SetMetadataAsync(info.Id, metadata, ct);
                var result = updated ?? info;
                var scheduler = await schedFactory.GetScheduler();
                await ScheduleFunctionAsync(scheduler, result.Id, name, cron);
                var triggers = await scheduler.GetTriggersOfJob(new JobKey($"function-{result.Id}"));
                var next = triggers.FirstOrDefault()?.GetNextFireTimeUtc();
                if (next.HasValue) logger.LogInformation("Scheduled function {Function} ({Id}) next at {Next}", name, result.Id, next);
                var json = System.Text.Json.JsonSerializer.Serialize(result, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
                return Results.Content(json, "application/json", System.Text.Encoding.UTF8, StatusCodes.Status201Created);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
        catch (Exception ex)
        {
            return Results.Text($"Validation error: {ex.Message}", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
        }
    })
    .WithName("RegisterFunctionScheduled")
    .WithTags("Functions")
    .WithDescription("Register a .NET Core function and schedule it with an NCRONTAB TimerTrigger in a single call.")
    .WithOpenApi(op =>
    {
        var example = """
        {
          "id": "a1b2c3d4e5f6",
          "fileName": "MyFunction.dll",
          "size": 12345,
          "createdUtc": "2025-09-13T21:20:00Z",
          "contentType": "application/octet-stream",
          "metadata": [
            { "name": "function.name", "contentType": "text/plain", "value": "MyFunction" },
            { "name": "function.runtime", "contentType": "text/plain", "value": ".net core" },
            { "name": "function.entrypoint", "contentType": "text/plain", "value": "MyFunction.dll" },
            { "name": "function.verified", "contentType": "text/plain", "value": "true" },
            { "name": "TimerTrigger", "contentType": "text/plain", "value": "* * * * *" }
          ]
        }
        """;
        if (op.Responses.TryGetValue("201", out var resp) && resp.Content.TryGetValue("application/json", out var media))
        {
            media.Example = new OpenApiString(example);
        }
        return op;
    });

// Cron preview endpoint
app.MapGet("/api/functions/cron-next", (string expr, int? count) =>
    {
        if (!ApiHelpers.TryParseSchedule(expr, out var schedule))
        {
            return Results.Text("Invalid cron expression.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
        }
        var n = Math.Clamp(count.GetValueOrDefault(5), 1, 50);
        var list = new List<string>(n);
        var next = DateTimeOffset.UtcNow;
        for (int i = 0; i < n; i++)
        {
            var maybe = schedule!.GetNextValidTimeAfter(next);
            if (!maybe.HasValue) break;
            next = maybe.Value;
            list.Add(next.ToString("u") + " UTC");
        }
        return Results.Json(list);
    })
    .WithName("CronNext")
    .WithTags("Functions")
    .WithDescription("Returns the next N occurrences for a valid NCRONTAB expression starting from now (UTC).")
    .WithOpenApi();

// Bulk schedule all functions referencing a given source DLL id
app.MapPost("/api/functions/schedule-all", async (HttpRequest request, IBlobStorage storage, ISchedulerFactory schedFactory, CancellationToken ct) =>
    {
        var payload = await request.ReadFromJsonAsync<Dictionary<string, string>>(cancellationToken: ct) ?? new();
        if (!payload.TryGetValue("sourceId", out var sourceId) || string.IsNullOrWhiteSpace(sourceId))
            return Results.Text("Missing 'sourceId'.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
        if (!payload.TryGetValue("cron", out var cron) || string.IsNullOrWhiteSpace(cron))
            return Results.Text("Missing 'cron'.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
        if (!ApiHelpers.TryParseSchedule(cron, out _))
            return Results.Text("Invalid NCRONTAB expression.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);

        var all = await storage.ListAsync(ct);
        var funcs = all.Where(b => b.Metadata?.Any(m => string.Equals(m.Name, "function.sourceId", StringComparison.OrdinalIgnoreCase) && string.Equals(m.Value, sourceId, StringComparison.OrdinalIgnoreCase)) == true).ToList();
        var count = 0;
        var scheduler = await schedFactory.GetScheduler();
        foreach (var f in funcs)
        {
            var meta = f.Metadata ?? new List<BlobMetadata>();
            // remove existing TimerTrigger
            meta = meta.Where(m => !string.Equals(m.Name, "TimerTrigger", StringComparison.OrdinalIgnoreCase)).ToList();
            meta.Add(new BlobMetadata("TimerTrigger", "text/plain", cron));
            await storage.SetMetadataAsync(f.Id, meta, ct);
            var name = meta.FirstOrDefault(m => string.Equals(m.Name, "function.name", StringComparison.OrdinalIgnoreCase))?.Value
                ?? f.Metadata.FirstOrDefault(m => string.Equals(m.Name, "function.name", StringComparison.OrdinalIgnoreCase))?.Value
                ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(name))
            {
                await ScheduleFunctionAsync(scheduler, f.Id, name, cron);
                var triggers = await scheduler.GetTriggersOfJob(new JobKey($"function-{f.Id}"));
                var next = triggers.FirstOrDefault()?.GetNextFireTimeUtc();
                if (next.HasValue) logger.LogInformation("Scheduled function {Function} ({Id}) next at {Next}", name, f.Id, next);
            }
            count++;
            ct.ThrowIfCancellationRequested();
        }
        return Results.Json(new { count });
    })
    .WithName("ScheduleAllFromSource")
    .WithTags("Functions")
    .WithDescription("Set the TimerTrigger cron on all functions that reference the given source DLL id.")
    .WithOpenApi();

// Bulk unschedule all functions referencing a given source DLL id
app.MapPost("/api/functions/unschedule-all", async (HttpRequest request, IBlobStorage storage, ISchedulerFactory schedFactory, CancellationToken ct) =>
    {
        var payload = await request.ReadFromJsonAsync<Dictionary<string, string>>(cancellationToken: ct) ?? new();
        if (!payload.TryGetValue("sourceId", out var sourceId) || string.IsNullOrWhiteSpace(sourceId))
            return Results.Text("Missing 'sourceId'.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
        var all = await storage.ListAsync(ct);
        var funcs = all.Where(b => b.Metadata?.Any(m => string.Equals(m.Name, "function.sourceId", StringComparison.OrdinalIgnoreCase) && string.Equals(m.Value, sourceId, StringComparison.OrdinalIgnoreCase)) == true).ToList();
        var count = 0;
        var scheduler = await schedFactory.GetScheduler();
        foreach (var f in funcs)
        {
            var meta = f.Metadata ?? new List<BlobMetadata>();
            meta = meta.Where(m => !string.Equals(m.Name, "TimerTrigger", StringComparison.OrdinalIgnoreCase)).ToList();
            await storage.SetMetadataAsync(f.Id, meta, ct);
            await UnscheduleFunctionAsync(scheduler, f.Id);
            count++;
            ct.ThrowIfCancellationRequested();
        }
        return Results.Json(new { count });
    })
    .WithName("UnscheduleAllFromSource")
    .WithTags("Functions")
    .WithDescription("Remove the TimerTrigger cron on all functions that reference the given source DLL id.")
    .WithOpenApi();

// Initialize Quartz schedules from persisted metadata at startup
{
    using var scope = app.Services.CreateScope();
    var storage = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
    var schedulerFactory = scope.ServiceProvider.GetRequiredService<ISchedulerFactory>();
    var scheduler = await schedulerFactory.GetScheduler();
    var blobs = await storage.ListAsync();
    foreach (var b in blobs)
    {
        var name = b.Metadata.FirstOrDefault(m => string.Equals(m.Name, "function.name", StringComparison.OrdinalIgnoreCase))?.Value;
        var cron = b.Metadata.FirstOrDefault(m => string.Equals(m.Name, "TimerTrigger", StringComparison.OrdinalIgnoreCase))?.Value;
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(cron))
        {
            await ScheduleFunctionAsync(scheduler, b.Id, name!, cron!);
            var triggers = await scheduler.GetTriggersOfJob(new JobKey($"function-{b.Id}"));
            var next = triggers.FirstOrDefault()?.GetNextFireTimeUtc();
            if (next.HasValue) logger.LogInformation("Scheduled function {Function} ({Id}) next at {Next}", name, b.Id, next);
        }
    }
}

await app.RunAsync(default);
