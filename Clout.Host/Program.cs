using System.Text.Json;
using Clout.Host.Configuration;
using Clout.Host.Functions;
using Clout.Host.Health;
using Clout.Host.Middleware;
using Clout.Host.Queue;
using Clout.Host.Storage;
using Clout.Shared;
using Clout.Shared.Abstractions;
using Clout.Shared.Models;
using Microsoft.Extensions.Options;
using Quartz;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

// Add services to the container.
builder.Services.AddProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddOpenTelemetry();

builder.Services.AddEndpointsApiExplorer();

// Add health checks
builder.Services.AddHealthChecks()
    .AddCloutHealthChecks();

// Configure and validate options
builder.Services.AddOptions<BlobStorageOptions>()
    .Bind(builder.Configuration.GetSection("BlobStorage"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<FunctionExecutionOptions>()
    .Bind(builder.Configuration.GetSection("FunctionExecution"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<TempFileCleanupOptions>()
    .Bind(builder.Configuration.GetSection("TempFileCleanup"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<DiagnosticsOptions>()
    .Bind(builder.Configuration.GetSection("Diagnostics"))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<IValidateOptions<BlobStorageOptions>, CloutConfigurationValidator>();
builder.Services.AddSingleton<IValidateOptions<FunctionExecutionOptions>, CloutConfigurationValidator>();
builder.Services.AddSingleton<IValidateOptions<TempFileCleanupOptions>, CloutConfigurationValidator>();
builder.Services.AddSingleton<IValidateOptions<DiagnosticsOptions>, CloutConfigurationValidator>();

var storageRoot = Path.Combine(AppContext.BaseDirectory, "storage");
Directory.CreateDirectory(storageRoot);

builder.Services.AddSingleton<IBlobStorage>(sp => 
    new FileBlobStorage(storageRoot, sp.GetRequiredService<ILogger<FileBlobStorage>>()));
builder.Services.AddOptions<QueueStorageOptions>()
    .Bind(builder.Configuration.GetSection("Queue"))
    .ValidateOnStart();
builder.Services.AddSingleton<IAmqpQueueServer, DiskBackedAmqpQueueServer>();
builder.Services.AddSingleton<FunctionExecutor>();
builder.Services.AddSingleton<QueueTriggerDispatcher>();
builder.Services.AddSingleton<IQueueTriggerDispatcher>(sp => sp.GetRequiredService<QueueTriggerDispatcher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<QueueTriggerDispatcher>());

builder.Services.AddHostedService<TempFileCleanupService>();
builder.Services.AddQuartz(o =>
{
    o.UseJobFactory<Quartz.Simpl.MicrosoftDependencyInjectionJobFactory>();
});
builder.Services.AddQuartzHostedService(opt =>
{
    opt.WaitForJobsToComplete = true;
});

var app = builder.Build();

// Add middleware in correct order
app.UseGlobalExceptionHandler();
app.UseCorrelationId();

app.MapDefaultEndpoints();

app.MapOpenApi(); // Built-in OpenApi endpoint

app.UseWebSockets();

var logger = app.Logger;
var maxBlobBytes = app.Configuration.GetValue<long?>("MaxBlobBytes") ?? 100 * 1024 * 1024; // 100MB default

// Ensure the queue service is created at startup
_ = app.Services.GetRequiredService<IAmqpQueueServer>();

static async Task SyncQueueTriggerAsync(BlobInfo blob, IQueueTriggerDispatcher dispatcher, CancellationToken cancellationToken)
{
    var queue = blob.Metadata?.FirstOrDefault(m => string.Equals(m.Name, MetadataKeys.QueueTrigger, StringComparison.OrdinalIgnoreCase))?.Value;
    var functionName = blob.Metadata?.FirstOrDefault(m => string.Equals(m.Name, MetadataKeys.FunctionName, StringComparison.OrdinalIgnoreCase))?.Value;

    if (string.IsNullOrWhiteSpace(queue) || string.IsNullOrWhiteSpace(functionName))
    {
        await dispatcher.DeactivateAsync(blob.Id, cancellationToken).ConfigureAwait(false);
        return;
    }

    await dispatcher.ActivateAsync(blob.Id, functionName.Trim(), queue.Trim(), cancellationToken).ConfigureAwait(false);
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
    .WithOpenApi();

// Queue health and AMQP-like endpoints
app.MapGet("/health", () => Results.Text("OK\n", "text/plain"))
    .WithName("Health")
    .WithTags("Queue");

app.MapGet("/health/queues", (IAmqpQueueServer server) =>
    {
        var stats = server.GetStats();
        var json = JsonSerializer.Serialize(stats, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Results.Content(json, "application/json");
    })
    .WithName("QueueStats")
    .WithTags("Queue")
    .WithDescription("Return current stats for queues.")
    .WithOpenApi();

var amqp = app.MapGroup("/amqp").WithTags("Queue");

amqp.MapGet("/queues", (IAmqpQueueServer server) =>
    {
        var stats = server.GetStats();
        var json = JsonSerializer.Serialize(stats, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Results.Content(json, "application/json");
    })
    .WithName("ListQueues")
    .WithDescription("List queues and stats.")
    .WithOpenApi();

amqp.MapPost("/queues/{name}", (string name, IAmqpQueueServer server) =>
    {
        server.CreateQueue(name);
        return Results.StatusCode(StatusCodes.Status201Created);
    })
    .WithName("CreateQueue")
    .WithDescription("Create a queue if missing.")
    .WithOpenApi();

amqp.MapPost("/queues/{name}/purge", (string name, IAmqpQueueServer server) =>
    {
        server.PurgeQueue(name);
        return Results.Ok();
    })
    .WithName("PurgeQueue")
    .WithDescription("Purge all messages in the queue.")
    .WithOpenApi();

amqp.MapPost("/queues/{name}/messages", async (string name, HttpRequest request, IAmqpQueueServer server, CancellationToken ct) =>
    {
        var elem = await request.ReadFromJsonAsync<JsonElement>(cancellationToken: ct).ConfigureAwait(false);
        if (elem.ValueKind == JsonValueKind.Undefined)
        {
            return Results.BadRequest("Missing or invalid JSON body");
        }
        await server.EnqueueAsync(name, elem, ct).ConfigureAwait(false);
        return Results.Accepted();
    })
    .WithName("EnqueueMessage")
    .WithDescription("Enqueue a JSON message.")
    .WithOpenApi();

amqp.MapPost("/queues/{name}/dequeue", async (string name, int? timeoutMs, IAmqpQueueServer server, CancellationToken ct) =>
    {
        using var linkedCts = timeoutMs is > 0
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, new CancellationTokenSource(timeoutMs.Value).Token)
            : null;
        var token = linkedCts?.Token ?? ct;
        try
        {
            var elem = await server.DequeueAsync<JsonElement>(name, token).ConfigureAwait(false);
            if (elem.ValueKind == JsonValueKind.Undefined || elem.ValueKind == JsonValueKind.Null)
            {
                return Results.NoContent();
            }
            var json = JsonSerializer.Serialize(elem, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Results.Content(json, "application/json");
        }
        catch (OperationCanceledException)
        {
            return Results.NoContent();
        }
    })
    .WithName("DequeueMessage")
    .WithDescription("Dequeue a message, optionally waiting up to timeoutMs.")
    .WithOpenApi();

// List registered functions (blobs with function metadata)
app.MapGet("/api/functions", async (IBlobStorage storage, CancellationToken ct) =>
    {
        var all = await storage.ListAsync(ct);
        var functions = all.Where(b => b.Metadata?.Any(m => string.Equals(m.Name, MetadataKeys.FunctionName, StringComparison.OrdinalIgnoreCase)) == true).ToList();
        var json = JsonSerializer.Serialize(functions, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Results.Content(json, "application/json");
    })
    .WithName("ListFunctions")
    .WithTags("Functions")
    .WithDescription("List all registered functions (identified by 'function.name' metadata).")
    .WithOpenApi();

// Register a function from an existing DLL blob id
app.MapPost("/api/functions/register-from/{dllId}", async (string dllId, HttpRequest request, IBlobStorage storage, ISchedulerFactory schedFactory, CancellationToken ct) =>
    {
        try
        {
            var payload = await request.ReadFromJsonAsync<Dictionary<string, string>>(cancellationToken: ct).ConfigureAwait(false) ?? new();
            payload.TryGetValue("name", out var name);
            if (string.IsNullOrWhiteSpace(name)) return Results.Text("Missing 'name'.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);

            await using var source = await storage.OpenReadAsync(dllId, ct).ConfigureAwait(false);
            if (source is null) return Results.NotFound();
            var info = await storage.GetInfoAsync(dllId, ct);
            var fileName = info?.FileName ?? $"{dllId}.dll";
            var contentType = info?.ContentType ?? "application/octet-stream";

            var tempPath = await FunctionRegistrationHelper.CopyToTempFileAsync(source, ct).ConfigureAwait(false);
            try
            {
                var scheduler = await schedFactory.GetScheduler(ct).ConfigureAwait(false);
                var (result, error) = await FunctionRegistrationHelper.RegisterSingleAsync(
                    tempPath, name, fileName, contentType, storage, scheduler, logger, ct: ct).ConfigureAwait(false);

                if (result is null)
                    return Results.Text(error!, "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);

                var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                return Results.Content(json, "application/json", System.Text.Encoding.UTF8, StatusCodes.Status201Created);
            }
            finally
            {
                FunctionRegistrationHelper.TryDeleteTempFile(tempPath, logger);
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

            if (!string.IsNullOrWhiteSpace(payload.Cron) && !CronHelper.TryParseSchedule(payload.Cron, out _))
                return Results.Text("Invalid NCRONTAB expression.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);

            await using var source = await storage.OpenReadAsync(dllId, ct);
            if (source is null) return Results.NotFound();
            var info = await storage.GetInfoAsync(dllId, ct);
            var fileName = info?.FileName ?? $"{dllId}.dll";
            var contentType = info?.ContentType ?? "application/octet-stream";

            var tempPath = await FunctionRegistrationHelper.CopyToTempFileAsync(source, ct).ConfigureAwait(false);
            try
            {
                var results = new List<BlobInfo>();
                var scheduler = await schedFactory.GetScheduler(ct).ConfigureAwait(false);
                foreach (var name in payload.Names.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var (result, error) = await FunctionRegistrationHelper.RegisterSingleAsync(
                        tempPath, name, fileName, contentType, storage, scheduler, logger,
                        sourceId: dllId, cron: payload.Cron, saveContent: false, ct: ct).ConfigureAwait(false);

                    if (result is null)
                        return Results.Text(error!, "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);

                    results.Add(result);
                    ct.ThrowIfCancellationRequested();
                }
                var json = JsonSerializer.Serialize(results, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                return Results.Content(json, "application/json", System.Text.Encoding.UTF8, StatusCodes.Status201Created);
            }
            finally { FunctionRegistrationHelper.TryDeleteTempFile(tempPath, logger); }
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

// Register multiple functions from uploaded DLL
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
            if (!FunctionRegistrationHelper.IsValidRuntime(runtime))
                return Results.Text("Unsupported runtime. Only '.NET Core' is allowed (e.g., 'dotnet' or 'dotnetcore').", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
            if (!file.FileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                return Results.Text("Entrypoint must be a single .dll file.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);

            var names = namesRaw
                .Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (names.Length == 0)
                return Results.Text("No function names provided in 'names'.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);

            string? cronValue = null;
            if (!string.IsNullOrWhiteSpace(cron))
            {
                if (!CronHelper.TryParseSchedule(cron, out _))
                    return Results.Text("Invalid NCRONTAB expression.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
                cronValue = cron;
            }

            var tempPath = await FunctionRegistrationHelper.CopyToTempFileAsync(file.OpenReadStream(), ct).ConfigureAwait(false);
            try
            {
                var results = new List<BlobInfo>();
                var scheduler = await schedFactory.GetScheduler(ct).ConfigureAwait(false);
                foreach (var name in names)
                {
                    var (result, error) = await FunctionRegistrationHelper.RegisterSingleAsync(
                        tempPath, name, file.FileName, file.ContentType ?? "application/octet-stream",
                        storage, scheduler, logger, cron: cronValue, ct: ct).ConfigureAwait(false);

                    if (result is null)
                        return Results.Text(error!, "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);

                    results.Add(result);
                    ct.ThrowIfCancellationRequested();
                }

                var json = JsonSerializer.Serialize(results, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                return Results.Content(json, "application/json", System.Text.Encoding.UTF8, StatusCodes.Status201Created);
            }
            finally
            {
                FunctionRegistrationHelper.TryDeleteTempFile(tempPath, logger);
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

// Download blob
app.MapGet("/api/blobs/{id}", async (string id, IBlobStorage storage, CancellationToken ct) =>
    {
        var info = await storage.GetInfoAsync(id, ct);
        var sourceId = info?.Metadata?.FirstOrDefault(m => string.Equals(m.Name, MetadataKeys.FunctionSourceId, StringComparison.OrdinalIgnoreCase))?.Value;
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

// Upload blob with size validation
app.MapPost("/api/blobs", async (HttpRequest request, IBlobStorage storage, CancellationToken ct) =>
    {
        if (!request.HasFormContentType)
            return Results.BadRequest("Expected multipart/form-data with a 'file' field.");

        var form = await request.ReadFormAsync(ct);
        var file = form.Files["file"];
        if (file is null) return Results.BadRequest("Missing 'file' field.");

        if (file.Length > maxBlobBytes)
            return Results.Text($"File exceeds maximum allowed size of {maxBlobBytes} bytes.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status413PayloadTooLarge);

        var info = await storage.SaveAsync(file.FileName, file.OpenReadStream(), file.ContentType, ct);
        var json = JsonSerializer.Serialize(info, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Results.Content(json, "application/json", System.Text.Encoding.UTF8, StatusCodes.Status201Created);
    })
    .WithName("UploadBlob")
    .WithTags("Blobs")
    .WithDescription("Upload a new blob via multipart/form-data.")
    .WithOpenApi();

// Replace blob with size validation
app.MapPut("/api/blobs/{id}", async (string id, HttpRequest request, IBlobStorage storage, CancellationToken ct) =>
    {
        if (!request.HasFormContentType)
            return Results.BadRequest("Expected multipart/form-data with a 'file' field.");

        var form = await request.ReadFormAsync(ct);
        var file = form.Files["file"];
        if (file is null) return Results.BadRequest("Missing 'file' field.");

        if (file.Length > maxBlobBytes)
            return Results.Text($"File exceeds maximum allowed size of {maxBlobBytes} bytes.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status413PayloadTooLarge);

        var info = await storage.ReplaceAsync(id, file.OpenReadStream(), file.ContentType, file.FileName, ct);
        if (info is null) return Results.NotFound();
        var json = JsonSerializer.Serialize(info, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Results.Content(json, "application/json");
    })
    .WithName("UpdateBlob")
    .WithTags("Blobs")
    .WithDescription("Replace the content (and filename) of an existing blob.")
    .WithOpenApi();

app.MapPut("/api/blobs/{id}/metadata", async (string id, HttpRequest request, IBlobStorage storage, IQueueTriggerDispatcher dispatcher, CancellationToken ct) =>
    {
        var meta = await request.ReadFromJsonAsync<List<BlobMetadata>>(cancellationToken: ct);
        if (meta is null) return Results.BadRequest("Invalid or missing JSON body.");
        var updated = await storage.SetMetadataAsync(id, meta, ct);
        if (updated is null) return Results.NotFound();
        await SyncQueueTriggerAsync(updated, dispatcher, ct).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(updated, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Results.Content(json, "application/json");
    })
    .WithName("SetBlobMetadata")
    .WithTags("Blobs")
    .WithDescription("Replace metadata entries for the blob with the provided list.")
    .WithOpenApi();

app.MapDelete("/api/blobs/{id}", async (string id, IBlobStorage storage, IQueueTriggerDispatcher dispatcher, CancellationToken ct) =>
    {
        var deleted = await storage.DeleteAsync(id, ct);
        if (deleted)
        {
            await dispatcher.DeactivateAsync(id, ct).ConfigureAwait(false);
            return Results.NoContent();
        }

        return Results.NotFound();
    })
    .WithName("DeleteBlob")
    .WithTags("Blobs")
    .WithDescription("Delete a blob by identifier.")
    .WithOpenApi();

// Register a single function from uploaded DLL
app.MapPost("/api/functions/register", async (HttpRequest request, IBlobStorage storage, ISchedulerFactory schedFactory, CancellationToken ct) =>
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
            if (!FunctionRegistrationHelper.IsValidRuntime(runtime))
                return Results.Text("Unsupported runtime. Only '.NET Core' is allowed (e.g., 'dotnet' or 'dotnetcore').", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
            if (!file.FileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                return Results.Text("Entrypoint must be a single .dll file.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);

            var tempPath = await FunctionRegistrationHelper.CopyToTempFileAsync(file.OpenReadStream(), ct).ConfigureAwait(false);
            try
            {
                var scheduler = await schedFactory.GetScheduler(ct).ConfigureAwait(false);
                var (result, error) = await FunctionRegistrationHelper.RegisterSingleAsync(
                    tempPath, name, file.FileName, file.ContentType ?? "application/octet-stream",
                    storage, scheduler, logger, ct: ct).ConfigureAwait(false);

                if (result is null)
                    return Results.Text(error!, "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);

                var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                return Results.Content(json, "application/json", System.Text.Encoding.UTF8, StatusCodes.Status201Created);
            }
            finally
            {
                FunctionRegistrationHelper.TryDeleteTempFile(tempPath, logger);
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
    .WithOpenApi();

// Schedule a function
app.MapPost("/api/functions/{id}/schedule", async (string id, HttpRequest request, IBlobStorage storage, ISchedulerFactory schedFactory, CancellationToken ct) =>
    {
        try
        {
            var body = await request.ReadFromJsonAsync<Dictionary<string, string>>(cancellationToken: ct);
            var expr = body is not null && (body.TryGetValue("expression", out var e) || body.TryGetValue("ncrontab", out e)) ? e : null;
            if (string.IsNullOrWhiteSpace(expr))
                return Results.Text("Missing JSON body with 'expression' (or 'ncrontab') property.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);

            if (!CronHelper.TryParseSchedule(expr!, out _))
                return Results.Text("Invalid NCRONTAB expression.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);

            var info = await storage.GetInfoAsync(id, ct);
            if (info is null) return Results.NotFound();
            var list = info.Metadata.ToList();
            list.RemoveAll(m => string.Equals(m.Name, MetadataKeys.TimerTrigger, StringComparison.OrdinalIgnoreCase));
            list.Add(new BlobMetadata(MetadataKeys.TimerTrigger, "text/plain", expr!));
            var updated = await storage.SetMetadataAsync(id, list, ct);
            if (updated is null) return Results.NotFound();

            var scheduler = await schedFactory.GetScheduler(ct).ConfigureAwait(false);
            var funcName = updated.Metadata.FirstOrDefault(m => string.Equals(m.Name, MetadataKeys.FunctionName, StringComparison.OrdinalIgnoreCase))?.Value;
            if (!string.IsNullOrWhiteSpace(funcName))
            {
                await FunctionRegistrationHelper.ScheduleFunctionAsync(scheduler, id, funcName!, expr!, logger).ConfigureAwait(false);
            }
            var json = JsonSerializer.Serialize(updated, new JsonSerializerOptions(JsonSerializerDefaults.Web));
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
    .WithOpenApi();

// Unschedule a function
app.MapDelete("/api/functions/{id}/schedule", async (string id, IBlobStorage storage, ISchedulerFactory schedFactory, CancellationToken ct) =>
    {
        var info = await storage.GetInfoAsync(id, ct);
        if (info is null) return Results.NotFound();
        var list = info.Metadata.ToList();
        list.RemoveAll(m => string.Equals(m.Name, MetadataKeys.TimerTrigger, StringComparison.OrdinalIgnoreCase));
        var updated = await storage.SetMetadataAsync(id, list, ct);
        if (updated is null) return Results.NotFound();
        var scheduler = await schedFactory.GetScheduler(ct).ConfigureAwait(false);
        await FunctionRegistrationHelper.UnscheduleFunctionAsync(scheduler, id).ConfigureAwait(false);
        logger.LogInformation("Unscheduled function job for {Id}", id);
        var json = JsonSerializer.Serialize(updated, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Results.Content(json, "application/json");
    })
    .WithName("UnscheduleFunction")
    .WithTags("Functions")
    .WithDescription("Removes the TimerTrigger NCRONTAB expression from a function blob.")
    .WithOpenApi();

// Register and schedule a function in one call
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
                cron = form["expression"].ToString();
                if (string.IsNullOrWhiteSpace(cron)) cron = form["ncrontab"].ToString();
            }

            if (file is null) return Results.Text("Missing 'file' field.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
            if (string.IsNullOrWhiteSpace(name)) return Results.Text("Missing 'name' field.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
            if (string.IsNullOrWhiteSpace(runtime)) return Results.Text("Missing 'runtime' field.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
            if (string.IsNullOrWhiteSpace(cron)) return Results.Text("Missing 'cron' (or 'expression'/'ncrontab') field.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
            if (!FunctionRegistrationHelper.IsValidRuntime(runtime))
                return Results.Text("Unsupported runtime. Only '.NET Core' is allowed (e.g., 'dotnet' or 'dotnetcore').", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
            if (!file.FileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                return Results.Text("Entrypoint must be a single .dll file.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
            if (!CronHelper.TryParseSchedule(cron, out _))
                return Results.Text("Invalid NCRONTAB expression.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);

            var tempPath = await FunctionRegistrationHelper.CopyToTempFileAsync(file.OpenReadStream(), ct).ConfigureAwait(false);
            try
            {
                var scheduler = await schedFactory.GetScheduler(ct).ConfigureAwait(false);
                var (result, error) = await FunctionRegistrationHelper.RegisterSingleAsync(
                    tempPath, name, file.FileName, file.ContentType ?? "application/octet-stream",
                    storage, scheduler, logger, cron: cron, ct: ct).ConfigureAwait(false);

                if (result is null)
                    return Results.Text(error!, "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);

                var json = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                return Results.Content(json, "application/json", System.Text.Encoding.UTF8, StatusCodes.Status201Created);
            }
            finally
            {
                FunctionRegistrationHelper.TryDeleteTempFile(tempPath, logger);
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
    .WithOpenApi();

// Cron preview endpoint
app.MapGet("/api/functions/cron-next", (string expr, int? count) =>
    {
        if (!CronHelper.TryParseSchedule(expr, out var schedule))
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

// Queue trigger management
app.MapPost("/api/functions/{id}/queue-trigger", async (string id, HttpRequest request, IBlobStorage storage, IQueueTriggerDispatcher dispatcher, CancellationToken ct) =>
    {
        var payload = await request.ReadFromJsonAsync<Dictionary<string, string>>(cancellationToken: ct);
        if (payload is null || !payload.TryGetValue("queue", out var queue) || string.IsNullOrWhiteSpace(queue))
        {
            return Results.BadRequest("Body must include non-empty 'queue'.");
        }

        queue = queue.Trim();
        var info = await storage.GetInfoAsync(id, ct);
        if (info is null) return Results.NotFound();

        var functionName = info.Metadata?.FirstOrDefault(m => string.Equals(m.Name, MetadataKeys.FunctionName, StringComparison.OrdinalIgnoreCase))?.Value;
        if (string.IsNullOrWhiteSpace(functionName))
        {
            return Results.Text("Queue triggers require 'function.name' metadata on the blob.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
        }

        var metadata = info.Metadata?.ToList() ?? new List<BlobMetadata>();
        metadata.RemoveAll(m => string.Equals(m.Name, MetadataKeys.QueueTrigger, StringComparison.OrdinalIgnoreCase));
        metadata.Add(new BlobMetadata(MetadataKeys.QueueTrigger, "text/plain", queue));

        var updated = await storage.SetMetadataAsync(id, metadata, ct);
        if (updated is null) return Results.NotFound();

        await SyncQueueTriggerAsync(updated, dispatcher, ct).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(updated, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Results.Content(json, "application/json");
    })
    .WithName("SetQueueTrigger")
    .WithTags("Functions")
    .WithDescription("Sets the QueueTrigger queue name on a function blob.")
    .WithOpenApi();

app.MapDelete("/api/functions/{id}/queue-trigger", async (string id, IBlobStorage storage, IQueueTriggerDispatcher dispatcher, CancellationToken ct) =>
    {
        var info = await storage.GetInfoAsync(id, ct);
        if (info is null) return Results.NotFound();

        var metadata = info.Metadata?.ToList() ?? new List<BlobMetadata>();
        metadata.RemoveAll(m => string.Equals(m.Name, MetadataKeys.QueueTrigger, StringComparison.OrdinalIgnoreCase));
        var updated = await storage.SetMetadataAsync(id, metadata, ct);
        if (updated is null) return Results.NotFound();

        await SyncQueueTriggerAsync(updated, dispatcher, ct).ConfigureAwait(false);
        var json = JsonSerializer.Serialize(updated, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return Results.Content(json, "application/json");
    })
    .WithName("ClearQueueTrigger")
    .WithTags("Functions")
    .WithDescription("Removes the QueueTrigger queue binding from a function blob.")
    .WithOpenApi();

// Bulk schedule all functions referencing a given source DLL id
app.MapPost("/api/functions/schedule-all", async (HttpRequest request, IBlobStorage storage, ISchedulerFactory schedFactory, CancellationToken ct) =>
    {
        var payload = await request.ReadFromJsonAsync<Dictionary<string, string>>(cancellationToken: ct).ConfigureAwait(false) ?? new();
        if (!payload.TryGetValue("sourceId", out var sourceId) || string.IsNullOrWhiteSpace(sourceId))
            return Results.Text("Missing 'sourceId'.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
        if (!payload.TryGetValue("cron", out var cron) || string.IsNullOrWhiteSpace(cron))
            return Results.Text("Missing 'cron'.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
        if (!CronHelper.TryParseSchedule(cron, out _))
            return Results.Text("Invalid NCRONTAB expression.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);

        var all = await storage.ListAsync(ct).ConfigureAwait(false);
        var funcs = all.Where(b => b.Metadata?.Any(m => string.Equals(m.Name, MetadataKeys.FunctionSourceId, StringComparison.OrdinalIgnoreCase) && string.Equals(m.Value, sourceId, StringComparison.OrdinalIgnoreCase)) == true).ToList();
        var count = 0;
        var scheduler = await schedFactory.GetScheduler(ct).ConfigureAwait(false);
        foreach (var f in funcs)
        {
            var meta = f.Metadata != null ? f.Metadata.ToList() : new List<BlobMetadata>();
            meta = meta.Where(m => !string.Equals(m.Name, MetadataKeys.TimerTrigger, StringComparison.OrdinalIgnoreCase)).ToList();
            meta.Add(new BlobMetadata(MetadataKeys.TimerTrigger, "text/plain", cron));
            await storage.SetMetadataAsync(f.Id, meta, ct).ConfigureAwait(false);
            var name = meta.FirstOrDefault(m => string.Equals(m.Name, MetadataKeys.FunctionName, StringComparison.OrdinalIgnoreCase))?.Value
                ?? f.Metadata?.FirstOrDefault(m => string.Equals(m.Name, MetadataKeys.FunctionName, StringComparison.OrdinalIgnoreCase))?.Value
                ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(name))
            {
                await FunctionRegistrationHelper.ScheduleFunctionAsync(scheduler, f.Id, name, cron, logger).ConfigureAwait(false);
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
        var payload = await request.ReadFromJsonAsync<Dictionary<string, string>>(cancellationToken: ct).ConfigureAwait(false) ?? new();
        if (!payload.TryGetValue("sourceId", out var sourceId) || string.IsNullOrWhiteSpace(sourceId))
            return Results.Text("Missing 'sourceId'.", "text/plain", System.Text.Encoding.UTF8, StatusCodes.Status400BadRequest);
        var all = await storage.ListAsync(ct).ConfigureAwait(false);
        var funcs = all.Where(b => b.Metadata?.Any(m => string.Equals(m.Name, MetadataKeys.FunctionSourceId, StringComparison.OrdinalIgnoreCase) && string.Equals(m.Value, sourceId, StringComparison.OrdinalIgnoreCase)) == true).ToList();
        var count = 0;
        var scheduler = await schedFactory.GetScheduler(ct).ConfigureAwait(false);
        foreach (var f in funcs)
        {
            var meta = f.Metadata != null ? f.Metadata.ToList() : new List<BlobMetadata>();
            meta = meta.Where(m => !string.Equals(m.Name, MetadataKeys.TimerTrigger, StringComparison.OrdinalIgnoreCase)).ToList();
            await storage.SetMetadataAsync(f.Id, meta, ct).ConfigureAwait(false);
            await FunctionRegistrationHelper.UnscheduleFunctionAsync(scheduler, f.Id).ConfigureAwait(false);
            count++;
            ct.ThrowIfCancellationRequested();
        }
        return Results.Json(new { count });
    })
    .WithName("UnscheduleAllFromSource")
    .WithTags("Functions")
    .WithDescription("Remove the TimerTrigger cron on all functions that reference the given source DLL id.")
    .WithOpenApi();

// Initialize Quartz schedules from persisted metadata at startup (can be disabled for tests)
var disableQuartz = string.Equals(Environment.GetEnvironmentVariable("DISABLE_QUARTZ"), "1", StringComparison.OrdinalIgnoreCase)
                    || app.Configuration.GetValue<bool>("DisableQuartz");
if (!disableQuartz)
{
    using var scope = app.Services.CreateScope();
    var storage = scope.ServiceProvider.GetRequiredService<IBlobStorage>();
    var schedulerFactory = scope.ServiceProvider.GetRequiredService<ISchedulerFactory>();
    var scheduler = await schedulerFactory.GetScheduler().ConfigureAwait(false);
    var blobs = await storage.ListAsync().ConfigureAwait(false);
    foreach (var b in blobs)
    {
        var name = b.Metadata.FirstOrDefault(m => string.Equals(m.Name, MetadataKeys.FunctionName, StringComparison.OrdinalIgnoreCase))?.Value;
        var cron = b.Metadata.FirstOrDefault(m => string.Equals(m.Name, MetadataKeys.TimerTrigger, StringComparison.OrdinalIgnoreCase))?.Value;
        if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(cron))
        {
            await FunctionRegistrationHelper.ScheduleFunctionAsync(scheduler, b.Id, name!, cron!, logger).ConfigureAwait(false);
        }
    }
}

await app.RunAsync(default);
